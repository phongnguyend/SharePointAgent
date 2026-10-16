using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.KnowledgeGraph;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SynchronousGraphIndexingTests
{
    [Theory]
    [InlineData(GraphIndexingRequestKind.Indexed)]
    [InlineData(GraphIndexingRequestKind.AccessChanged)]
    [InlineData(GraphIndexingRequestKind.Removed)]
    public async Task InlineSignalWaitsForPipelineAndPassesCancellation(GraphIndexingRequestKind kind)
    {
        var pipeline = Substitute.For<IGraphIndexingPipeline>();
        var completion = new TaskCompletionSource<GraphIndexingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new GraphIndexingRequest(kind, "drive", "item", DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        pipeline.ProcessAsync(request, cancellation.Token).Returns(completion.Task);
        var pending = new SynchronousGraphIndexingSignal(pipeline).PublishAsync(request, cancellation.Token);
        Assert.False(pending.IsCompleted);
        completion.SetResult(default);
        await pending;
        await pipeline.Received(1).ProcessAsync(request, cancellation.Token);
    }

    [Fact]
    public async Task PipelineFailureReachesTheExistingIndexerFailureHandler()
    {
        var pipeline = Substitute.For<IGraphIndexingPipeline>();
        pipeline.ProcessAsync(Arg.Any<GraphIndexingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GraphIndexingOutcome>(new InvalidOperationException("failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SynchronousGraphIndexingSignal(pipeline)
            .PublishAsync(new GraphIndexingRequest(GraphIndexingRequestKind.Indexed, "drive", "item", DateTimeOffset.UtcNow), default));
    }

    [Theory]
    [InlineData(true, true, typeof(SynchronousGraphIndexingSignal))]
    [InlineData(true, false, typeof(ServiceBusGraphIndexingSignal))]
    [InlineData(false, true, typeof(NullGraphIndexingSignal))]
    public void SelectsTransportFromGraphFlags(bool enabled, bool synchronous, Type expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnowledgeGraph:IndexingEnabled"] = enabled.ToString(),
            ["KnowledgeGraph:SynchronousIndexing"] = synchronous.ToString()
        }).Build();
        var services = new ServiceCollection();
        services.AddGraphIndexingSignal(configuration);
        Assert.Equal(expected, services.Single(item => item.ServiceType == typeof(IGraphIndexingSignal)).ImplementationType);
        Assert.Equal(enabled && synchronous, services.Any(item => item.ServiceType == typeof(IGraphIndexingPipeline)));
    }
}
