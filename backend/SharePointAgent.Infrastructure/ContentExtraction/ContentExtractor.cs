using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public sealed class ContentExtractor(
    IPdfExtractor pdfExtractor,
    IDocxExtractor docxExtractor,
    IPptxExtractor pptxExtractor,
    IXlsxExtractor xlsxExtractor,
    IImageExtractor imageExtractor,
    DocumentIntelligenceClient documentIntelligence,
    IOptions<DocumentIntelligenceOptions> options,
    ILogger<ContentExtractor> logger) : IContentExtractor
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".sql" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff" };

    private readonly DocumentIntelligenceOptions _options = options.Value;

    public async Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(item.Name);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return await pdfExtractor.ExtractAsync(item, content, cancellationToken);
        }
        if (TextExtensions.Contains(extension))
        {
            return Encoding.UTF8.GetString(content);
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return await docxExtractor.ExtractAsync(item, content, cancellationToken);
        }

        if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            return await pptxExtractor.ExtractAsync(item, content, cancellationToken);
        }

        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return await xlsxExtractor.ExtractAsync(item, content, cancellationToken);
        }

        if (ImageExtensions.Contains(extension))
        {
            return await imageExtractor.ExtractAsync(item, content, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return await documentIntelligence.ExtractAsync(content, cancellationToken);
        }

        logger.LogWarning("No Document Intelligence endpoint is configured; indexing metadata only for {FileName}.", item.Name);
        return $"File name: {item.Name}\nContent type: {item.MimeType}\nPath: {item.ParentPath}";
    }
}
