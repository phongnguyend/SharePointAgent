using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;

namespace SharePointAgent.Tests;

/// <summary>
/// The only change to the existing pipeline is a best-effort signal after indexing. These tests pin that it
/// fires at the right point, that it can never fail indexing, and that nothing graph-related is registered
/// or validated while the feature flags are off.
/// </summary>
public sealed class GraphRagIntegrationHookTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();

    [Fact]
    public void WithTheFlagsOffNothingIsRegisteredButANoOpHook()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();

        services.AddGraphIndexingSignal(configuration);
        services.AddGraphIndexingServices(configuration);
        services.AddGraphRetrievalServices(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<NullGraphIndexingSignal>(provider.GetRequiredService<IGraphIndexingSignal>());
        Assert.Null(provider.GetService<IGraphReader>());
        Assert.Null(provider.GetService<IGraphRetrievalService>());
        Assert.Null(provider.GetService<IGraphIndexingPipeline>());
    }

    [Fact]
    public void EnabledRetrievalRequiresAGraphStoreToBeConfigured()
    {
        var services = new ServiceCollection();
        services.AddGraphRetrievalServices(Configuration(("GraphRag:RetrievalEnabled", "true"), ("GraphRag:Cosmos:UsedManagedIdentity", "true")));

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GraphRagOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("GraphRag:Cosmos:Endpoint"));
    }

    [Fact]
    public void EnabledIndexingRequiresTheArchiveAndAValidOntology()
    {
        var services = new ServiceCollection();
        services.AddGraphIndexingServices(Configuration(
            ("GraphRag:IndexingEnabled", "true"),
            ("GraphRag:Cosmos:Endpoint", "https://graph.documents.azure.com:443/"),
            ("GraphRag:Ontology:Version", "bad version!"),
            ("GraphRag:Ontology:EntityTypes:0:Name", "System")));

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GraphRagOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("GraphRag:Archive"));
        Assert.Contains(exception.Failures, failure => failure.Contains("GraphRag:Ontology"));
    }

    [Fact]
    public void ConfiguredRetrievalRegistersTheVerifiedPathOnly()
    {
        var services = new ServiceCollection();
        services.AddGraphRetrievalServices(Configuration(("GraphRag:ShadowRetrieval", "true"), ("GraphRag:Cosmos:Endpoint", "https://graph.documents.azure.com:443/")));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGraphRetrievalService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAuthorizedChunkStore));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IGraphIndexingPipeline));
    }

    [Fact]
    public async Task ReindexingSignalsTheGraphAfterTheIndexAndTrackedRecordAreWritten()
    {
        var signal = Substitute.For<IGraphIndexingSignal>();
        var order = new List<string>();
        var (processor, search, metadata) = Processor(signal, order);
        signal.PublishAsync(Arg.Any<GraphIndexingRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("signal");
            return Task.CompletedTask;
        });

        await processor.ReindexAsync("drive", "item", default);

        await signal.Received(1).PublishAsync(Arg.Is<GraphIndexingRequest>(request =>
            request.Kind == GraphIndexingRequestKind.Indexed && request.DriveId == "drive" && request.ItemId == "item"), Arg.Any<CancellationToken>());
        Assert.Equal(["replace", "save", "signal"], order);
    }

    [Fact]
    public async Task AFailingSignalNeverFailsIndexing()
    {
        var signal = Substitute.For<IGraphIndexingSignal>();
        signal.PublishAsync(Arg.Any<GraphIndexingRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("Service Bus is down"));
        var (processor, search, metadata) = Processor(signal, []);

        var record = await processor.ReindexAsync("drive", "item", default);

        Assert.NotNull(record);
        await metadata.Received(1).SaveAsync(Arg.Any<FileIndexRecord>(), Arg.Any<CancellationToken>());
    }

    private static (SharePointChangeProcessor Processor, ISearchIndexStore Search, IFileMetadataRepository Metadata) Processor(IGraphIndexingSignal signal, List<string> order)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        memory.Set("SharePointDrive___", new Drive { Id = "drive" });
        var graph = new GraphServiceClient(new HttpClient(new GraphHandler()));
        var tracked = new FileIndexRecord("drive", "item", "document.docx", null, null, null, 9, null, null, null, "", "", 1, Guid.NewGuid(), DateTimeOffset.UtcNow, 0);
        var metadata = Substitute.For<IFileMetadataRepository>();
        metadata.GetAsync("drive", "item", Arg.Any<CancellationToken>()).Returns(tracked);
        metadata.SaveAsync(Arg.Any<FileIndexRecord>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("save");
            return Task.CompletedTask;
        });
        var search = Substitute.For<ISearchIndexStore>();
        search.ReplaceItemAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchChunkDocument>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("replace");
            return Task.CompletedTask;
        });
        var extractor = Substitute.For<IContentExtractor>();
        extractor.ExtractAsync(Arg.Any<DriveItemChange>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns("index this text");
        var protection = Substitute.For<IProtectedFileService>();
        protection.EnsureReadableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FileSensitivity(null, null, false, false, DateTimeOffset.UtcNow));
        var embeddings = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embeddings.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1 })]));
        var client = new SharePointClient(graph, memory, Options.Create(new SharePointOptions()), protection);
        var processor = new SharePointChangeProcessor(client, Substitute.For<IDeltaStateRepository>(), metadata,
            search, extractor, embeddings, Options.Create(new ProcessorOptions { MaxFileBytes = 1024, AllowedFileExtensions = [".docx"] }),
            Options.Create(new OpenAiOptions()), Options.Create(new SharePointAgent.Application.SearchOptions()),
            NullLogger<SharePointChangeProcessor>.Instance, graphIndexing: signal);
        return (processor, search, metadata);
    }

    private sealed class GraphHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/content") ? "plain text"
                : path.EndsWith("/permissions") ? "{\"value\":[]}"
                : "{\"id\":\"item\",\"name\":\"document.docx\",\"file\":{\"mimeType\":\"application/vnd.openxmlformats-officedocument.wordprocessingml.document\"},\"size\":9}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, path.EndsWith("/content") ? "application/octet-stream" : "application/json")
            });
        }
    }
}
