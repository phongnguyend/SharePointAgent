using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace DocumentParsers;

/// <summary>Heuristic layout analysis for horizontal, left-to-right PDF content.</summary>
internal sealed class LayoutAnalyzer(PdfParserOptions options)
{
    private sealed record LayoutWord(string Text, DocumentBoundingBox Box, double FontSize);

    private sealed record Line(IReadOnlyList<LayoutWord> Words, DocumentBoundingBox Box)
    {
        public string Text => string.Join(" ", Words.Select(word => word.Text));

        public double FontSize => Words.Average(word => word.FontSize);
    }

    private static readonly Regex ListMarker = new(@"^(?:[•●▪◦‣\-–*]|\d+[.)])\s+", RegexOptions.CultureInvariant);

    internal IReadOnlyList<DocumentElement> Analyze(IEnumerable<Word> source, IEnumerable<ImageElement> images,
        double pageHeight, int pageNumber, CancellationToken token)
    {
        var words = new List<LayoutWord>();
        foreach (var word in source)
        {
            token.ThrowIfCancellationRequested();
            words.Add(new(word.Text, Normalize(word.BoundingBox, pageHeight),
                word.Letters.Count == 0 ? word.BoundingBox.Height : word.Letters.Average(letter => letter.FontSize)));
        }
        var bodySize = words.Count == 0 ? 12 : words.GroupBy(word => Math.Round(word.FontSize))
            .OrderByDescending(group => group.Sum(word => word.Text.Length)).ThenBy(group => group.Key).First().Key;
        var rows = BuildLines(words, token);
        var elements = new List<DocumentElement>();
        var remaining = DetectTables(rows, elements, pageNumber, token);
        var lines = remaining.SelectMany(SplitCells).ToList();
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            var text = line.Text;
            DocumentElement element;
            if (ListMarker.IsMatch(text))
            {
                var marker = ListMarker.Match(text).Value.Trim();
                var prefix = char.IsDigit(marker[0]) ? marker.TrimEnd('.', ')') + ". " : "- ";
                element = new TextElement(prefix + text[ListMarker.Match(text).Length..]);
            }
            else if (line.FontSize >= bodySize * 1.2 && line.Words.Count <= 20)
            {
                element = new HeadingElement(text, line.FontSize >= bodySize * 1.6 ? 1 : 2);
            }
            else
            {
                element = new TextElement(text);
            }
            elements.Add(element with { PageNumber = pageNumber, BoundingBox = line.Box });
        }
        elements.AddRange(images);
        return MergeParagraphs(Order(elements, token, options.PdfReadingOrder), bodySize, token);
    }

    // PdfPig resolves PDF transformations; convert its bottom-left geometry to top-left points.
    internal static DocumentBoundingBox Normalize(PdfRectangle rectangle, double height)
    {
        var points = new[] { rectangle.TopLeft, rectangle.TopRight, rectangle.BottomLeft, rectangle.BottomRight };
        var left = points.Min(point => point.X);
        var right = points.Max(point => point.X);
        var top = points.Max(point => point.Y);
        var bottom = points.Min(point => point.Y);
        return new(left, height - top, right - left, top - bottom);
    }

    private static List<Line> BuildLines(List<LayoutWord> words, CancellationToken token)
    {
        var rows = new List<List<LayoutWord>>();
        foreach (var word in words.OrderBy(word => word.Box.Y).ThenBy(word => word.Box.X))
        {
            token.ThrowIfCancellationRequested();
            var row = rows.LastOrDefault(candidate =>
                Math.Abs(candidate[0].Box.Y + candidate[0].Box.Height / 2 - word.Box.Y - word.Box.Height / 2)
                    <= Math.Max(2, Math.Min(candidate[0].FontSize, word.FontSize) * 0.35));
            if (row is null)
            {
                rows.Add([word]);
            }
            else
            {
                row.Add(word);
            }
        }
        return rows.Select(row => MakeLine(row.OrderBy(word => word.Box.X).ToArray())).OrderBy(line => line.Box.Y).ToList();
    }

    private static Line MakeLine(IReadOnlyList<LayoutWord> words) => new(words, Union(words.Select(word => word.Box)));

    private static IEnumerable<Line> SplitCells(Line row)
    {
        var cell = new List<LayoutWord>();
        foreach (var word in row.Words)
        {
            if (cell.Count > 0 && word.Box.X - Right(cell[^1].Box) > Math.Max(18, row.FontSize * 2))
            {
                yield return MakeLine(cell.ToArray());
                cell.Clear();
            }
            cell.Add(word);
        }
        if (cell.Count > 0)
        {
            yield return MakeLine(cell.ToArray());
        }
    }

    private List<Line> DetectTables(List<Line> rows, List<DocumentElement> elements, int page, CancellationToken token)
    {
        var remaining = new List<Line>();
        for (var i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var first = SplitCells(rows[i]).ToArray();
            var candidate = new List<Line[]> { first };
            var end = i + 1;
            if (first.Length >= 2)
            {
                while (end < rows.Count)
                {
                    token.ThrowIfCancellationRequested();
                    var cells = SplitCells(rows[end]).ToArray();
                    if (cells.Length != first.Length || rows[end].Box.Y - Bottom(rows[end - 1].Box) > rows[i].FontSize * 2
                        || cells.Where((cell, index) => Math.Abs(cell.Box.X - first[index].Box.X) > 5).Any())
                    {
                        break;
                    }
                    candidate.Add(cells);
                    end++;
                }
            }
            // Repeated alignment alone also describes columns. Require short cells and
            // numeric data in a majority of rows before promoting a candidate to a table.
            var numericRows = candidate.Skip(1).Count(row => row.Skip(1).Any(cell =>
                double.TryParse(cell.Text, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out _)));
            if (candidate.Count >= 3 && numericRows >= (candidate.Count - 1) / 2.0
                && candidate.All(row => row.All(cell => cell.Words.Count <= 8 && !ListMarker.IsMatch(cell.Text))))
            {
                if ((long)candidate.Count * first.Length > options.MaxTableCells)
                {
                    throw new InvalidDataException("PDF table exceeds the cell limit.");
                }
                elements.Add(new TableElement(Markdown.Table(candidate.Select(row => row.Select(cell => cell.Text))))
                {
                    PageNumber = page,
                    BoundingBox = Union(candidate.SelectMany(row => row).Select(cell => cell.Box))
                });
                i = end - 1;
            }
            else
            {
                remaining.Add(rows[i]);
            }
        }
        return remaining;
    }

    // Resolve regions before lines unless the caller explicitly requests row order.
    internal static List<DocumentElement> Order(List<DocumentElement> elements, CancellationToken token,
        PdfReadingOrder mode = PdfReadingOrder.LayoutAware)
    {
        token.ThrowIfCancellationRequested();
        if (mode == PdfReadingOrder.RowBased)
        {
            return OrderRows(elements, token);
        }
        var positioned = elements.Where(element => element.BoundingBox is not null).ToList();
        var ordered = OrderRegions(positioned, token, 0);
        ordered.AddRange(elements.Where(element => element.BoundingBox is null));
        return ordered;
    }

    private static List<DocumentElement> OrderRegions(List<DocumentElement> elements, CancellationToken token, int depth)
    {
        token.ThrowIfCancellationRequested();
        if (elements.Count < 3 || depth >= 64)
        {
            return OrderRows(elements, token);
        }
        // A continuous gutter separates columns. Require repeated content on both
        // sides and vertical overlap so a staggered pair of blocks is not a column.
        var byX = elements.OrderBy(element => element.BoundingBox!.X).ToList();
        var right = Right(byX[0].BoundingBox!);
        var bestGap = 18.0;
        var cut = -1;
        for (var i = 1; i < byX.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var gap = byX[i].BoundingBox!.X - right;
            if (i >= 2 && byX.Count - i >= 2 && gap > bestGap)
            {
                var leftBounds = Union(byX.Take(i).Select(element => element.BoundingBox!));
                var rightBounds = Union(byX.Skip(i).Select(element => element.BoundingBox!));
                if (Math.Min(Bottom(leftBounds), Bottom(rightBounds)) > Math.Max(leftBounds.Y, rightBounds.Y))
                {
                    bestGap = gap;
                    cut = i;
                }
            }
            right = Math.Max(right, Right(byX[i].BoundingBox!));
        }
        if (cut > 0)
        {
            return JoinRegions(byX, cut, token, depth);
        }
        // Spanning content closes a gutter. Split at horizontal whitespace first,
        // then search each section independently for columns.
        var byY = elements.OrderBy(element => element.BoundingBox!.Y).ToList();
        var regionWidth = Union(elements.Select(element => element.BoundingBox!)).Width;
        var bottom = Bottom(byY[0].BoundingBox!);
        bestGap = 2;
        var spanningCut = false;
        cut = -1;
        for (var i = 1; i < byY.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var gap = byY[i].BoundingBox!.Y - bottom;
            var nearSpanningContent = byY[i - 1].BoundingBox!.Width >= regionWidth * 0.65
                || byY[i].BoundingBox!.Width >= regionWidth * 0.65;
            if (gap > 2 && ((!spanningCut && nearSpanningContent)
                || nearSpanningContent == spanningCut && gap > bestGap))
            {
                bestGap = gap;
                cut = i;
                spanningCut = nearSpanningContent;
            }
            bottom = Math.Max(bottom, Bottom(byY[i].BoundingBox!));
        }
        return cut > 0 ? JoinRegions(byY, cut, token, depth) : OrderRows(elements, token);
    }

    private static List<DocumentElement> JoinRegions(List<DocumentElement> sorted, int cut, CancellationToken token, int depth)
    {
        var result = OrderRegions(sorted.GetRange(0, cut), token, depth + 1);
        result.AddRange(OrderRegions(sorted.GetRange(cut, sorted.Count - cut), token, depth + 1));
        return result;
    }

    private static List<DocumentElement> OrderRows(List<DocumentElement> elements, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Anchor each row to its first top edge so offsets cannot chain rows together.
        const double rowTolerance = 2;
        var positioned = elements.Where(element => element.BoundingBox is not null)
            .OrderBy(element => element.BoundingBox!.Y).ThenBy(element => element.BoundingBox!.X).ToList();
        var ordered = new List<DocumentElement>(elements.Count);
        for (var start = 0; start < positioned.Count;)
        {
            token.ThrowIfCancellationRequested();
            var top = positioned[start].BoundingBox!.Y;
            var end = start + 1;
            while (end < positioned.Count && positioned[end].BoundingBox!.Y - top <= rowTolerance)
            {
                token.ThrowIfCancellationRequested();
                end++;
            }
            ordered.AddRange(positioned.GetRange(start, end - start).OrderBy(element => element.BoundingBox!.X));
            start = end;
        }
        // OCR may omit geometry; retain such elements in source order after positioned content.
        ordered.AddRange(elements.Where(element => element.BoundingBox is null));
        return ordered;
    }

    private static IReadOnlyList<DocumentElement> MergeParagraphs(List<DocumentElement> ordered, double bodySize, CancellationToken token)
    {
        var result = new List<DocumentElement>();
        foreach (var element in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (result.LastOrDefault() is TextElement previous && element is TextElement current
                && !ListMarker.IsMatch(previous.Text) && !ListMarker.IsMatch(current.Text)
                && previous.BoundingBox is { } a && current.BoundingBox is { } b
                && Math.Abs(a.X - b.X) <= bodySize && b.Y >= Bottom(a) - 1 && b.Y - Bottom(a) <= bodySize * 0.9)
            {
                result[^1] = previous with { Text = previous.Text + "\n" + current.Text, BoundingBox = Union([a, b]) };
            }
            else
            {
                result.Add(element);
            }
        }
        return result;
    }

    private static double Right(DocumentBoundingBox box) => box.X + box.Width;

    private static double Bottom(DocumentBoundingBox box) => box.Y + box.Height;

    private static DocumentBoundingBox Union(IEnumerable<DocumentBoundingBox> boxes)
    {
        var values = boxes.ToArray();
        var x = values.Min(box => box.X);
        var y = values.Min(box => box.Y);
        return new(x, y, values.Max(Right) - x, values.Max(Bottom) - y);
    }
}
