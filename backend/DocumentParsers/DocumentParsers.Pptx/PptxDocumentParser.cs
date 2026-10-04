using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace DocumentParsers;

public sealed class PptxDocumentParser : IPptxDocumentParser
{
    private readonly ParserOptions _options;
    private readonly IReadingOrderResolver _readingOrder;

    public PptxDocumentParser(ParserOptions? options = null, IReadingOrderResolver? readingOrder = null)
    {
        _options = options ?? new();
        _options.Validate();
        _readingOrder = readingOrder ?? new PositionReadingOrderResolver();
    }

    public async Task<PptxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = await ParserInput.ReadAsync(stream, _options, true, cancellationToken);
        using var document = PresentationDocument.Open(buffer, false, ParserInput.Settings(_options));
        var main = document.PresentationPart ?? throw new InvalidDataException("Missing presentation part.");
        var result = new PptxParseResult();
        var images = new ImageReader(_options);
        foreach (var id in main.Presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = main.GetPartById(id.RelationshipId?.Value ?? throw new InvalidDataException("Missing slide relationship.")) as SlidePart
                ?? throw new InvalidDataException("Invalid slide relationship.");
            var number = result.Slides.Count + 1;
            var elements = new List<DocumentElement>();
            foreach (var child in part.Slide.CommonSlideData?.ShapeTree?.ChildElements ?? [])
            {
                Visit(child, 1, 1, 0, 0);
            }
            result.Slides.Add(new(number, _readingOrder.Resolve(elements).Select((element, order) => element with { Order = order }).ToArray())
            {
                Notes = ExtractNotes(part, cancellationToken)
            });

            void Visit(OpenXmlElement shape, double sx, double sy, double tx, double ty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (shape is P.GroupShape group)
                {
                    var transform = group.GroupShapeProperties?.TransformGroup;
                    var gx = transform?.Offset?.X?.Value ?? 0;
                    var gy = transform?.Offset?.Y?.Value ?? 0;
                    var cx = transform?.ChildOffset?.X?.Value ?? 0;
                    var cy = transform?.ChildOffset?.Y?.Value ?? 0;
                    var childWidth = transform?.ChildExtents?.Cx?.Value ?? 0;
                    var childHeight = transform?.ChildExtents?.Cy?.Value ?? 0;
                    var scaleX = childWidth == 0 ? 1 : (double)(transform?.Extents?.Cx?.Value ?? childWidth) / childWidth;
                    var scaleY = childHeight == 0 ? 1 : (double)(transform?.Extents?.Cy?.Value ?? childHeight) / childHeight;
                    foreach (var child in group.ChildElements)
                    {
                        Visit(child, sx * scaleX, sy * scaleY, tx + sx * (gx - cx * scaleX), ty + sy * (gy - cy * scaleY));
                    }
                    if (transform?.Rotation is not null || transform?.HorizontalFlip?.Value == true || transform?.VerticalFlip?.Value == true)
                    {
                        result.Warnings.Add(new("GroupTransform", "Rotated/flipped group reading order is approximate."));
                    }
                    return;
                }
                var offset = shape.Descendants<A.Offset>().FirstOrDefault();
                var extents = shape.Descendants<A.Extents>().FirstOrDefault();
                var frameTransform = (shape as P.GraphicFrame)?.Transform;
                var x = frameTransform?.Offset?.X?.Value ?? offset?.X?.Value ?? 0;
                var y = frameTransform?.Offset?.Y?.Value ?? offset?.Y?.Value ?? 0;
                var width = frameTransform?.Extents?.Cx?.Value ?? extents?.Cx?.Value ?? 0;
                var height = frameTransform?.Extents?.Cy?.Value ?? extents?.Cy?.Value ?? 0;
                var box = new DocumentBoundingBox(tx + x * sx, ty + y * sy, width * sx, height * sy);
                switch (shape)
                {
                    case P.Shape textShape:
                        var text = PptxListText.Extract(textShape, part, main, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var type = textShape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
                            DocumentElement element = type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle
                                ? new HeadingElement(text, 1) : new TextElement(text);
                            elements.Add(element with { PageNumber = number, BoundingBox = box });
                        }
                        break;
                    case P.Picture picture:
                        var image = images.FromPart(part, picture.BlipFill?.Blip?.Embed?.Value, result.Warnings, cancellationToken);
                        if (image is not null)
                        {
                            elements.Add(image with
                            {
                                AltText = picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Description?.Value,
                                PageNumber = number,
                                BoundingBox = box
                            });
                        }
                        break;
                    case P.GraphicFrame frame:
                        var table = frame.Descendants<A.Table>().FirstOrDefault();
                        if (table is not null)
                        {
                            var rows = table.Elements<A.TableRow>().Select(row => row.Elements<A.TableCell>()
                                .Select(cell => string.Join("\n", cell.Descendants<A.Paragraph>().Select(ParagraphText))).ToArray()).ToArray();
                            if (rows.Sum(row => (long)row.Length) > _options.MaxTableCells)
                            {
                                throw new InvalidDataException("PPTX table exceeds the cell limit.");
                            }
                            elements.Add(new TableElement(Markdown.Table(rows)) { PageNumber = number, BoundingBox = box });
                        }
                        else
                        {
                            result.Warnings.Add(new("UnsupportedGraphic", $"Slide {number} contains an unsupported chart or SmartArt graphic."));
                        }
                        break;
                }
            }
        }
        return result;
    }

    private static string? ExtractNotes(SlidePart slide, CancellationToken token)
    {
        var paragraphs = new List<string>();
        foreach (var shape in slide.NotesSlidePart?.NotesSlide.CommonSlideData?.ShapeTree?.Descendants<P.Shape>() ?? [])
        {
            token.ThrowIfCancellationRequested();
            var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
            // Notes pages also contain slide numbers, dates, headers and slide thumbnails.
            if (placeholder is not null && placeholder.Type?.Value != P.PlaceholderValues.Body)
            {
                continue;
            }
            foreach (var paragraph in shape.TextBody?.Elements<A.Paragraph>() ?? [])
            {
                token.ThrowIfCancellationRequested();
                var text = ParagraphText(paragraph);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    paragraphs.Add(text);
                }
            }
        }
        return paragraphs.Count == 0 ? null : string.Join("\n\n", paragraphs);
    }

    private static string ParagraphText(A.Paragraph paragraph) => string.Concat(paragraph.Descendants()
        .Select(element => element is A.Text text ? text.Text : element is A.Break ? "\n" : string.Empty));

    public string ConvertToMarkdown(PptxParseResult result, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        var output = new StringBuilder();
        foreach (var slide in result.Slides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.AppendLine($"<!-- Slide number: {slide.SlideNumber} -->").AppendLine();
            output.AppendLine(Markdown.Elements(slide.Elements, cancellationToken, skipImages)).AppendLine();
            if (!string.IsNullOrWhiteSpace(slide.Notes))
            {
                output.AppendLine("### Notes:").AppendLine();
                output.AppendLine(slide.Notes.Trim()).AppendLine();
            }
        }
        return output.ToString().TrimEnd();
    }
}
