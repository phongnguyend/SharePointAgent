using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace DocumentParsers;

public sealed class XlsxDocumentParser : IXlsxDocumentParser
{
    private readonly ParserOptions _options;

    public XlsxDocumentParser(ParserOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
    }

    public async Task<XlsxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = await ParserInput.ReadAsync(stream, _options, true, cancellationToken);
        using var document = SpreadsheetDocument.Open(buffer, false, ParserInput.Settings(_options));
        var main = document.WorkbookPart ?? throw new InvalidDataException("Missing workbook part.");
        var result = new XlsxParseResult();
        var images = new ImageReader(_options);
        var sharedStrings = main.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        var formats = main.WorkbookStylesPart?.Stylesheet?.CellFormats?.Elements<S.CellFormat>().ToArray() ?? [];
        var date1904 = main.Workbook.WorkbookProperties?.Date1904?.Value == true;
        var cellsRead = 0;
        foreach (var sheet in main.Workbook.Sheets?.Elements<S.Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = main.GetPartById(sheet.Id?.Value ?? throw new InvalidDataException("Missing worksheet relationship.")) as WorksheetPart;
            if (part is null)
            {
                throw new InvalidDataException("Only worksheets are supported; the workbook contains another sheet type.");
            }
            var name = sheet.Name?.Value ?? $"Sheet {result.Worksheets.Count + 1}";
            var number = result.Worksheets.Count + 1;
            var rows = new List<SpreadsheetRow>();
            var nextRow = 1;
            foreach (var row in part.Worksheet.GetFirstChild<S.SheetData>()?.Elements<S.Row>() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowIndex = row.RowIndex is null ? nextRow : checked((int)row.RowIndex.Value);
                nextRow = rowIndex + 1;
                var cells = new List<SpreadsheetCell>();
                var column = 0;
                foreach (var cell in row.Elements<S.Cell>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++cellsRead > _options.MaxTableCells)
                    {
                        throw new InvalidDataException("Workbook exceeds the cell limit.");
                    }
                    var reference = cell.CellReference?.Value ?? CellReference.Format(rowIndex - 1, column);
                    column = CellReference.Parse(reference).Column + 1;
                    var value = cell.CellValue?.Text;
                    var type = cell.DataType?.Value;
                    if (type == S.CellValues.SharedString)
                    {
                        if (!int.TryParse(value, out var index) || index < 0 || index >= sharedStrings.Length)
                        {
                            throw new InvalidDataException("Invalid shared string index.");
                        }
                        value = sharedStrings[index];
                    }
                    else if (type == S.CellValues.InlineString)
                    {
                        value = cell.InlineString?.InnerText;
                    }
                    else if (type == S.CellValues.Boolean)
                    {
                        value = value == "1" ? "TRUE" : value == "0" ? "FALSE" : value;
                    }
                    else if ((type is null || type == S.CellValues.Number) && cell.StyleIndex is not null)
                    {
                        var styleIndex = cell.StyleIndex.Value;
                        if (styleIndex < formats.Length && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                        {
                            var format = formats[styleIndex].NumberFormatId?.Value ?? 0;
                            if (format is >= 14 and <= 17 || format == 22)
                            {
                                try
                                {
                                    if (!date1904 && numeric >= 60 && numeric < 61)
                                    {
                                        result.Warnings.Add(new("ExcelLeapDay", $"{name}!{reference} uses Excel's fictitious 1900 leap day; serial retained."));
                                    }
                                    else
                                    {
                                        var serial = date1904 ? numeric + 1462 : numeric < 60 ? numeric + 1 : numeric;
                                        value = DateTime.FromOADate(serial).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                                    }
                                }
                                catch (ArgumentException)
                                {
                                    result.Warnings.Add(new("InvalidDate", $"{name}!{reference} has an invalid date serial."));
                                }
                            }
                            else if (format is >= 18 and <= 21 || format is >= 45 and <= 47 || format >= 164)
                            {
                                result.Warnings.Add(new("NumberFormatNotApplied", $"{name}!{reference} retains its stored value; time/duration or custom formatting is not applied."));
                            }
                        }
                    }
                    var formula = cell.CellFormula?.Text;
                    if (formula is not null && cell.CellValue is null)
                    {
                        result.Warnings.Add(new("MissingFormulaValue", $"{name}!{reference} has no cached formula value."));
                    }
                    cells.Add(new(reference, value, formula));
                }
                rows.Add(new(rowIndex, cells));
            }
            var sheetImages = new List<ImageElement>();
            var drawing = part.DrawingsPart;
            foreach (var anchor in drawing?.WorksheetDrawing?.ChildElements ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = anchor.GetFirstChild<Xdr.FromMarker>();
                string? cellAnchor = null;
                if (int.TryParse(from?.RowId?.Text, out var r) && int.TryParse(from?.ColumnId?.Text, out var c))
                {
                    cellAnchor = CellReference.Format(r, c);
                }
                foreach (var picture in anchor.Descendants<Xdr.Picture>())
                {
                    var image = images.FromPart(drawing!, picture.BlipFill?.Blip?.Embed?.Value, result.Warnings, cancellationToken);
                    if (image is not null)
                    {
                        sheetImages.Add(image with
                        {
                            PageNumber = number,
                            Anchor = cellAnchor,
                            AltText = picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Description?.Value
                        });
                    }
                }
                if (anchor.Descendants<Xdr.GraphicFrame>().Any())
                {
                    result.Warnings.Add(new("UnsupportedChart", $"{name} contains a chart or graphic that is not rendered."));
                }
            }
            if (part.Worksheet.Elements<S.MergeCells>().Any())
            {
                result.Warnings.Add(new("MergedCells", $"{name} retains merged cells only at their stored coordinates."));
            }
            result.Worksheets.Add(new(number, name, new SpreadsheetElement { SheetName = name, Rows = rows, PageNumber = number }, sheetImages));
        }
        return result;
    }

    public string ConvertToMarkdown(XlsxParseResult result, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        var output = new StringBuilder();
        foreach (var sheet in result.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.AppendLine($"# {sheet.SheetName}").AppendLine();
            var pendingImages = (skipImages ? Enumerable.Empty<ImageElement>() : sheet.Images).OrderBy(image => image.Anchor is null ? int.MaxValue : CellReference.Parse(image.Anchor).Row).ToList();
            var nonEmptyRows = sheet.Data.Rows.Where(row =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return row.Cells.Any(cell => !string.IsNullOrWhiteSpace(cell.Value) || !string.IsNullOrWhiteSpace(cell.Formula));
            });
            foreach (var region in nonEmptyRows.OrderBy(row => row.RowIndex).Chunk(_options.MarkdownRowsPerRegion))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var columns = region.SelectMany(row => row.Cells)
                    .Where(cell => !string.IsNullOrWhiteSpace(CellText(cell)))
                    .Select(cell => CellReference.Parse(cell.Reference).Column).Distinct().Order().ToArray();
                if (columns.Length > 0)
                {
                    // Keep sparse columns sparse and bound both table dimensions.
                    foreach (var columnRegion in columns.Chunk(50))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var tableRows = new List<IEnumerable<string>>
                        {
                            new[] { "Row" }.Concat(columnRegion.Select(column => CellReference.Format(0, column).TrimEnd('1')))
                        };
                        int? firstRow = null;
                        var lastRow = 0;
                        foreach (var row in region)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var values = row.Cells.ToDictionary(cell => CellReference.Parse(cell.Reference).Column);
                            var renderedCells = columnRegion.Select(column =>
                                values.TryGetValue(column, out var cell) ? CellText(cell) : string.Empty).ToArray();
                            if (renderedCells.All(string.IsNullOrWhiteSpace))
                            {
                                continue;
                            }
                            firstRow ??= row.RowIndex;
                            lastRow = row.RowIndex;
                            tableRows.Add(new[] { row.RowIndex.ToString(CultureInfo.InvariantCulture) }.Concat(renderedCells));
                        }
                        if (firstRow is null)
                        {
                            continue;
                        }
                        output.AppendLine($"## {CellReference.Format(firstRow.Value - 1, columnRegion[0])}:{CellReference.Format(lastRow - 1, columnRegion[^1])}").AppendLine();
                        output.AppendLine(Markdown.Table(tableRows)).AppendLine();
                    }
                }
                var nearby = pendingImages.Where(image => image.Anchor is not null && CellReference.Parse(image.Anchor).Row < region[^1].RowIndex).ToArray();
                output.AppendLine(Markdown.Elements(nearby, cancellationToken));
                foreach (var image in nearby)
                {
                    pendingImages.Remove(image);
                }
            }
            output.AppendLine(Markdown.Elements(pendingImages, cancellationToken)).AppendLine();
        }
        return output.ToString().TrimEnd();
    }

    private static string CellText(SpreadsheetCell cell) => !string.IsNullOrWhiteSpace(cell.Value)
        ? cell.Value : !string.IsNullOrWhiteSpace(cell.Formula) ? "=" + cell.Formula : string.Empty;
}
