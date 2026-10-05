namespace DocumentParsers;

public sealed record XlsxParserOptions
{
    public long MaxInputBytes { get; init; } = 50 * 1024 * 1024;

    public long MaxExpandedBytes { get; init; } = 200 * 1024 * 1024;

    public int MaxZipEntries { get; init; } = 10_000;

    public int MaxImages { get; init; } = 500;

    public long MaxImageBytes { get; init; } = 50 * 1024 * 1024;

    public int MaxTableCells { get; init; } = 1_000_000;

    public int MarkdownRowsPerRegion { get; init; } = 100;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxZipEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxImages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxImageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTableCells);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MarkdownRowsPerRegion);
    }
}
