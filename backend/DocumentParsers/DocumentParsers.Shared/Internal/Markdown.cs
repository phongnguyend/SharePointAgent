using System.Text;

namespace DocumentParsers;

internal static class Markdown
{
    internal static string Escape(string text) => text.Replace("\\", "\\\\").Replace("|", "\\|")
        .Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    internal static string Table(IEnumerable<IEnumerable<string>> rows)
    {
        var cells = rows.Select(row => row.ToArray()).ToArray();
        var width = cells.Length == 0 ? 0 : cells.Max(row => row.Length);
        if (width == 0)
        {
            return string.Empty;
        }
        var output = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            output.AppendLine("| " + string.Join(" | ", Enumerable.Range(0, width)
                .Select(column => Escape(column < cells[i].Length ? cells[i][column] : string.Empty))) + " |");
            if (i == 0)
            {
                output.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", width)) + " |");
            }
        }
        return output.ToString().TrimEnd();
    }

    internal static string Elements(IEnumerable<DocumentElement> elements, CancellationToken token, bool skipImages = false)
    {
        var output = new StringBuilder();
        foreach (var element in elements)
        {
            token.ThrowIfCancellationRequested();
            switch (element)
            {
                case HeadingElement heading:
                    output.AppendLine(new string('#', Math.Clamp(heading.Level, 1, 6)) + " " + heading.Text);
                    break;
                case TextElement text:
                    output.AppendLine(text.Text);
                    break;
                case TableElement table:
                    output.AppendLine(table.Markdown);
                    break;
                case ImageElement image:
                    output.AppendLine(ImageComment(image));
                    if (skipImages)
                    {
                        break;
                    }
                    output.AppendLine("[Image]");
                    Field("Anchor", image.Anchor);
                    Field("Caption", image.Caption);
                    Field("Alt text", image.AltText);
                    Field("Description", image.Description);
                    Field("Extracted text (OCR)", image.ExtractedText);
                    break;
            }
            output.AppendLine();
        }
        return output.ToString().TrimEnd();

        void Field(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                output.AppendLine(name + ": " + value);
            }
        }
    }

    private static string ImageComment(ImageElement image)
    {
        var name = image.FileName?.Replace('\\', '/').Split('/')[^1].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            var extension = image.ContentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/svg+xml" => ".svg",
                "image/bmp" => ".bmp",
                "image/tiff" => ".tiff",
                _ => ".bin"
            };
            name = "image-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Data)).ToLowerInvariant() + extension;
        }
        var fields = new List<string> { "image: " + EscapeComment(name) };
        if (!string.IsNullOrWhiteSpace(image.Caption))
        {
            fields.Add("caption: " + EscapeComment(image.Caption.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(image.AltText))
        {
            fields.Add("alt: " + EscapeComment(image.AltText.Trim()));
        }
        return "<!-- " + string.Join("; ", fields) + " -->";
    }

    private static string EscapeComment(string value) => System.Net.WebUtility.HtmlEncode(value)
        .Replace("--", "&#45;&#45;").Replace("\r", "&#13;").Replace("\n", "&#10;");
}
