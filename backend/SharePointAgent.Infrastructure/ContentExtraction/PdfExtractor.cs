using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

public sealed class PdfExtractor(
    DocumentIntelligenceClient documentIntelligence,
    MarkItDownClient markItDown,
    IOptions<ContentExtractionOptions> options) : IPdfExtractor
{
    private readonly ContentExtractionOptions _options = options.Value;

    public Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        _options.Pdf switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(item, content, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Pdf}"),
        };

    public Task<string> ExtractAsync(ChatMessageAttachmentFileEntity attachment, byte[] content, CancellationToken cancellationToken) =>
        _options.Pdf switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(attachment.FileName, content, attachment.ContentType, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Pdf}"),
        };
}
