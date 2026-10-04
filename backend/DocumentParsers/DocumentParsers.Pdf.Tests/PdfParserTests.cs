using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using NSubstitute;
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
        var parser = new PdfDocumentParser(Substitute.For<DocumentIntelligenceClient>());
        using var stream = new MemoryStream("broken file"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAsync(stream, cancelled.Token));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task MapsLayoutWithoutDuplicateTableParagraphsAndDownloadsFigures()
    {
        var region = F.BoundingRegion(1, [0, 0, 2, 0, 2, 1, 0, 1]);
        var analysis = F.AnalyzeResult(modelId: "prebuilt-layout", content: "TitleCellCaption",
            paragraphs:
            [
                F.DocumentParagraph(role: ParagraphRole.Title, content: "Title", boundingRegions: [region], spans: [F.DocumentSpan(0, 5)]),
                F.DocumentParagraph(content: "Cell", boundingRegions: [region], spans: [F.DocumentSpan(5, 4)])
            ],
            tables: [F.DocumentTable(rowCount: 1, columnCount: 2,
                cells: [F.DocumentTableCell(rowIndex: 0, columnIndex: 0, content: "Cell|Value")],
                boundingRegions: [region], spans: [F.DocumentSpan(5, 4)])],
            figures: [F.DocumentFigure(id: "1.1", boundingRegions: [region], spans: [F.DocumentSpan(9, 7)])]);
        var client = Substitute.For<DocumentIntelligenceClient>();
        var operation = new CompletedOperation(analysis);
        client.AnalyzeDocumentAsync(WaitUntil.Started, Arg.Any<AnalyzeDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Operation<AnalyzeResult>>(operation));
        client.GetAnalyzeResultFigureAsync("prebuilt-layout", "result-id", "1.1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Response.FromValue(BinaryData.FromBytes(new byte[] { 1, 2 }), new TestResponse())));
        var parser = new PdfDocumentParser(client);
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test"));
        var result = await parser.ParseAsync(stream);
        Assert.Collection(result.Elements,
            element => Assert.IsType<HeadingElement>(element),
            element => Assert.IsType<TableElement>(element),
            element => Assert.IsType<ImageElement>(element));
        Assert.Equal(new DocumentBoundingBox(0, 0, 2, 1), result.Elements[0].BoundingBox);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<ImageElement>(result.Elements[2]).Data);
        Assert.Equal(9, result.Elements[2].Order);
        var markdown = parser.ConvertToMarkdown(result);
        Assert.Contains("<!-- Page 1 -->", markdown);
        Assert.Contains("Cell\\|Value", markdown);
        Assert.DoesNotContain("\nCell\n", markdown);
        Assert.True(stream.CanRead);
        await client.Received(1).AnalyzeDocumentAsync(WaitUntil.Started,
            Arg.Is<AnalyzeDocumentOptions>(options => options.Output.Contains(AnalyzeOutputOption.Figures)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ParagraphWithoutBoundingRegionIsRetained()
    {
        var parser = new PdfDocumentParser(Substitute.For<DocumentIntelligenceClient>());
        var result = parser.Map(F.AnalyzeResult(modelId: "layout", paragraphs: [F.DocumentParagraph(content: "Text")]), default);
        Assert.Null(Assert.Single(result.Elements).BoundingBox);
        Assert.Contains("Text", parser.ConvertToMarkdown(result));
    }

    [Fact]
    public async Task RejectsNonPdfBeforeCallingAzure()
    {
        var client = Substitute.For<DocumentIntelligenceClient>();
        var parser = new PdfDocumentParser(client);
        using var stream = new MemoryStream("invalid"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(stream));
        Assert.Empty(client.ReceivedCalls());
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
            [new("Operation-Location", "https://example.test/documentModels/prebuilt-layout/analyzeResults/result-id?api-version=2024-11-30")];

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
