using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DocumentParsers;

internal static class PptxListText
{
    internal static string Extract(P.Shape shape, SlidePart slide, PresentationPart presentation, CancellationToken token)
    {
        var output = new StringBuilder();
        var counters = new int?[9];
        var starts = new int?[9];
        var schemes = new string?[9];
        var widths = Enumerable.Repeat(4, 9).ToArray();
        bool? previousList = null;
        var placeholder = Placeholder(shape);
        var layoutShape = Match(slide.SlideLayoutPart?.SlideLayout.CommonSlideData?.ShapeTree, placeholder);
        var master = slide.SlideLayoutPart?.SlideMasterPart?.SlideMaster;
        var masterShape = Match(master?.CommonSlideData?.ShapeTree, Placeholder(layoutShape) ?? placeholder);
        var type = placeholder?.Type?.Value ?? Placeholder(layoutShape)?.Type?.Value ?? P.PlaceholderValues.Body;
        OpenXmlElement? masterStyle = placeholder is null ? master?.TextStyles?.OtherStyle
            : type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle
                ? master?.TextStyles?.TitleStyle : master?.TextStyles?.BodyStyle;

        foreach (var paragraph in shape.TextBody?.Elements<A.Paragraph>() ?? [])
        {
            token.ThrowIfCancellationRequested();
            var text = string.Concat(paragraph.Descendants().Select(element =>
                element is A.Text run ? run.Text : element is A.Break ? "\n" : string.Empty));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            var level = Math.Clamp(paragraph.ParagraphProperties?.Level?.Value ?? 0, 0, 8);
            var bullet = Bullet(paragraph.ParagraphProperties)
                ?? StyleBullet(shape.TextBody?.ListStyle, level)
                ?? PlaceholderBullet(layoutShape, level)
                ?? PlaceholderBullet(masterShape, level)
                ?? StyleBullet(masterStyle, level)
                ?? StyleBullet(presentation.Presentation.DefaultTextStyle, level);
            var isList = bullet is A.AutoNumberedBullet or A.CharacterBullet or A.PictureBullet;
            if (previousList is not null && previousList != isList)
            {
                output.AppendLine();
            }
            previousList = isList;
            if (!isList)
            {
                Array.Clear(counters);
                Array.Clear(starts);
                Array.Clear(schemes);
                output.AppendLine(text);
                continue;
            }
            for (var deeper = level + 1; deeper < counters.Length; deeper++)
            {
                counters[deeper] = null;
                starts[deeper] = null;
                schemes[deeper] = null;
            }
            var marker = "- ";
            if (bullet is A.AutoNumberedBullet numbered)
            {
                var start = numbered.StartAt?.Value ?? 1;
                var scheme = numbered.Type?.InnerText;
                // Repeated style defaults belong to one sequence, not a restart on every paragraph.
                counters[level] = counters[level] is null || starts[level] != start || schemes[level] != scheme
                    ? start : counters[level] + 1;
                starts[level] = start;
                schemes[level] = scheme;
                marker = counters[level]!.Value.ToString(CultureInfo.InvariantCulture) + ". ";
            }
            else
            {
                counters[level] = null;
                starts[level] = null;
                schemes[level] = null;
            }
            widths[level] = Math.Max(4, marker.Length);
            var indent = new string(' ', widths.Take(level).Sum());
            var continuation = indent + new string(' ', marker.Length);
            output.Append(indent).Append(marker).AppendLine(text.Replace("\n", "\n" + continuation));
        }
        return output.ToString().TrimEnd();
    }

    private static OpenXmlElement? Bullet(OpenXmlElement? properties) => properties?.ChildElements
        .FirstOrDefault(element => element is A.AutoNumberedBullet or A.CharacterBullet or A.PictureBullet or A.NoBullet);

    private static OpenXmlElement? StyleBullet(OpenXmlElement? style, int level) =>
        Bullet(style?.ChildElements.FirstOrDefault(element => element.LocalName == $"lvl{level + 1}pPr"))
        ?? Bullet(style?.GetFirstChild<A.DefaultParagraphProperties>());

    private static OpenXmlElement? PlaceholderBullet(P.Shape? shape, int level) =>
        Bullet(shape?.TextBody?.Elements<A.Paragraph>().FirstOrDefault(paragraph =>
            (paragraph.ParagraphProperties?.Level?.Value ?? 0) == level)?.ParagraphProperties)
        ?? StyleBullet(shape?.TextBody?.ListStyle, level);

    private static P.PlaceholderShape? Placeholder(P.Shape? shape) =>
        shape?.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;

    private static P.Shape? Match(P.ShapeTree? tree, P.PlaceholderShape? placeholder)
    {
        if (placeholder is null)
        {
            return null;
        }
        return tree?.Elements<P.Shape>().FirstOrDefault(shape => Placeholder(shape) is { } candidate &&
            (candidate.Index?.Value ?? 0) == (placeholder.Index?.Value ?? 0));
    }
}
