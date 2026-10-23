using PdfObjectKit.Converters;
using PdfObjectKit.Diagnostics;
using PdfObjectKit.Readers;

namespace DocumentParsers;

public static class PdfObjectKitConversion
{
    public static async Task<MarkdownConversionResult> ConvertAsync(
        Stream stream,
        bool skipImages = false,
        Func<IEnumerable<ImageElement>, List<DocumentParseWarning>, CancellationToken, Task>? enrichImages = null,
        CancellationToken cancellationToken = default)
    {
        using var buffer = await ParserInput.ReadAsync(stream, 50 * 1024 * 1024, false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var document = PdfReader.Read(buffer.ToArray());
        var options = new PdfMarkdownOptions();
        if (skipImages)
        {
            options = new PdfMarkdownOptions { ImageRender = (_, _, _) => string.Empty };
        }
        var result = PdfMarkdownConverter.Convert(document, options);
        var warnings = result.Diagnostics
            .Where(diagnostic => diagnostic.Severity >= PdfDiagnosticSeverity.Warning)
            .Select(diagnostic => new DocumentParseWarning(diagnostic.Code.ToString(), diagnostic.Message)).ToList();
        if (!skipImages && enrichImages is not null)
        {
            var objects = document.Objects.ToDictionary(item => item.Id, item => item.Value);
            foreach (var page in result.Document.Pages)
            {
                foreach (var block in page.Blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (block.Image is not { } image)
                    {
                        continue;
                    }
                    var encoded = image.Source?.InlineDataBase64;
                    if (encoded is null && image.Source?.ImageObjects.FirstOrDefault() is { } reference
                        && objects.TryGetValue(reference, out var value))
                    {
                        encoded = value.Base64;
                    }
                    if (image.ContentType != "image/jpeg" || encoded is null)
                    {
                        warnings.Add(new("ImageEnrichmentUnsupported", $"Page {page.PageNumber}: image bytes are not available in a supported standalone format; image metadata is retained."));
                        continue;
                    }
                    var element = new ImageElement
                    {
                        Data = Convert.FromBase64String(encoded),
                        ContentType = image.ContentType,
                        PageNumber = page.PageNumber
                    };
                    await enrichImages([element], warnings, cancellationToken);
                    image.ImageUnderstandingText = element.Description;
                    image.OcrText = element.ExtractedText;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(PdfMarkdownConverter.Render(result.Document, options),
            new Dictionary<string, string> { ["Parser"] = "PdfObjectKit", ["PageCount"] = result.Document.Pages.Count.ToString() },
            warnings.ToArray());
    }
}
