using NSubstitute;
using Xunit;

namespace DocumentParsers.Tests;

public sealed class DispatchTests
{
    [Theory]
    [InlineData("report.PDF", "application/pdf", DocumentFormat.Pdf)]
    [InlineData("report.docx", null, DocumentFormat.Docx)]
    [InlineData("report.pptx", "application/octet-stream", DocumentFormat.Pptx)]
    [InlineData("report.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", DocumentFormat.Xlsx)]
    public void ResolvesSupportedFormats(string name, string? mime, DocumentFormat expected)
    {
        Assert.Equal(expected, new DocumentFormatResolver().Resolve(name, mime));
    }

    [Fact]
    public void RejectsMismatchAndLegacyFormats()
    {
        var resolver = new DocumentFormatResolver();
        Assert.Throws<InvalidDataException>(() => resolver.Resolve("test.pdf", "image/png"));
        Assert.Throws<NotSupportedException>(() => resolver.Resolve("test.doc", null));
    }

    [Fact]
    public async Task DispatchesToTypedParserAndEnrichesBeforeMarkdownConversion()
    {
        var pdf = Substitute.For<IPdfDocumentParser>();
        var docx = Substitute.For<IDocxDocumentParser>();
        var pptx = Substitute.For<IPptxDocumentParser>();
        var xlsx = Substitute.For<IXlsxDocumentParser>();
        var analysis = Substitute.For<IImageAnalysisService>();
        analysis.DescribeAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("Diagram");
        var result = new DocxParseResult();
        result.BodyElements.Add(new ImageElement { Data = [1], ContentType = "image/png" });
        result.Metadata["Title"] = "Example";
        docx.ParseAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(result);
        docx.ConvertToMarkdown(result, Arg.Any<CancellationToken>()).Returns(call => ((ImageElement)result.BodyElements[0]).Description!);
        var service = new DocumentConversionService(pdf, docx, pptx, xlsx, new(analysis));
        using var stream = new MemoryStream();
        var output = await service.ConvertAsync(stream, "test.docx");
        Assert.Equal("Diagram", output.Markdown);
        Assert.Equal("Example", output.Metadata["Title"]);
        Assert.Empty(pdf.ReceivedCalls());
        Assert.Empty(pptx.ReceivedCalls());
        Assert.Empty(xlsx.ReceivedCalls());
    }

    [Fact]
    public async Task ConversionServiceSkipsEnrichmentAndForwardsOption()
    {
        var result = new DocxParseResult();
        var image = new ImageElement { Data = [1], ContentType = "image/png" };
        result.BodyElements.Add(image);
        var docx = Substitute.For<IDocxDocumentParser>();
        docx.ParseAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(result);
        docx.ConvertToMarkdown(result, Arg.Any<CancellationToken>(), true).Returns("Text only");
        var analysis = Substitute.For<IImageAnalysisService>();
        var service = new DocumentConversionService(
            Substitute.For<IPdfDocumentParser>(), docx,
            Substitute.For<IPptxDocumentParser>(), Substitute.For<IXlsxDocumentParser>(), new(analysis));
        using var stream = new MemoryStream();
        var output = await service.ConvertAsync(stream, "test.docx", imageOptions: new() { ExtractText = true }, skipImages: true);
        Assert.Equal("Text only", output.Markdown);
        Assert.Empty(analysis.ReceivedCalls());
        Assert.Null(image.Description);
        Assert.Null(image.ExtractedText);
        Assert.Same(image, Assert.Single(result.BodyElements));
    }
}
