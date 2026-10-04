namespace DocumentParsers;

public interface IPptxDocumentParser
{
    Task<PptxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default);

    string ConvertToMarkdown(PptxParseResult result, CancellationToken cancellationToken = default, bool skipImages = false);
}
