using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DocumentParsers.Tests;

public sealed class PptxListTests
{
    [Fact]
    public async Task AgendaKeepsNumbersAndNestedBullets()
    {
        var paragraphs = new[]
        {
            Paragraph("Software Architecture in General", 0, Number()),
            Paragraph("Loose coupling and high cohesion", 1, new A.CharacterBullet { Char = "▪" }),
            Paragraph("SOLID", 1, new A.CharacterBullet { Char = "▪" }),
            Paragraph("Clean Architecture: core layers and components", 0, Number()),
            Paragraph("Clean Architecture in Practices", 0, Number()),
            Paragraph("Samples and Demo", 1, new A.CharacterBullet { Char = "▪" }),
            Paragraph("Testing in Clean Architecture", 0, Number()),
            Paragraph("Clean Architecture for Modern Architecture", 0, Number()),
            Paragraph("Microservices (MSA)", 1, new A.CharacterBullet { Char = "▪" }),
            Paragraph("Serverless", 1, new A.CharacterBullet { Char = "▪" }),
            Paragraph("Clean Architecture, and beyond", 0, Number()),
            Paragraph("Vertical Slice Architecture", 1, new A.CharacterBullet { Char = "▪" }),
            Paragraph("Appendix: Hexagonal Architecture vs Onion Architecture", 0, Number())
        };
        var markdown = await ConvertAsync(new A.ListStyle(), paragraphs);
        Assert.Equal("""
            <!-- Slide number: 1 -->

            1. Software Architecture in General
                - Loose coupling and high cohesion
                - SOLID
            2. Clean Architecture: core layers and components
            3. Clean Architecture in Practices
                - Samples and Demo
            4. Testing in Clean Architecture
            5. Clean Architecture for Modern Architecture
                - Microservices (MSA)
                - Serverless
            6. Clean Architecture, and beyond
                - Vertical Slice Architecture
            7. Appendix: Hexagonal Architecture vs Onion Architecture
            """.Replace("\r\n", "\n"), markdown);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("layout")]
    [InlineData("master")]
    [InlineData("presentation")]
    public async Task InheritsListStylesAndHonorsNoBullet(string source)
    {
        var style = new A.ListStyle(new A.Level1ParagraphProperties(Number(3)),
            new A.Level2ParagraphProperties(new A.CharacterBullet { Char = "•" }));
        var markdown = await ConvertAsync(style,
        [
            Paragraph("First", 0),
            Paragraph("Child", 1),
            Paragraph("Second", 0),
            Paragraph("No marker", 0, new A.NoBullet()),
            Paragraph("New list", 0)
        ], source);
        Assert.Contains("3. First\n    - Child\n4. Second\n\nNo marker\n\n3. New list", markdown);
        Assert.DoesNotContain("- No marker", markdown);
    }

    [Fact]
    public async Task NestedNumberingRestartsAndLineBreaksStayInsideItem()
    {
        var multiline = Paragraph("First line", 1, Number());
        multiline.Append(new A.Break(), new A.Run(new A.Text("Second line")));
        var markdown = await ConvertAsync(new A.ListStyle(),
        [
            Paragraph("Parent", 0, Number(10)), multiline,
            Paragraph("Next child", 1, Number()),
            Paragraph("Next parent", 0, Number(10)),
            Paragraph("Restart child", 1, Number())
        ]);
        Assert.Contains("10. Parent\n    1. First line\n       Second line\n    2. Next child\n11. Next parent\n    1. Restart child", markdown);
    }

    private static A.AutoNumberedBullet Number(int start = 1) => new()
    {
        Type = A.TextAutoNumberSchemeValues.ArabicPeriod,
        StartAt = start
    };

    private static A.Paragraph Paragraph(string text, int level, OpenXmlElement? bullet = null)
    {
        var properties = new A.ParagraphProperties { Level = level };
        if (bullet is not null)
        {
            properties.Append(bullet);
        }
        return new A.Paragraph(properties, new A.Run(new A.Text(text)));
    }

    private static async Task<string> ConvertAsync(A.ListStyle style, A.Paragraph[] paragraphs, string source = "shape")
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var main = document.AddPresentationPart();
            var slide = main.AddNewPart<SlidePart>();
            var shape = Shape(source == "shape" ? style : new A.ListStyle(), paragraphs);
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(shape)));
            main.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId { Id = 256, RelationshipId = main.GetIdOfPart(slide) }));
            if (source == "layout")
            {
                var layout = slide.AddNewPart<SlideLayoutPart>();
                layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(new P.ShapeTree(Shape(style, []))));
            }
            else if (source == "master")
            {
                var layout = slide.AddNewPart<SlideLayoutPart>();
                layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(new P.ShapeTree()));
                var master = layout.AddNewPart<SlideMasterPart>();
                master.SlideMaster = new P.SlideMaster(new P.CommonSlideData(new P.ShapeTree()),
                    new P.TextStyles(new P.BodyStyle(style.ChildElements.Select(element => element.CloneNode(true)))));
            }
            else if (source == "presentation")
            {
                main.Presentation.Append(new P.DefaultTextStyle(style.ChildElements.Select(element => element.CloneNode(true))));
            }
        }
        stream.Position = 0;
        var parser = new PptxDocumentParser();
        var result = await parser.ParseAsync(stream);
        return parser.ConvertToMarkdown(result).Replace("\r\n", "\n");
    }

    private static P.Shape Shape(A.ListStyle style, A.Paragraph[] paragraphs)
    {
        var body = new P.TextBody(new A.BodyProperties(), style);
        body.Append(paragraphs);
        return new P.Shape(new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "Body" },
            new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties(
                new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1 })), new P.ShapeProperties(), body);
    }
}
