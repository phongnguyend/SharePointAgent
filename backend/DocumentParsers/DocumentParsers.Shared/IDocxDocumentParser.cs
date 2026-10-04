namespace DocumentParsers;

public interface IDocxDocumentParser
{
    Task<DocxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default);

    string ConvertToMarkdown(DocxParseResult result, CancellationToken cancellationToken = default, bool skipImages = false);
}
