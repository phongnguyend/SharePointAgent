using NSubstitute;
using Xunit;

namespace DocumentParsers.Tests;

public sealed class ImageProcessorTests
{
    [Fact]
    public async Task DeduplicatesByImageAndContextButKeepsOcrSeparate()
    {
        var service = Substitute.For<IImageAnalysisService>();
        service.DescribeAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("Description");
        service.ExtractTextAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("OCR");
        var images = new[] { Image("One"), Image("One"), Image("Two") };
        var warnings = await new ImageDescriptionProcessor(service).ProcessAsync(images, new() { ExtractText = true });
        Assert.Empty(warnings);
        Assert.All(images, image =>
        {
            Assert.Equal("Description", image.Description);
            Assert.Equal("OCR", image.ExtractedText);
        });
        await service.Received(2).DescribeAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await service.Received(1).ExtractTextAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DescriptionFailureDoesNotDiscardSuccessfulEmptyOcr()
    {
        var service = Substitute.For<IImageAnalysisService>();
        service.DescribeAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HttpRequestException("Do not expose provider details")));
        service.ExtractTextAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("");
        var image = Image("One");
        var warnings = await new ImageDescriptionProcessor(service).ProcessAsync([image], new() { ExtractText = true });
        Assert.Equal("DescriptionFailed", Assert.Single(warnings).Code);
        Assert.Null(image.Description);
        Assert.Equal("", image.ExtractedText);
    }

    [Fact]
    public async Task OcrIsOptionalAndCancellationPropagates()
    {
        var service = Substitute.For<IImageAnalysisService>();
        service.DescribeAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("Description");
        var processor = new ImageDescriptionProcessor(service);
        var image = Image("One");
        await processor.ProcessAsync([image]);
        Assert.Null(image.ExtractedText);
        await service.DidNotReceive().ExtractTextAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync([image], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ConcurrentRequestsStayWithinConfiguredLimit()
    {
        var service = new CountingService();
        var images = Enumerable.Range(0, 20).Select(index => Image(index.ToString()));
        await new ImageDescriptionProcessor(service).ProcessAsync(images, new() { MaxConcurrency = 2 });
        Assert.InRange(service.Peak, 1, 2);
    }

    private static ImageElement Image(string caption) => new() { Data = [1, 2, 3], ContentType = "image/png", Caption = caption };

    private sealed class CountingService : IImageAnalysisService
    {
        private int _active;
        public int Peak;

        public async Task<string> DescribeAsync(ReadOnlyMemory<byte> image, string contentType, string? contextualText = null, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdatePeak(active);
            await Task.Delay(5, cancellationToken);
            Interlocked.Decrement(ref _active);
            return "Description";
        }

        private void UpdatePeak(int active)
        {
            int previous;
            do
            {
                previous = Peak;
            }
            while (active > previous && Interlocked.CompareExchange(ref Peak, active, previous) != previous);
        }

        public Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, CancellationToken cancellationToken = default) => Task.FromResult("");
    }
}
