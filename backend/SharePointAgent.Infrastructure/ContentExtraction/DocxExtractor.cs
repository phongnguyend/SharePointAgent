using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

/// <summary>Converts the document to markdown by default, so headings, lists, and tables survive into the indexed text.</summary>
public sealed class DocxExtractor(
    DocumentIntelligenceClient documentIntelligence,
    MarkItDownClient markItDown,
    IOptions<ContentExtractionOptions> options) : IDocxExtractor
{
    private readonly ContentExtractionOptions _options = options.Value;

    public Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        _options.Docx switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(item, content, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Docx}"),
        };

    public Task<string> ExtractAsync(ChatMessageAttachmentFileEntity attachment, byte[] content, CancellationToken cancellationToken) =>
        _options.Docx switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(attachment.FileName, content, attachment.ContentType, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Docx}"),
        };
}
