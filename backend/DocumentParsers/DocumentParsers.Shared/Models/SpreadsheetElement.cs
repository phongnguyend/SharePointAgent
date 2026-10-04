namespace DocumentParsers;

public sealed record SpreadsheetElement : DocumentElement
{
    public required string SheetName { get; init; }

    public required IReadOnlyList<SpreadsheetRow> Rows { get; init; }
}
