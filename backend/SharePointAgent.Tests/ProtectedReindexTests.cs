using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ProtectedReindexTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("denied")]
    [InlineData("extraction failure")]
    [InlineData("canceled")]
    public async Task ReindexDecryptsBeforeExtractionAndCleansTemporaryFiles(string outcome)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        memory.Set("SharePointDrive___", new Drive { Id = "drive" });
        using var http = new HttpClient(new GraphHandler());
        using var graph = new GraphServiceClient(http);
        var metadata = Substitute.For<IFileMetadataRepository>();
        metadata.GetAsync("drive", "item", Arg.Any<CancellationToken>()).Returns(new FileIndexRecord(
            "drive", "item", "document.docx", null, null, null, 9, null, null, null,
            "", "", 1, Guid.NewGuid(), DateTimeOffset.UtcNow, 0));
        var search = Substitute.For<ISearchIndexStore>();
        var extractor = Substitute.For<IContentExtractor>();
        var protection = Substitute.For<IProtectedFileService>();
        var client = new SharePointClient(graph, memory, Options.Create(new SharePointOptions()), protection);
        string? temporaryPath = null;
        using var cancellation = new CancellationTokenSource();
        protection.EnsureReadableAsync(Arg.Any<string>(), "document.docx", 1024, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                temporaryPath = call.ArgAt<string>(0);
                Assert.Equal("encrypted", await File.ReadAllTextAsync(temporaryPath));
                File.Copy(temporaryPath, temporaryPath + ProtectedFileService.ProtectedOriginalSuffix);
                if (outcome == "denied") throw new ProtectedDocumentAccessDeniedException();
                await File.WriteAllTextAsync(temporaryPath, "decrypted");
                if (outcome == "canceled") cancellation.Cancel();
                return new FileSensitivity("2096f6a2-d2f7-48be-b329-b73aaa526e5d", "Confidential", true, true, DateTimeOffset.UtcNow);
            });
        extractor.ExtractAsync(Arg.Any<DriveItemChange>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal("decrypted", Encoding.UTF8.GetString(call.ArgAt<byte[]>(1)));
                if (outcome == "extraction failure") throw new InvalidDataException("conversion failed");
                return Task.FromResult("index this text");
            });
        var embeddings = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embeddings.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1 })]));
        var processor = new SharePointChangeProcessor(client, Substitute.For<IDeltaStateRepository>(), metadata,
            search, extractor, embeddings, Options.Create(new ProcessorOptions { MaxFileBytes = 1024, AllowedFileExtensions = [".docx"] }),
            Options.Create(new OpenAiOptions()), Options.Create(new SharePointAgent.Application.SearchOptions()),
            NullLogger<SharePointChangeProcessor>.Instance);

        var error = await Record.ExceptionAsync(() => processor.ReindexAsync("drive", "item", cancellation.Token));

        Assert.True(temporaryPath is not null, error?.ToString() ?? "Reindex returned before downloading.");
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporaryPath)));
        if (outcome == "success")
        {
            Assert.Null(error);
            await search.Received(1).ReplaceItemAsync("drive", "item",
                Arg.Is<IReadOnlyList<SearchChunkDocument>>(chunks => chunks.Count == 1 && chunks[0].Content == "index this text"), Arg.Any<CancellationToken>());
            await metadata.Received(1).SaveAsync(Arg.Is<FileIndexRecord>(record => record.Sensitivity != null
                && record.Sensitivity.LabelName == "Confidential" && record.Sensitivity.IsEncrypted), Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.NotNull(error);
            if (outcome == "denied") Assert.IsType<ProtectedDocumentAccessDeniedException>(error);
            if (outcome == "canceled") Assert.IsAssignableFrom<OperationCanceledException>(error);
            await search.DidNotReceive().ReplaceItemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchChunkDocument>>(), Arg.Any<CancellationToken>());
            await metadata.DidNotReceive().SaveAsync(Arg.Any<FileIndexRecord>(), Arg.Any<CancellationToken>());
            if (outcome is "denied" or "canceled")
                await extractor.DidNotReceive().ExtractAsync(Arg.Any<DriveItemChange>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        }
    }

    private sealed class GraphHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/content") ? "encrypted"
                : path.EndsWith("/permissions") ? "{\"value\":[]}"
                : "{\"id\":\"item\",\"name\":\"document.docx\",\"file\":{\"mimeType\":\"application/vnd.openxmlformats-officedocument.wordprocessingml.document\"},\"size\":9}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, path.EndsWith("/content") ? "application/octet-stream" : "application/json")
            });
        }
    }
}
