namespace DocumentParsers;

public sealed record ParserOptions
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
        if (MaxInputBytes <= 0 || MaxExpandedBytes <= 0 || MaxZipEntries <= 0 ||
            MaxImages <= 0 || MaxImageBytes <= 0 || MaxTableCells <= 0 || MarkdownRowsPerRegion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ParserOptions), "All limits must be positive.");
        }
    }
}
