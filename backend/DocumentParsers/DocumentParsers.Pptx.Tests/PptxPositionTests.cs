using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DocumentParsers.Tests;

public sealed class PptxPositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InheritedFooterPositionDoesNotSortBeforeTitle(bool fromMaster, bool localOverride)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var main = document.AddPresentationPart();
            var slide = main.AddNewPart<SlidePart>();
            // Date comes first in XML but last visually. Layout inherits from master by type,
            // even when the master placeholder uses a different index.
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                Shape("July 2026", P.PlaceholderValues.DateAndTime, 10, localOverride ? 650 : null),
                Shape("Clean Architecture", P.PlaceholderValues.Title, 0, 100),
                Shape("Technical Team", P.PlaceholderValues.SubTitle, 1, 590))));
            var layout = slide.AddNewPart<SlideLayoutPart>();
            layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(new P.ShapeTree(
                Shape("", P.PlaceholderValues.DateAndTime, 10, fromMaster ? null : localOverride ? 10 : 650))));
            var master = layout.AddNewPart<SlideMasterPart>();
            master.SlideMaster = new P.SlideMaster(new P.CommonSlideData(new P.ShapeTree(
                Shape("", P.PlaceholderValues.DateAndTime, 4, 650))));
            main.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId { Id = 256, RelationshipId = main.GetIdOfPart(slide) }));
        }
        stream.Position = 0;
        var parser = new PptxDocumentParser();
        var result = await parser.ParseAsync(stream);
        var elements = Assert.Single(result.Slides).Elements;
        Assert.Equal("Clean Architecture", Assert.IsType<HeadingElement>(elements[0]).Text);
        Assert.Equal("Technical Team", Assert.IsType<TextElement>(elements[1]).Text);
        Assert.Equal("July 2026", Assert.IsType<TextElement>(elements[2]).Text);
        Assert.Equal(650, elements[2].BoundingBox!.Y);
        Assert.Equal("<!-- Slide number: 1 -->\n\n# Clean Architecture\n\nTechnical Team\n\nJuly 2026",
            parser.ConvertToMarkdown(result).Replace("\r\n", "\n"));
    }

    [Fact]
    public void UnknownPositionsStayAfterKnownPositionsInSourceOrder()
    {
        var unknown = new TextElement("Unknown");
        var other = new TextElement("Other");
        var known = new TextElement("Known") { BoundingBox = new(100, 100, 100, 100) };
        Assert.Equal(new DocumentElement[] { known, unknown, other }, new PositionReadingOrderResolver().Resolve([unknown, known, other]));
    }

    private static P.Shape Shape(string text, P.PlaceholderValues type, uint index, long? y)
    {
        var properties = new P.ShapeProperties();
        if (y is not null)
        {
            properties.Transform2D = new A.Transform2D(new A.Offset { X = 90, Y = y.Value }, new A.Extents { Cx = 500, Cy = 40 });
        }
        return new P.Shape(new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = index + 1, Name = "Placeholder" },
                new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = type, Index = index })),
            properties, new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text(text)))));
    }
}
