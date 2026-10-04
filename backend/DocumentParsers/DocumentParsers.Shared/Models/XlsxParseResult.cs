namespace DocumentParsers;

public sealed class XlsxParseResult
{
    public List<ParsedWorksheet> Worksheets { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}
