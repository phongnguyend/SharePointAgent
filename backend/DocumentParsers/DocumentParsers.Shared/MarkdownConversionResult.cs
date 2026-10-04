namespace DocumentParsers;

public sealed record MarkdownConversionResult(
    string Markdown,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<DocumentParseWarning> Warnings);
