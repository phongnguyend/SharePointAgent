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

public sealed class PptxParserTests
{
    [Fact]
    public async Task ExtractsSpeakerNotesAndExcludesNotesPagePlaceholders()
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var main = document.AddPresentationPart();
            var slide = main.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(Shape("Slide content", 0, 0))));
            var notes = slide.AddNewPart<NotesSlidePart>();
            var body = NotesShape(P.PlaceholderValues.Body, "First note");
            body.TextBody!.Append(new A.Paragraph(new A.Run(new A.Text("Second note"))));
            notes.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(
                body, NotesShape(P.PlaceholderValues.SlideNumber, "99"),
                NotesShape(P.PlaceholderValues.Footer, "Footer to exclude"),
                NotesShape(P.PlaceholderValues.DateAndTime, "Date to exclude"))));
            main.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId
            {
                Id = 256,
                RelationshipId = main.GetIdOfPart(slide)
            }));
        }
        stream.Position = 0;
        var parser = new PptxDocumentParser();
        var result = await parser.ParseAsync(stream);
        Assert.Equal("First note\n\nSecond note", Assert.Single(result.Slides).Notes);
        var paddedNotes = " \r\nFirst note\n\nSecond note\r\n\t ";
        result.Slides[0] = result.Slides[0] with { Notes = paddedNotes };
        var markdown = parser.ConvertToMarkdown(result, skipImages: true).Replace("\r\n", "\n");
        Assert.Equal("<!-- Slide number: 1 -->\n\nSlide content\n\n### Notes:\n\nFirst note\n\nSecond note", markdown);
        Assert.DoesNotContain("# Slide", markdown);
        Assert.Equal(paddedNotes, result.Slides[0].Notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void EmptyNotesDoNotProduceNotesHeading(string? notes)
    {
        var result = new PptxParseResult();
        result.Slides.Add(new(3, []) { Notes = notes });
        Assert.Equal("<!-- Slide number: 3 -->", new PptxDocumentParser().ConvertToMarkdown(result));
    }

    private static P.Shape NotesShape(P.PlaceholderValues type, string text) => new(
        new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "Notes" },
            new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = type })),
        new P.ShapeProperties(), new P.TextBody(new A.BodyProperties(), new A.Paragraph(new A.Run(new A.Text(text)))));

    [Fact]
    public async Task PptxUsesPresentationOrderGroupCoordinatesAndKeepsEmptySlides()
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var main = document.AddPresentationPart();
            var slide = main.AddNewPart<SlidePart>();
            var group = new P.GroupShape(new P.GroupShapeProperties(new A.TransformGroup(
                new A.Offset { X = 0, Y = 100 }, new A.Extents { Cx = 100, Cy = 100 },
                new A.ChildOffset { X = 0, Y = 0 }, new A.ChildExtents { Cx = 100, Cy = 100 })), Shape("Grouped", 0, 0));
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(Shape("Bottom", 0, 200), group, Shape("Top", 0, 0), new P.GraphicFrame())));
            var empty = main.AddNewPart<SlidePart>();
            empty.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree()));
            main.Presentation = new P.Presentation(new P.SlideIdList(
                new P.SlideId { Id = 256, RelationshipId = main.GetIdOfPart(slide) },
                new P.SlideId { Id = 257, RelationshipId = main.GetIdOfPart(empty) }));
        }
        stream.Position = 0;
        var parser = new PptxDocumentParser();
        var result = await parser.ParseAsync(stream);
        Assert.Equal(2, result.Slides.Count);
        Assert.Equal(new[] { "Top", "Grouped", "Bottom" }, result.Slides[0].Elements.OfType<TextElement>().Select(element => element.Text));
        Assert.Equal(100, result.Slides[0].Elements[1].BoundingBox!.Y);
        Assert.Empty(result.Slides[1].Elements);
        Assert.Contains(result.Warnings, warning => warning.Code == "UnsupportedGraphic");
        Assert.Contains("<!-- Slide number: 2 -->", parser.ConvertToMarkdown(result));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void MarkdownConversionKeepsMetadataWarningsAndSourceOrdering()
    {
        var result = new PptxParseResult();
        var elements = new DocumentElement[] { new TextElement("Z"), new TextElement("A") };
        result.Slides.Add(new(1, elements));
        result.Metadata["Title"] = "Example";
        result.Warnings.Add(new("Example", "Warning"));
        var parser = new PptxDocumentParser();
        Assert.Equal(parser.ConvertToMarkdown(result), parser.ConvertToMarkdown(result));
        Assert.Same(elements, result.Slides[0].Elements);
        Assert.Equal("Example", result.Metadata["Title"]);
        Assert.Single(result.Warnings);
    }

    private static P.Shape Shape(string text, long x, long y) => new(
        new P.ShapeProperties(new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = 100, Cy = 100 })),
        new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text(text)))));

    [Fact]
    public async Task ParserHonorsCancellationAndRejectsCorruptInput()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var parser = new PptxDocumentParser();
        using var stream = new MemoryStream("broken file"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAsync(stream, cancelled.Token));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
        Assert.True(stream.CanRead);
    }
}
