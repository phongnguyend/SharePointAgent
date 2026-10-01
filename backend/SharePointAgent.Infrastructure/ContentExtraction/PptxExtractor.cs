using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

/// <summary>Converts the presentation to markdown by default. MarkItDown emits one section per slide, in slide order.</summary>
public sealed class PptxExtractor(
    DocumentIntelligenceClient documentIntelligence,
    MarkItDownClient markItDown,
    IOptions<ContentExtractionOptions> options) : IPptxExtractor
{
    private readonly ContentExtractionOptions _options = options.Value;

    public Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        _options.Pptx switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(item, content, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Pptx}"),
        };

    public Task<string> ExtractAsync(ChatMessageAttachmentFileEntity attachment, byte[] content, CancellationToken cancellationToken) =>
        _options.Pptx switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(attachment.FileName, content, attachment.ContentType, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Pptx}"),
        };
}
