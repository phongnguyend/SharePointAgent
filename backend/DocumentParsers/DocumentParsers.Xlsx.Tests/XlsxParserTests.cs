using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;

using System.IO.Compression;
using Azure.AI.DocumentIntelligence;
using NSubstitute;

namespace DocumentParsers.Tests;

public sealed class XlsxParserTests
{
    [Fact]
    public void WideTablesOmitRowsEmptyWithinEachColumnGroupAndUnusedColumns()
    {
        var result = new XlsxParseResult();
        var first = Enumerable.Range(0, 50).Select(column => new SpreadsheetCell(CellReference.Format(0, column), "Value", null)).ToArray();
        result.Worksheets.Add(new(1, "Wide", new SpreadsheetElement
        {
            SheetName = "Wide",
            Rows = [new(1, first), new(2, [new("AY2", "Other group", null), new("HZ2", "", null)])]
        }, []));
        var markdown = new XlsxDocumentParser().ConvertToMarkdown(result).Replace("\r\n", "\n");
        Assert.Contains("## A1:AX1", markdown);
        Assert.Contains("## AY2:AY2", markdown);
        Assert.DoesNotContain("HZ", markdown);
        Assert.DoesNotContain("| 2 |", markdown[..markdown.IndexOf("## AY2:AY2", StringComparison.Ordinal)]);
        Assert.DoesNotContain("| 1 |", markdown[markdown.IndexOf("## AY2:AY2", StringComparison.Ordinal)..]);
        Assert.DoesNotMatch(@"(?m)^\| \d+ \|(?:\s*\|)+\s*$", markdown);
    }

    [Fact]
    public void MarkdownOmitsEmptyRowsBeforeChunkingButPreservesDataAndImages()
    {
        var rows = new SpreadsheetRow[]
        {
            new(1, []),
            new(2, [new("A2", null, null)]),
            new(3, [new("A3", " \t", null), new("B3", "", null)]),
            new(4, [new("A4", "0", null)]),
            new(5, [new("A5", "FALSE", null)]),
            new(6, [new("A6", null, "1+1")]),
            new(7, [new("A7", "", "IF(TRUE,\"\",\"x\")")])
        };
        var result = new XlsxParseResult();
        result.Worksheets.Add(new(1, "Data", new SpreadsheetElement { SheetName = "Data", Rows = rows },
            [new ImageElement { Data = [1], ContentType = "image/png", Anchor = "A2", Caption = "Kept image" }]));
        var markdown = new XlsxDocumentParser(new ParserOptions { MarkdownRowsPerRegion = 2 }).ConvertToMarkdown(result);
        Assert.DoesNotContain("| 1 |", markdown);
        Assert.DoesNotContain("| 2 |", markdown);
        Assert.DoesNotContain("| 3 |", markdown);
        Assert.Contains("## A4:A5", markdown);
        Assert.Contains("## A6:A7", markdown);
        Assert.Contains("| 4 | 0 |", markdown);
        Assert.Contains("| 5 | FALSE |", markdown);
        Assert.Contains("| 6 | =1+1 |", markdown);
        Assert.Contains("| 7 |", markdown);
        Assert.Contains("=IF(TRUE,\"\",\"x\")", markdown);
        Assert.Contains("Anchor: A2", markdown);
        Assert.Contains("Kept image", markdown);
        Assert.Same(rows, result.Worksheets[0].Data.Rows);
        Assert.Equal(7, rows.Length);
    }

    [Fact]
    public void EmptyWorksheetKeepsHeadingAndImagesWithoutEmptyTable()
    {
        var result = new XlsxParseResult();
        result.Worksheets.Add(new(1, "Empty", new SpreadsheetElement
        {
            SheetName = "Empty",
            Rows = [new(1, [new("A1", "", null)])]
        }, [new ImageElement { Data = [1], ContentType = "image/png", Anchor = "A1" }]));
        var parser = new XlsxDocumentParser();
        Assert.Equal("# Empty", parser.ConvertToMarkdown(result, skipImages: true));
        var markdown = parser.ConvertToMarkdown(result);
        Assert.Contains("Anchor: A1", markdown);
        Assert.DoesNotContain("| Row |", markdown);
    }

