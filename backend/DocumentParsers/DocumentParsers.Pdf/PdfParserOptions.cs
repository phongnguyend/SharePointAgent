namespace DocumentParsers;

public sealed record PdfParserOptions
{
    public long MaxInputBytes { get; init; } = 50 * 1024 * 1024;

    public int MaxImages { get; init; } = 500;

    public long MaxImageBytes { get; init; } = 50 * 1024 * 1024;

    public int MaxTableCells { get; init; } = 1_000_000;

    public PdfReadingOrder PdfReadingOrder { get; init; } = PdfReadingOrder.LayoutAware;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxImages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxImageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTableCells);
        if (!Enum.IsDefined(PdfReadingOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(PdfReadingOrder));
        }
    }
}
