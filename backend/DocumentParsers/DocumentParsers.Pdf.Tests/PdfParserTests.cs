using Azure;
using Azure.AI.DocumentIntelligence;
using NSubstitute;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using F = Azure.AI.DocumentIntelligence.DocumentIntelligenceModelFactory;

namespace DocumentParsers.Tests;

public sealed class PdfParserTests
{
    [Fact]
    public async Task ParserHonorsCancellationAndRejectsCorruptInput()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var parser = new PdfDocumentParser();
        using var stream = new MemoryStream("broken file"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAsync(stream, cancelled.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task NativeTextUsesLocalLayoutAndNeverCallsAzure()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 800);
        page.AddText("Bottom paragraph", 12, new PdfPoint(40, 100), font);
        page.AddText("Top paragraph", 12, new PdfPoint(40, 700), font);
        builder.AddPage(600, 800).AddText("Second page", 12, new PdfPoint(40, 700), font);
        var client = Substitute.For<DocumentIntelligenceClient>();
        var parser = new PdfDocumentParser(client);
        using var stream = new MemoryStream(builder.Build());
        var result = await parser.ParseAsync(stream);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(new[] { "Top paragraph", "Bottom paragraph", "Second page" },
            result.Elements.OfType<TextElement>().Select(element => element.Text));
        Assert.Equal(new int?[] { 1, 1, 2 }, result.Elements.Select(element => element.PageNumber));
        Assert.Equal(new long?[] { 0, 1, 2 }, result.Elements.Select(element => element.Order));
        Assert.InRange(result.Elements[0].BoundingBox!.Y, 80, 110);
        Assert.Equal("point", result.Metadata["Page1.Unit"]);
        Assert.Contains("<!-- Page 2 -->", parser.ConvertToMarkdown(result));
        Assert.Empty(client.ReceivedCalls());
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task MissingOcrClientRetainsLocalContentAndWarns()
    {
        using var stream = MixedDocument();
        var result = await new PdfDocumentParser().ParseAsync(stream);
        Assert.Equal("Native text", Assert.Single(result.Elements.OfType<TextElement>()).Text);
        Assert.Contains(result.Warnings, warning => warning.Code == "OcrNotConfigured");
    }

    [Fact]
    public async Task OcrRequestsReadModelOnlyForPagesWithoutNativeText()
    {
        var analysis = F.AnalyzeResult(modelId: "prebuilt-read", pages:
        [
            F.DocumentPage(pageNumber: 1, lines: [F.DocumentLine(content: "Do not duplicate native text")]),
            F.DocumentPage(pageNumber: 2, width: 6, height: 8,
                lines: [F.DocumentLine(content: "Scanned text", polygon: [1, 1, 3, 1, 3, 2, 1, 2])])
        ]);
        var client = Substitute.For<DocumentIntelligenceClient>();
        client.AnalyzeDocumentAsync(WaitUntil.Started, Arg.Any<AnalyzeDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Operation<AnalyzeResult>>(new CompletedOperation(analysis)));
        using var stream = MixedDocument();
        var result = await new PdfDocumentParser(client).ParseAsync(stream);
        Assert.Equal(new[] { "Native text", "Scanned text" }, result.Elements.OfType<TextElement>().Select(element => element.Text));
        Assert.Equal(new DocumentBoundingBox(100, 100, 200, 100), result.Elements[1].BoundingBox);
        Assert.Equal(2, result.Elements[1].PageNumber);
        Assert.Equal("prebuilt-read", result.Metadata["OcrModelId"]);
        await client.Received(1).AnalyzeDocumentAsync(WaitUntil.Started,
            Arg.Is<AnalyzeDocumentOptions>(options => options.ModelId == "prebuilt-read" && options.Pages == "2" && options.Output.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InputLimitIsEnforcedBeforeParsing()
    {
        using var stream = MixedDocument();
        var parser = new PdfDocumentParser(options: new ParserOptions { MaxInputBytes = 5 });
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
    }

    [Fact]
    public async Task EmbeddedImagesAreLocalAndCanBeOmittedFromMarkdown()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        page.AddPng(png, new PdfRectangle(20, 30, 120, 130));
        using var stream = new MemoryStream(builder.Build());
        var parser = new PdfDocumentParser();
        var result = await parser.ParseAsync(stream);
        var image = Assert.Single(result.Elements.OfType<ImageElement>());
        Assert.Equal("image/png", image.ContentType);
        Assert.NotEmpty(image.Data);
        Assert.Equal(new DocumentBoundingBox(20, 670, 100, 100), image.BoundingBox);
        var skipped = parser.ConvertToMarkdown(result, skipImages: true);
        Assert.Contains("<!-- Page 1 -->", skipped);
        Assert.Matches(@"<!-- image: image-[a-f0-9]{64}\.png -->", skipped);
        Assert.DoesNotContain("[Image]", skipped);
        Assert.Equal(skipped, parser.ConvertToMarkdown(result, skipImages: true));
    }

    private static MemoryStream MixedDocument()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(600, 800).AddText("Native text", 12, new PdfPoint(40, 700), font);
        builder.AddPage(600, 800);
        return new MemoryStream(builder.Build());
    }

    private sealed class CompletedOperation(AnalyzeResult result) : Operation<AnalyzeResult>
    {
        public override string Id => "result-id";

        public override AnalyzeResult Value => result;

        public override bool HasCompleted => true;

        public override bool HasValue => true;

        public override Response GetRawResponse() => new TestResponse();

        public override Response UpdateStatus(CancellationToken cancellationToken = default) => GetRawResponse();

        public override ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(GetRawResponse());
    }

    private sealed class TestResponse : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = "test";

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => name == "Operation-Location";

        protected override IEnumerable<Azure.Core.HttpHeader> EnumerateHeaders() =>
            [new("Operation-Location", "https://example.test/documentModels/prebuilt-read/analyzeResults/result-id?api-version=2024-11-30")];

        protected override bool TryGetHeader(string name, out string value)
        {
            value = EnumerateHeaders().First().Value;
            return ContainsHeader(name);
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = [EnumerateHeaders().First().Value];
            return ContainsHeader(name);
        }
    }
}
