namespace DocumentParsers;

public sealed class DocxParseResult
{
    public List<DocumentElement> BodyElements { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}
