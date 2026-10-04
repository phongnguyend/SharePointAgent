namespace DocumentParsers;

public sealed record ParsedSlide(int SlideNumber, IReadOnlyList<DocumentElement> Elements)
{
    public string? Notes { get; init; }
}
