using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;
using F = Azure.AI.DocumentIntelligence.DocumentIntelligenceModelFactory;

namespace DocumentParsers.Tests;

public sealed class ImageAnalysisServiceTests
{
    [Fact]
    public async Task DescriptionSendsImageAndContextToLlmWithoutCallingOcr()
    {
        var vision = Substitute.For<IChatClient>();
        var ocr = Substitute.For<DocumentIntelligenceClient>();
        vision.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Architecture diagram")));
        var service = new ImageAnalysisService(vision, ocr, "vision-model");
        var result = await service.DescribeAsync(new byte[] { 1, 2, 3 }, "image/png", "Source caption");
        Assert.Equal("Architecture diagram", result);
        await vision.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(messages => messages.Any(message => message.Contents.OfType<DataContent>().Any()) &&
                messages.Any(message => message.Text.Contains("Source caption"))),
            Arg.Is<ChatOptions>(options => options.ModelId == "vision-model"), Arg.Any<CancellationToken>());
        Assert.Empty(ocr.ReceivedCalls());
    }

    [Theory]
    [InlineData(true, "First line\nSecond line")]
    [InlineData(false, "")]
    public async Task OcrUsesReadModelAndPreservesLinesWithoutCallingLlm(bool hasText, string expected)
    {
        var vision = Substitute.For<IChatClient>();
        var ocr = Substitute.For<DocumentIntelligenceClient>();
        var operation = Substitute.For<Operation<AnalyzeResult>>();
        operation.Value.Returns(F.AnalyzeResult(modelId: "prebuilt-read", pages:
            [F.DocumentPage(pageNumber: 1, lines: hasText ? [F.DocumentLine(content: "First line"), F.DocumentLine(content: "Second line")] : [])]));
        ocr.AnalyzeDocumentAsync(WaitUntil.Completed, Arg.Any<AnalyzeDocumentOptions>(), Arg.Any<CancellationToken>()).Returns(operation);
        var result = await new ImageAnalysisService(vision, ocr).ExtractTextAsync(new byte[] { 1 }, "image/png");
        Assert.Equal(expected, result);
        await ocr.Received(1).AnalyzeDocumentAsync(WaitUntil.Completed,
            Arg.Is<AnalyzeDocumentOptions>(options => options.ModelId == "prebuilt-read"), Arg.Any<CancellationToken>());
        Assert.Empty(vision.ReceivedCalls());
    }
}
