using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// Converts the workbook to markdown by default. MarkItDown emits one markdown table per worksheet, with
/// cells rendered as their displayed text rather than their stored value.
/// </summary>
public sealed class XlsxExtractor(
    DocumentIntelligenceClient documentIntelligence,
    MarkItDownClient markItDown,
    IOptions<ContentExtractionOptions> options) : IXlsxExtractor
{
    private readonly ContentExtractionOptions _options = options.Value;

    public Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        _options.Xlsx switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(item, content, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Xlsx}"),
        };

    public Task<string> ExtractAsync(ChatMessageAttachmentFileEntity attachment, byte[] content, CancellationToken cancellationToken) =>
        _options.Xlsx switch
        {
            ContentExtractionMethod.DocumentIntelligence => documentIntelligence.ExtractAsync(content, cancellationToken),
            ContentExtractionMethod.MarkItDown => markItDown.ConvertAsync(attachment.FileName, content, attachment.ContentType, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported content extraction method: {_options.Xlsx}"),
        };
}
