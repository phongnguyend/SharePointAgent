using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;

namespace DocumentParsers;

public sealed class PdfDocumentParser : IPdfDocumentParser
{
    private readonly DocumentIntelligenceClient _client;
    private readonly ParserOptions _options;

    public PdfDocumentParser(DocumentIntelligenceClient client, ParserOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new();
        _options.Validate();
    }

    public async Task<PdfParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = await ParserInput.ReadAsync(stream, _options, false, cancellationToken);
        if (buffer.Length < 5 || !buffer.GetBuffer().AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException("Input is not a PDF document.");
        }
        var request = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(buffer.ToArray()));
        request.Output.Add(AnalyzeOutputOption.Figures);
        var operation = await _client.AnalyzeDocumentAsync(WaitUntil.Started, request, cancellationToken);
        // Capture the documented result URL before polling replaces the raw response.
        operation.GetRawResponse().Headers.TryGetValue("Operation-Location", out var location);
        var resultId = location is not null && Uri.TryCreate(location, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.TrimEnd('/').Split('/')[^1] : null;
        var completed = await operation.WaitForCompletionAsync(cancellationToken);
        var analysis = completed.Value;
        var result = Map(analysis, cancellationToken);
        var images = new ImageReader(_options);
        foreach (var figure in analysis.Figures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(resultId) || string.IsNullOrEmpty(figure.Id))
            {
                result.Warnings.Add(new("FigureUnavailable", "Figure result ID or figure ID is missing."));
                continue;
            }
            try
            {
                var response = await _client.GetAnalyzeResultFigureAsync(analysis.ModelId, resultId, figure.Id, cancellationToken);
                using var imageStream = response.Value.ToStream();
                var element = new ImageElement
                {
                    Data = images.Read(imageStream, cancellationToken),
                    ContentType = "image/png",
                    Caption = figure.Caption?.Content
                };
                result.Elements.Add(Locate(element, figure.Spans, figure.BoundingRegions));
            }
            catch (RequestFailedException)
            {
                result.Warnings.Add(new("FigureDownloadFailed", "A PDF figure could not be retrieved."));
            }
        }
        result.Elements.Sort((left, right) => Nullable.Compare(left.Order, right.Order));
        return result;
    }

    internal PdfParseResult Map(AnalyzeResult analysis, CancellationToken token)
    {
        var result = new PdfParseResult { PageCount = analysis.Pages.Count };
        result.Metadata["ModelId"] = analysis.ModelId;
        foreach (var warning in analysis.Warnings)
        {
            result.Warnings.Add(new(warning.Code, warning.Message));
        }
        foreach (var page in analysis.Pages)
        {
            result.Metadata[$"Page{page.PageNumber}.Unit"] = page.Unit.ToString() ?? "unknown";
        }
        foreach (var paragraph in analysis.Paragraphs)
        {
            token.ThrowIfCancellationRequested();
            if (paragraph.Spans.Count > 0 && paragraph.Spans.All(span => analysis.Tables.Any(table =>
                table.Spans.Any(tableSpan => span.Offset >= tableSpan.Offset && (long)span.Offset + span.Length <= (long)tableSpan.Offset + tableSpan.Length))))
            {
                continue;
            }
            DocumentElement element = paragraph.Role == ParagraphRole.Title ? new HeadingElement(paragraph.Content, 1)
                : paragraph.Role == ParagraphRole.SectionHeading ? new HeadingElement(paragraph.Content, 2)
                : new TextElement(paragraph.Content);
            result.Elements.Add(Locate(element, paragraph.Spans, paragraph.BoundingRegions));
        }
        foreach (var table in analysis.Tables)
        {
            token.ThrowIfCancellationRequested();
            if (table.RowCount < 0 || table.ColumnCount < 0 || (long)table.RowCount * table.ColumnCount > _options.MaxTableCells)
            {
                throw new InvalidDataException("PDF table exceeds the cell limit.");
            }
            var rows = Enumerable.Range(0, table.RowCount).Select(_ => Enumerable.Repeat(string.Empty, table.ColumnCount).ToArray()).ToArray();
            foreach (var cell in table.Cells)
            {
                token.ThrowIfCancellationRequested();
                if (cell.RowIndex < 0 || cell.RowIndex >= rows.Length || cell.ColumnIndex < 0 || cell.ColumnIndex >= table.ColumnCount)
                {
                    result.Warnings.Add(new("MalformedTable", "A table cell is outside the declared dimensions."));
                    continue;
                }
                rows[cell.RowIndex][cell.ColumnIndex] = cell.Content;
            }
            result.Elements.Add(Locate(new TableElement(Markdown.Table(rows)), table.Spans, table.BoundingRegions));
        }
        result.Elements.Sort((left, right) => Nullable.Compare(left.Order, right.Order));
        return result;
    }

    private static DocumentElement Locate(DocumentElement element, IEnumerable<DocumentSpan> spans, IEnumerable<BoundingRegion> regions)
    {
        BoundingRegion? region = regions.Select(value => (BoundingRegion?)value).FirstOrDefault();
        DocumentBoundingBox? box = null;
        if (region is { } bounds && bounds.Polygon.Count >= 4 && bounds.Polygon.Count % 2 == 0)
        {
            var xs = bounds.Polygon.Where((_, index) => index % 2 == 0).ToArray();
            var ys = bounds.Polygon.Where((_, index) => index % 2 != 0).ToArray();
            box = new(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
        }
        return element with
        {
            Order = spans.Select(span => (long)span.Offset).DefaultIfEmpty(long.MaxValue).Min(),
            PageNumber = region?.PageNumber,
            BoundingBox = box
        };
    }

    public string ConvertToMarkdown(PdfParseResult result, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        var output = new StringBuilder();
        int? page = null;
        foreach (var element in result.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipImages && element is ImageElement)
            {
                continue;
            }
            if (element.PageNumber is not null && element.PageNumber != page)
            {
                page = element.PageNumber;
                output.AppendLine($"<!-- Page {page} -->").AppendLine();
            }
            output.AppendLine(Markdown.Elements([element], cancellationToken, skipImages)).AppendLine();
        }
        return output.ToString().TrimEnd();
    }
}
