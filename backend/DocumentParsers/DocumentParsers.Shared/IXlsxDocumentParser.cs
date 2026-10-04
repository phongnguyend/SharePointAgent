namespace DocumentParsers;

public interface IXlsxDocumentParser
{
    Task<XlsxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default);

    string ConvertToMarkdown(XlsxParseResult result, CancellationToken cancellationToken = default, bool skipImages = false);
}
