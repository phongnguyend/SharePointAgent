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

public sealed class DocxParserTests
{
    [Fact]
    public async Task DocxPreservesInlineImageOrderCustomHeadingAndTableEscaping()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            var stylePart = main.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles = new W.Styles(new W.Style(new W.StyleName { Val = "Heading 2" }) { StyleId = "CustomHeading" });
            var image = main.AddImagePart(ImagePartType.Png);
            using var bytes = new MemoryStream([1, 2, 3]);
            image.FeedData(bytes);
            var drawing = new W.Drawing(new DW.Inline(new A.Graphic(new A.GraphicData(
                new Pic.Picture(new Pic.BlipFill(new A.Blip { Embed = main.GetIdOfPart(image) })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "CustomHeading" }), new W.Run(new W.Text("Title"))),
                new W.Paragraph(new W.Run(new W.Text("Before"), drawing, new W.Text("After"))),
                new W.Table(new W.TableRow(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("A|B")))), new W.TableCell(new W.Paragraph())))));
        }
        stream.Position = 0;
        var parser = new DocxDocumentParser();
        var result = await parser.ParseAsync(stream);
        Assert.True(stream.CanRead);
        Assert.Equal(2, Assert.IsType<HeadingElement>(result.BodyElements[0]).Level);
        Assert.Equal("Before", Assert.IsType<TextElement>(result.BodyElements[1]).Text);
        var extractedImage = Assert.IsType<ImageElement>(result.BodyElements[2]);
        Assert.Equal(new byte[] { 1, 2, 3 }, extractedImage.Data);
        Assert.Equal("After", Assert.IsType<TextElement>(result.BodyElements[3]).Text);
        extractedImage.Description = "A diagram";
        extractedImage.ExtractedText = "Label";
        var markdown = parser.ConvertToMarkdown(result);
        Assert.Contains("## Title", markdown);
        Assert.Contains("A\\|B", markdown);
        Assert.Contains("Description: A diagram", markdown);
        Assert.Contains("Extracted text (OCR): Label", markdown);
        Assert.True(markdown.IndexOf("Before", StringComparison.Ordinal) < markdown.IndexOf("[Image]", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("[Image]", StringComparison.Ordinal) < markdown.IndexOf("After", StringComparison.Ordinal));
        Assert.Null(result.BodyElements[0].PageNumber);
    }

    [Fact]
    public async Task LimitsInputWithoutDisposingCallerStream()
    {
        using var stream = new MemoryStream(new byte[100]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new DocxDocumentParser(new DocxParserOptions { MaxInputBytes = 10 }).ParseAsync(stream));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task RejectsExcessiveExpandedZipBeforeOpeningDocument()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("bomb.xml").Open();
            entry.Write(new byte[10000]);
        }
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => new DocxDocumentParser(new DocxParserOptions { MaxExpandedBytes = 100 }).ParseAsync(stream));
    }

    [Fact]
    public async Task ParserHonorsCancellationAndRejectsCorruptInput()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var parser = new DocxDocumentParser();
        using var stream = new MemoryStream("broken file"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAsync(stream, cancelled.Token));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
        Assert.True(stream.CanRead);
    }
}