    [Fact]
    public async Task XlsxRetainsSparseCellsTypesFormulasDatesAnchorsAndEmptySheets()
    {
        using var stream = CreateWorkbook();
        var parser = new XlsxDocumentParser(new ParserOptions { MarkdownRowsPerRegion = 1 });
        var result = await parser.ParseAsync(stream);
        var sheet = result.Worksheets[0];
        var cells = sheet.Data.Rows.SelectMany(row => row.Cells).ToDictionary(cell => cell.Reference);
        Assert.Equal("Shared", cells["A1"].Value);
        Assert.Equal("Inline", cells["D1"].Value);
        Assert.Equal("TRUE", cells["F1"].Value);
        Assert.Equal("42", cells["AA1"].Value);
        Assert.Equal("SUM(A1:A2)", cells["AA1"].Formula);
        Assert.StartsWith("1900-01-01", cells["A25"].Value);
        Assert.Null(cells["D25"].Value);
        Assert.Contains(result.Warnings, warning => warning.Code == "MissingFormulaValue");
        Assert.Contains(result.Warnings, warning => warning.Code == "MergedCells");
        Assert.Equal("D3", Assert.Single(sheet.Images).Anchor);
        var markdown = parser.ConvertToMarkdown(result);
        Assert.Contains("# Empty", markdown);
        Assert.Contains("## A1:AA1", markdown);
        Assert.Contains("## A25:D25", markdown);
        Assert.Contains("Anchor: D3", markdown);
        Assert.Contains("| Row | A | D | F | AA |", markdown);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("A1", 0, 0)]
    [InlineData("D25", 24, 3)]
    [InlineData("AA7", 6, 26)]
    [InlineData("XFD1048576", 1048575, 16383)]
    public void CellReferencesRoundTrip(string reference, int row, int column)
    {
        Assert.Equal((row, column), CellReference.Parse(reference));
        Assert.Equal(reference, CellReference.Format(row, column));
    }

    [Theory]
    [InlineData("A0")]
    [InlineData("1A")]
    [InlineData("XFE1")]
    [InlineData("A1048577")]
    public void CellReferencesRejectInvalidCoordinates(string reference)
    {
        Assert.Throws<FormatException>(() => CellReference.Parse(reference));
    }

    [Fact]
    public async Task ImageAndCellLimitsAreEnforced()
    {
        using var stream = CreateWorkbook();
        await Assert.ThrowsAsync<InvalidDataException>(() => new XlsxDocumentParser(new ParserOptions { MaxImageBytes = 1 }).ParseAsync(stream));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => new XlsxDocumentParser(new ParserOptions { MaxTableCells = 1 }).ParseAsync(stream));
    }

    [Fact]
    public async Task SupportsNonSeekableInput()
    {
        using var workbook = CreateWorkbook();
        using var stream = new NonSeekableStream(workbook);
        var result = await new XlsxDocumentParser().ParseAsync(stream);
        Assert.Equal(2, result.Worksheets.Count);
        Assert.True(workbook.CanRead);
    }

    internal static MemoryStream CreateWorkbook()
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var main = document.AddWorkbookPart();
            main.AddNewPart<SharedStringTablePart>().SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text("Shared")));
            main.AddNewPart<WorkbookStylesPart>().Stylesheet = new S.Stylesheet(new S.CellFormats(new S.CellFormat { NumberFormatId = 0 }, new S.CellFormat { NumberFormatId = 14 }));
            var sheet = main.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(
                    new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new("0") },
                    new S.Cell { CellReference = "D1", DataType = S.CellValues.InlineString, InlineString = new(new S.Text("Inline")) },
                    new S.Cell { CellReference = "F1", DataType = S.CellValues.Boolean, CellValue = new("1") },
                    new S.Cell { CellReference = "AA1", CellValue = new("42"), CellFormula = new("SUM(A1:A2)") }) { RowIndex = 1 },
                new S.Row(
                    new S.Cell { CellReference = "A25", CellValue = new("1"), StyleIndex = 1 },
                    new S.Cell { CellReference = "D25", CellFormula = new("1+1") }) { RowIndex = 25 }),
                new S.MergeCells(new S.MergeCell { Reference = "A1:B1" }));
            var drawing = sheet.AddNewPart<DrawingsPart>();
            var image = drawing.AddImagePart(ImagePartType.Png);
            using var bytes = new MemoryStream([1, 2, 3]);
            image.FeedData(bytes);
            drawing.WorksheetDrawing = new Xdr.WorksheetDrawing(new Xdr.OneCellAnchor(
                new Xdr.FromMarker(new Xdr.ColumnId("3"), new Xdr.ColumnOffset("0"), new Xdr.RowId("2"), new Xdr.RowOffset("0")),
                new Xdr.Picture(new Xdr.BlipFill(new A.Blip { Embed = drawing.GetIdOfPart(image) }))));
            sheet.Worksheet.Append(new S.Drawing { Id = sheet.GetIdOfPart(drawing) });
            var empty = main.AddNewPart<WorksheetPart>();
            empty.Worksheet = new S.Worksheet(new S.SheetData());
            main.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Name = "Data", SheetId = 1, Id = main.GetIdOfPart(sheet) },
                new S.Sheet { Name = "Empty", SheetId = 2, Id = main.GetIdOfPart(empty) }));
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ParserHonorsCancellationAndRejectsCorruptInput()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var parser = new XlsxDocumentParser();
        using var stream = new MemoryStream("broken file"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAsync(stream, cancelled.Token));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
        Assert.True(stream.CanRead);
    }
}
