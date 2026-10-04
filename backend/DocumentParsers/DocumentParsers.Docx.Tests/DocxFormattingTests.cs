using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DocumentParsers.Tests;

public sealed class DocxFormattingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumberedHeadingTakesPrecedenceOverListAndBold(bool inherited)
    {
        var result = await ParseAsync(main =>
        {
            AddNumbering(main);
            main.AddNewPart<StyleDefinitionsPart>().Styles = new W.Styles(
                new W.Style(new W.StyleName { Val = "Heading 2" },
                    new W.StyleParagraphProperties(Numbering(1, 0)),
                    new W.StyleRunProperties(new W.Bold())) { StyleId = "Heading2", Type = W.StyleValues.Paragraph },
                new W.Style(new W.BasedOn { Val = "Heading2" }) { StyleId = "CustomHeading", Type = W.StyleValues.Paragraph });
            var properties = inherited
                ? new W.ParagraphProperties(new W.ParagraphStyleId { Val = "CustomHeading" })
                : new W.ParagraphProperties(Numbering(1, 0), new W.OutlineLevel { Val = 1 });
            return new W.Body(List("Previous item", 1, 0),
                new W.Paragraph(properties, Run("Business Objectives", new W.Bold())),
                List("Following item", 1, 0));
        });
        Assert.Equal(new HeadingElement("Business Objectives", 2) { Order = 1 }, result.BodyElements[1]);
        var markdown = new DocxDocumentParser().ConvertToMarkdown(result);
        Assert.Contains("## Business Objectives", markdown);
        Assert.DoesNotContain("**Business Objectives**", markdown);
        Assert.DoesNotContain("2. Business Objectives", markdown);
        Assert.Contains("3. Following item", markdown);
    }

    [Fact]
    public async Task PreservesDirectAndInheritedBoldAndExplicitOff()
    {
        var result = await ParseAsync(main =>
        {
            main.AddNewPart<StyleDefinitionsPart>().Styles = new W.Styles(
                new W.Style(new W.StyleRunProperties(new W.Bold())) { StyleId = "BoldBase", Type = W.StyleValues.Paragraph },
                new W.Style(new W.BasedOn { Val = "BoldBase" }) { StyleId = "Derived", Type = W.StyleValues.Paragraph },
                new W.Style(new W.StyleRunProperties(new W.Bold())) { StyleId = "Strong", Type = W.StyleValues.Character });
            return new W.Body(
                new W.Paragraph(Run("Normal "), Run("bold ", new W.Bold()), Run("phrase", new W.Bold()), Run(" end")),
                new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Derived" }),
                    Run("Inherited "), Run("plain", new W.Bold { Val = false })),
                new W.Paragraph(Run("Character style", new W.RunStyle { Val = "Strong" })),
                new W.Paragraph(Run(" padded ", new W.Bold())));
        });
        Assert.Equal(new[] { "Normal **bold phrase** end", "**Inherited** plain", "**Character style**", " **padded** " },
            result.BodyElements.OfType<TextElement>().Select(element => element.Text));
    }

    [Fact]
    public async Task ResolvesInheritedBulletsNestedNumberingAndInstanceRestarts()
    {
        var result = await ParseAsync(main =>
        {
            AddNumbering(main);
            main.AddNewPart<StyleDefinitionsPart>().Styles = new W.Styles(
                new W.Style(new W.StyleParagraphProperties(Numbering(2, 0))) { StyleId = "ListBase", Type = W.StyleValues.Paragraph },
                new W.Style(new W.BasedOn { Val = "ListBase" }) { StyleId = "ListDerived", Type = W.StyleValues.Paragraph });
            return new W.Body(
                List("First", 1, 0), List("Child", 1, 1), List("Second", 1, 0),
                new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "ListDerived" }), Run("Bullet", new W.Bold())),
                new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "ListDerived" }, new W.NumberingProperties(new W.NumberingId { Val = 0 })), Run("Not a list")),
                List("Restart", 3, 0), List("Next", 3, 0));
        });
        Assert.Equal(new[] { "1. First", "    - Child", "2. Second", "- **Bullet**", "Not a list", "5. Restart", "6. Next" },
            result.BodyElements.OfType<TextElement>().Select(element => element.Text));
        Assert.Empty(result.Warnings);
        Assert.Contains("- **Bullet**", new DocxDocumentParser().ConvertToMarkdown(result));
    }

    [Fact]
    public async Task KeepsBoldAndBulletTextInTableCells()
    {
        var result = await ParseAsync(main =>
        {
            AddNumbering(main);
            return new W.Body(new W.Table(new W.TableRow(
                new W.TableCell(new W.Paragraph(Run("Header", new W.Bold()))),
                new W.TableCell(new W.Paragraph(new W.ParagraphProperties(Numbering(2, 0)), Run("Item", new W.Bold()))))));
        });
        Assert.Equal("| **Header** | - **Item** |\n| --- | --- |",
            new DocxDocumentParser().ConvertToMarkdown(result).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task MissingListDefinitionPreservesTextAndReportsWarning()
    {
        var result = await ParseAsync(_ => new W.Body(List("Retained", 99, 0)));
        Assert.Equal("Retained", Assert.IsType<TextElement>(Assert.Single(result.BodyElements)).Text);
        Assert.Equal("MissingNumberingDefinition", Assert.Single(result.Warnings).Code);
    }

    private static W.Run Run(string text, params OpenXmlElement[] properties) =>
        new(new W.RunProperties(properties), new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static W.NumberingProperties Numbering(int id, int level) =>
        new(new W.NumberingLevelReference { Val = level }, new W.NumberingId { Val = id });

    private static W.Paragraph List(string text, int id, int level) =>
        new(new W.ParagraphProperties(Numbering(id, level)), Run(text));

    private static void AddNumbering(MainDocumentPart main)
    {
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = new W.Numbering(
            new W.AbstractNum(
                new W.Level(new W.StartNumberingValue { Val = 1 }, new W.NumberingFormat { Val = W.NumberFormatValues.Decimal }, new W.LevelText { Val = "%1." }) { LevelIndex = 0 },
                new W.Level(new W.StartNumberingValue { Val = 1 }, new W.NumberingFormat { Val = W.NumberFormatValues.Bullet }, new W.LevelText { Val = "•" }) { LevelIndex = 1 }) { AbstractNumberId = 0 },
            new W.AbstractNum(new W.Level(new W.NumberingFormat { Val = W.NumberFormatValues.Bullet }, new W.LevelText { Val = "•" }) { LevelIndex = 0 }) { AbstractNumberId = 1 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 0 }) { NumberID = 1 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 1 }) { NumberID = 2 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 0 }, new W.LevelOverride(new W.StartOverrideNumberingValue { Val = 5 }) { LevelIndex = 0 }) { NumberID = 3 });
    }

    private static async Task<DocxParseResult> ParseAsync(Func<MainDocumentPart, W.Body> create)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(create(main));
        }
        stream.Position = 0;
        return await new DocxDocumentParser().ParseAsync(stream);
    }
}
