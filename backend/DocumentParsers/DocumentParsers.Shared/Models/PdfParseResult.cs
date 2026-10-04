namespace DocumentParsers;

public sealed class PdfParseResult
{
    public int PageCount { get; init; }

    public List<DocumentElement> Elements { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}
