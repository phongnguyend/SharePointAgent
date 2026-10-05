using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace DocumentParsers.Tests;

public sealed class PdfLayoutTests
{
    [Fact]
    public async Task ClassifiesHeadingsParagraphsAndLists()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 800);
        page.AddText("Document title", 24, new PdfPoint(40, 740), font);
        page.AddText("Business objectives", 16, new PdfPoint(40, 680), font);
        page.AddText("This paragraph begins on the first line", 12, new PdfPoint(40, 640), font);
        page.AddText("and continues on the next line.", 12, new PdfPoint(40, 624), font);
        page.AddText("- First item", 12, new PdfPoint(40, 570), font);
        page.AddText("2) Second item", 12, new PdfPoint(40, 548), font);
        var result = await Parse(builder);
        Assert.Collection(result.Elements,
            element => Assert.Equal(1, Assert.IsType<HeadingElement>(element).Level),
            element => Assert.Equal(2, Assert.IsType<HeadingElement>(element).Level),
            element => Assert.Equal("This paragraph begins on the first line\nand continues on the next line.", Assert.IsType<TextElement>(element).Text),
            element => Assert.Equal("- First item", Assert.IsType<TextElement>(element).Text),
            element => Assert.Equal("2. Second item", Assert.IsType<TextElement>(element).Text));
        var markdown = new PdfDocumentParser().ConvertToMarkdown(result);
        Assert.Contains("## Business objectives", markdown);
        Assert.All(result.Elements, element => Assert.NotNull(element.BoundingBox));
    }

    [Theory]
    [InlineData(PdfReadingOrder.LayoutAware)]
    [InlineData(PdfReadingOrder.RowBased)]
    public async Task ReadsColumnsAndImagesUsingSelectedMode(PdfReadingOrder mode)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 800);
        // Deliberately draw the right column first to test geometric ordering.
        page.AddText("Right first paragraph", 12, new PdfPoint(330, 650), font);
        page.AddText("Right second paragraph", 12, new PdfPoint(330, 550), font);
        page.AddText("Left first paragraph", 12, new PdfPoint(40, 650), font);
        page.AddText("Left second paragraph", 12, new PdfPoint(40, 550), font);
        page.AddText("A heading spanning both text columns", 26, new PdfPoint(40, 740), font);
        page.AddPng(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"),
            new PdfRectangle(330, 590, 420, 620));
        var result = await Parse(builder, mode);
        Assert.IsType<HeadingElement>(result.Elements[0]);
        var expected = mode == PdfReadingOrder.LayoutAware
            ? new[] { "Left first paragraph", "Left second paragraph", "Right first paragraph", "Image", "Right second paragraph" }
            : new[] { "Left first paragraph", "Right first paragraph", "Image", "Left second paragraph", "Right second paragraph" };
        Assert.Equal(expected, result.Elements.Skip(1).Select(element => element is TextElement text ? text.Text : "Image"));
        Assert.Equal(Enumerable.Range(0, 6).Select(value => (long?)value), result.Elements.Select(element => element.Order));
    }

    [Fact]
    public async Task AlignedNumericRowsBecomeOneTableWithoutDuplicateText()
    {
        var result = await Parse(TableDocument());
        var table = Assert.IsType<TableElement>(Assert.Single(result.Elements));
        Assert.Contains("| Item | Quantity |", table.Markdown);
        Assert.Contains("| A\\|B | 20 |", table.Markdown);
        Assert.Contains("| Second | 30 |", table.Markdown);
        Assert.Equal(1, table.PageNumber);
        Assert.NotNull(table.BoundingBox);
    }

    [Fact]
    public async Task RepeatedTextColumnsAreNotPromotedToTables()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 800);
        for (var i = 0; i < 3; i++)
        {
            page.AddText("Left column prose", 12, new PdfPoint(40, 700 - i * 18), font);
            page.AddText("Right column prose", 12, new PdfPoint(330, 700 - i * 18), font);
        }
        var result = await Parse(builder);
        Assert.Equal(2, result.Elements.Count);
        Assert.All(result.Elements, element => Assert.IsType<TextElement>(element));
        Assert.Equal(new[] { string.Join("\n", Enumerable.Repeat("Left column prose", 3)), string.Join("\n", Enumerable.Repeat("Right column prose", 3)) },
            result.Elements.Cast<TextElement>().Select(element => element.Text));
    }

    [Fact]
    public void ReadingRowsTolerateSmallOffsetsWithoutChainingOrUsingImageHeight()
    {
        var left = new TextElement("Left") { BoundingBox = new(40, 101, 80, 10) };
        var right = new ImageElement { Data = [1], ContentType = "image/png", BoundingBox = new(330, 100, 100, 200) };
        var next = new TextElement("Next row") { BoundingBox = new(10, 103, 80, 10) };
        var unknown = new TextElement("No geometry");
        var ordered = LayoutAnalyzer.Order([unknown, next, right, left], default, PdfReadingOrder.RowBased);
        Assert.Equal(new DocumentElement[] { left, right, next, unknown }, ordered);
    }

    [Fact]
    public async Task DetectedTablesRespectCellLimit()
    {
        using var stream = new MemoryStream(TableDocument().Build());
        var parser = new PdfDocumentParser(options: new PdfParserOptions { MaxTableCells = 5 });
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
    }

    [Fact]
    public void SpanningTitleTableAndFooterSeparateColumnSections()
    {
        static TextElement Text(string text, double x, double y, double width = 160)
            => new(text) { BoundingBox = new(x, y, width, 12) };
        var title = Text("Title", 40, 10, 480);
        var a = Text("A", 40, 40);
        var b = Text("B", 40, 180);
        var c = Text("C", 330, 40);
        var d = Text("D", 330, 180);
        var table = new TableElement("Table") { BoundingBox = new(40, 220, 480, 40) };
        var e = Text("E", 40, 290);
        var f = Text("F", 40, 420);
        var g = Text("G", 330, 290);
        var h = Text("H", 330, 420);
        var footer = Text("Footer", 40, 460, 480);
        var unknown = new TextElement("Unknown position");
        var original = new List<DocumentElement> { unknown, h, d, c, footer, table, title, g, f, b, a, e };
        var snapshot = original.ToArray();
        var actual = LayoutAnalyzer.Order(original, default);
        Assert.Equal(new DocumentElement[] { title, a, b, c, d, table, e, f, g, h, footer, unknown }, actual);
        Assert.Equal(snapshot, original);
    }

    [Fact]
    public void ThreeUnevenColumnsReadEachColumnCompletely()
    {
        var elements = new List<DocumentElement>();
        for (var column = 0; column < 3; column++)
        {
            for (var row = 0; row < 3; row++)
            {
                elements.Add(new TextElement($"{column}:{row}")
                {
                    BoundingBox = new(40 + column * 180, 30 + row * 60 + column * 3, 110, 12)
                });
            }
        }
        var expected = elements.ToArray();
        elements.Reverse();
        Assert.Equal(expected, LayoutAnalyzer.Order(elements, default));
    }

    [Fact]
    public void IsolatedStaggeredBlocksFollowVerticalPosition()
    {
        var upperRight = new TextElement("First") { BoundingBox = new(330, 30, 100, 12) };
        var lowerLeft = new TextElement("Second") { BoundingBox = new(40, 100, 100, 12) };
        Assert.Equal(new[] { upperRight, lowerLeft }, LayoutAnalyzer.Order([lowerLeft, upperRight], default));
    }

    [Fact]
    public void InvalidReadingOrderIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocumentParser(options:
            new PdfParserOptions { PdfReadingOrder = (PdfReadingOrder)99 }));
    }

    [Fact]
    public void LayoutAnalysisHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LayoutAnalyzer(new()).Analyze([], [], 800, 1, cancellation.Token));
    }

    private static PdfDocumentBuilder TableDocument()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 800);
        var labels = new[] { "Item", "A|B", "Second" };
        var values = new[] { "Quantity", "20", "30" };
        for (var i = 0; i < labels.Length; i++)
        {
            page.AddText(labels[i], 12, new PdfPoint(40, 700 - i * 20), font);
            page.AddText(values[i], 12, new PdfPoint(220, 700 - i * 20), font);
        }
        return builder;
    }

    private static async Task<PdfParseResult> Parse(PdfDocumentBuilder builder, PdfReadingOrder mode = PdfReadingOrder.LayoutAware)
    {
        using var stream = new MemoryStream(builder.Build());
        return await new PdfDocumentParser(options: new PdfParserOptions { PdfReadingOrder = mode }).ParseAsync(stream);
    }
}
