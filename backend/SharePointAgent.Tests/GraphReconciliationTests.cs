using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class GraphReconciliationTests
{
    private readonly InMemoryGraphProjectionStore _store = new();
    private readonly InMemoryFileMetadata _metadata = new();
    private readonly IIndexStateRepository _indexState = Substitute.For<IIndexStateRepository>();
    private readonly IGraphIndexingPipeline _pipeline = Substitute.For<IGraphIndexingPipeline>();

    public GraphReconciliationTests()
    {
        _pipeline.CurrentExtractionVersion.Returns("x1");
        _indexState.ListFilesAsync(Arg.Any<IndexedFileQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.Arg<IndexedFileQuery>();
            var rows = _metadata.All().OrderBy(record => record.ItemId).Select(Row).ToList();
            return new PagedResult<IndexedFileRow>(rows.Count, rows.Skip(query.Skip).Take(query.Top).ToList());
        });
    }

    private static IndexedFileRow Row(FileIndexRecord record) => new(
        record.DriveId, record.ItemId, record.Name, record.ParentPath, record.WebUrl, record.MimeType, record.Size, record.LastModifiedUtc,
        record.ETag, record.CTag, record.PermissionsHash, record.IndexFingerprint, record.ChunkCount, record.ScanId, record.IndexedAtUtc, null);

    private GraphReconciliationService Service(Action<GraphRagOptions>? configure = null) => new(
        Options(configure), SharePoint(), _indexState, _metadata,
        new BoundedGraphTraversal(_store, Ontology, NullLogger<BoundedGraphTraversal>.Instance), _store,
        new GraphProjectionWriter(_store, Ontology, Options(), TimeProvider.System, NullLogger<GraphProjectionWriter>.Instance),
        _pipeline, TimeProvider.System, NullLogger<GraphReconciliationService>.Instance);

    [Fact]
    public async Task MissingStaleAndUpgradedDocumentsAreReprocessedAndCurrentOnesAreLeftAlone()
    {
        var missing = File("a-missing");
        var stale = File("b-stale", "\"c:{1},2\"");
        var upgraded = File("c-upgraded");
        var current = File("d-current");
        foreach (var record in new[] { missing, stale, upgraded, current })
        {
            _metadata.Put(record);
        }
        _store.SeedState(ReadyState(File("b-stale", "\"c:{1},1\"")));
        _store.SeedState(ReadyState(upgraded) with { ActiveExtractionVersion = "x0" });
        _store.SeedState(ReadyState(current));

        var report = await Service(options => options.Reconciliation.PageSize = 10).ReconcileAsync(default);

        Assert.Equal(4, report.Checked);
        Assert.Equal(3, report.Repaired);
        await _pipeline.Received(1).ProcessAsync(Arg.Is<GraphIndexingRequest>(request => request.ItemId == "a-missing"), Arg.Any<CancellationToken>());
        await _pipeline.Received(1).ProcessAsync(Arg.Is<GraphIndexingRequest>(request => request.ItemId == "b-stale"), Arg.Any<CancellationToken>());
        await _pipeline.Received(1).ProcessAsync(Arg.Is<GraphIndexingRequest>(request => request.ItemId == "c-upgraded"), Arg.Any<CancellationToken>());
        await _pipeline.DidNotReceive().ProcessAsync(Arg.Is<GraphIndexingRequest>(request => request.ItemId == "d-current"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GraphDocumentsTheIndexerNoLongerTracksAreRemoved()
    {
        _store.SeedState(ReadyState(File("gone")));

        var report = await Service().ReconcileAsync(default);

        Assert.Equal(1, report.Removed);
        await _pipeline.Received(1).ProcessAsync(Arg.Is<GraphIndexingRequest>(request => request.ItemId == "gone" && request.Kind == GraphIndexingRequestKind.Removed), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnfinishedTombstonesAreCleanedUp()
    {
        var record = File("deleted");
        var assertion = Assertion(Entity("System", "A"), "DEPENDS_ON", Entity("System", "B"), Evidence(record, 0, "A depends on B"));
        _store.Seed(assertion);
        _store.SeedState(ReadyState(record) with
        {
            Status = GraphProjectionStatus.Tombstoned,
            ActiveRevision = null,
            PendingManifest = [new GraphManifestEntry(assertion.AssertionId, assertion.SubjectEntityId, assertion.Predicate, assertion.ObjectEntityId, assertion.Evidence.ChunkId)]
        });

        var report = await Service().ReconcileAsync(default);

        Assert.Equal(1, report.Removed);
        Assert.Equal(GraphAssertionStatus.Retracted, Assert.Single(_store.Assertions).Status);
    }

    [Fact]
    public async Task RepairsAreBoundedPerCycleAndFailuresDoNotStopTheCycle()
    {
        for (var index = 0; index < 5; index++)
        {
            _metadata.Put(File($"item-{index}"));
        }
        _pipeline.ProcessAsync(Arg.Is<GraphIndexingRequest>(request => request.ItemId == "item-0"), Arg.Any<CancellationToken>())
            .Returns<GraphIndexingOutcome>(_ => throw new GraphTransientFailureException("store.throttled", "busy"));

        var report = await Service(options => options.Reconciliation.MaxRepairsPerCycle = 3).ReconcileAsync(default);

        Assert.Equal(1, report.Failed);
        Assert.Equal(2, report.Repaired);
    }

    [Fact]
    public async Task NothingRunsWhenIndexingIsOff()
    {
        _metadata.Put(File("item-1"));

        var report = await Service(options => options.IndexingEnabled = false).ReconcileAsync(default);

        Assert.Equal(0, report.Checked);
        await _pipeline.DidNotReceive().ProcessAsync(Arg.Any<GraphIndexingRequest>(), Arg.Any<CancellationToken>());
    }
}
