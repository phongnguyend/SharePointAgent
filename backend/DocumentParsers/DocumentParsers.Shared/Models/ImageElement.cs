namespace DocumentParsers;

public sealed record ImageElement : DocumentElement
{
    public required byte[] Data { get; init; }

    public required string ContentType { get; init; }

    public string? AltText { get; init; }

    public string? Caption { get; init; }

    public string? Anchor { get; init; }

    public string? Description { get; set; }

    public string? ExtractedText { get; set; }
}
