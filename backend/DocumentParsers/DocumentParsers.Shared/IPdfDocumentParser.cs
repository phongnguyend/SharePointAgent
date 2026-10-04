namespace DocumentParsers;

public interface IPdfDocumentParser
{
    Task<PdfParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default);

    string ConvertToMarkdown(PdfParseResult result, CancellationToken cancellationToken = default, bool skipImages = false);
}
