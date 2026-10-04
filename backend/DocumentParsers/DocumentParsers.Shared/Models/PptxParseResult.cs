namespace DocumentParsers;

public sealed class PptxParseResult
{
    public List<ParsedSlide> Slides { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}
