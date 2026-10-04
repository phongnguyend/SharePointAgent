namespace DocumentParsers;

public sealed record ParsedWorksheet(int SheetIndex, string SheetName, SpreadsheetElement Data, IReadOnlyList<ImageElement> Images);
