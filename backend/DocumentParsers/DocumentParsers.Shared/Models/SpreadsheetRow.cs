namespace DocumentParsers;

public sealed record SpreadsheetRow(int RowIndex, IReadOnlyList<SpreadsheetCell> Cells);
