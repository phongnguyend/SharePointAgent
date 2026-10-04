using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace DocumentParsers;

public sealed class DocxDocumentParser : IDocxDocumentParser
{
    private readonly ParserOptions _options;

    public DocxDocumentParser(ParserOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
    }

    public async Task<DocxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = await ParserInput.ReadAsync(stream, _options, true, cancellationToken);
        using var document = WordprocessingDocument.Open(buffer, false, ParserInput.Settings(_options));
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing DOCX main part.");
        var body = main.Document.Body ?? throw new InvalidDataException("Missing DOCX body.");
        var result = new DocxParseResult();
        var images = new ImageReader(_options);
        var formatting = new DocxFormatting(main, result.Warnings);
        foreach (var child in body.ChildElements)
        {
            Visit(child);
        }
        return result;

        void Visit(OpenXmlElement element)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is W.Paragraph paragraph)
            {
                ParseParagraph(paragraph);
            }
            else if (element is W.Table table)
            {
                var rows = table.Elements<W.TableRow>().Select(row => row.Elements<W.TableCell>()
                    .Select(cell => string.Join("\n", cell.Descendants<W.Paragraph>().Select(FormatCellParagraph))).ToArray()).ToArray();
                if (rows.Sum(row => (long)row.Length) > _options.MaxTableCells)
                {
                    throw new InvalidDataException("DOCX table exceeds the cell limit.");
                }
                Add(new TableElement(Markdown.Table(rows)));
                foreach (var drawing in table.Descendants<W.Drawing>())
                {
                    ParseDrawing(drawing);
                }
                if (table.Descendants<W.Drawing>().Any() || table.Descendants<W.Table>().Any())
                {
                    result.Warnings.Add(new("FlattenedTable", "Nested table content is flattened; cell images follow the table."));
                }
            }
            else if (element is W.SdtBlock)
            {
                foreach (var child in element.ChildElements)
                {
                    Visit(child);
                }
            }
            else if (element is W.SdtContentBlock)
            {
                foreach (var child in element.ChildElements)
                {
                    Visit(child);
                }
            }
        }

        void Add(DocumentElement element) => result.BodyElements.Add(element with { Order = result.BodyElements.Count });

        void ParseParagraph(W.Paragraph paragraph)
        {
            var level = HeadingLevel(paragraph, main);
            var text = new DocxInlineText();
            var listPrefix = formatting.ListPrefix(paragraph);
            var prefix = level > 0 ? string.Empty : listPrefix;
            var firstSegment = true;
            Walk(paragraph);
            Flush();

            void Flush()
            {
                var value = text.Take();
                if (value.Length > 0)
                {
                    var continuation = new string(' ', prefix.Length);
                    value = (firstSegment ? prefix : continuation) + value.Replace("\n", "\n" + continuation);
                    Add(level > 0 ? new HeadingElement(value, level) : new TextElement(value));
                    firstSegment = false;
                }
            }

            void Walk(OpenXmlElement node, bool bold = false)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (node)
                {
                    case W.Text value:
                        text.Append(value.Text, bold);
                        return;
                    case W.TabChar:
                        text.Append("\t", bold);
                        return;
                    case W.Break:
                    case W.CarriageReturn:
                        text.Append("\n", bold);
                        return;
                    case W.Drawing drawing:
                        Flush();
                        ParseDrawing(drawing);
                        return;
                }
                if (node is W.Run run)
                {
                    bold = level == 0 && formatting.IsBold(run, paragraph);
                }
                foreach (var child in node.ChildElements)
                {
                    Walk(child, bold);
                }
            }
        }

        string FormatCellParagraph(W.Paragraph paragraph)
        {
            var text = new DocxInlineText();
            foreach (var run in paragraph.Descendants<W.Run>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bold = formatting.IsBold(run, paragraph);
                foreach (var node in run.ChildElements)
                {
                    switch (node)
                    {
                        case W.Text value:
                            text.Append(value.Text, bold);
                            break;
                        case W.TabChar:
                            text.Append("\t", bold);
                            break;
                        case W.Break:
                        case W.CarriageReturn:
                            text.Append("\n", bold);
                            break;
                    }
                }
            }
            return formatting.ListPrefix(paragraph) + text.Take();
        }

        void ParseDrawing(W.Drawing drawing)
        {
            var properties = drawing.Descendants<DW.DocProperties>().FirstOrDefault();
            var blips = drawing.Descendants<A.Blip>().ToArray();
            foreach (var blip in blips)
            {
                var image = images.FromPart(main, blip.Embed?.Value, result.Warnings, cancellationToken);
                if (image is not null)
                {
                    Add(image with { AltText = properties?.Description?.Value ?? properties?.Title?.Value });
                }
            }
            if (blips.Length == 0)
            {
                result.Warnings.Add(new("UnsupportedDrawing", "A drawing has no extractable image; charts and text boxes may be omitted."));
            }
        }
    }

    public string ConvertToMarkdown(DocxParseResult result, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        return Markdown.Elements(result.BodyElements, cancellationToken, skipImages);
    }

    private static int HeadingLevel(W.Paragraph paragraph, MainDocumentPart main)
    {
        var outline = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (outline is >= 0 and < 6)
        {
            return outline.Value + 1;
        }
        var id = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var visited = new HashSet<string>();
        while (id is not null && visited.Add(id))
        {
            var style = main.StyleDefinitionsPart?.Styles?.Elements<W.Style>().FirstOrDefault(s => s.StyleId?.Value == id);
            var name = style?.StyleName?.Val?.Value ?? id;
            var compact = name.Replace(" ", string.Empty);
            if (compact.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(compact[7..], out var level))
            {
                return Math.Clamp(level, 1, 6);
            }
            var inheritedOutline = style?.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (inheritedOutline is >= 0 and < 6)
            {
                return inheritedOutline.Value + 1;
            }
            id = style?.BasedOn?.Val?.Value;
        }
        return 0;
    }
}
