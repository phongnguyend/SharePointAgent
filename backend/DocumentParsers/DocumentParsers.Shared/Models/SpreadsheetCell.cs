namespace DocumentParsers;

public sealed record SpreadsheetCell(string Reference, string? Value, string? Formula);
