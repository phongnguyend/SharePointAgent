using Microsoft.Extensions.Logging.Abstractions;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

public sealed class GraphIndexingPipelineTests
{
    private const string Text = "The CRM depends on ERP for invoicing.";

    private readonly InMemoryGraphProjectionStore _store = new();
    private readonly InMemoryAliasIndex _aliases = new();
    private readonly InMemoryArchive _archive = new();
    private readonly InMemoryFileMetadata _metadata = new();
    private readonly FakeChunkSource _chunks = new();
    private ScriptedExtractor _extractor = new(input => new GraphExtractionResponse
    {
        Entities = [Extracted("e1", "System", "CRM"), Extracted("e2", "System", "ERP")],
        Relations = [Relation("e1", "depends on", "e2", "CRM depends on ERP")]
    });

    private GraphIndexingPipeline Pipeline(Action<GraphRagOptions>? configure = null)
    {
        var options = Options(configure);
        var writer = new GraphProjectionWriter(_store, Ontology, options, TimeProvider.System, NullLogger<GraphProjectionWriter>.Instance);
        var reader = new BoundedGraphTraversal(_store, Ontology, NullLogger<BoundedGraphTraversal>.Instance);
        return new GraphIndexingPipeline(
            options, SharePoint(), _metadata, _chunks, _extractor, new EntityResolver(_aliases, _store, Ontology),
            _aliases, _archive, writer, reader, Ontology, NullLogger<GraphIndexingPipeline>.Instance);
    }

    private static GraphIndexingRequest Request(GraphIndexingRequestKind kind = GraphIndexingRequestKind.Indexed, string itemId = "item-1") =>
        new(kind, Drive, itemId, DateTimeOffset.UnixEpoch);

    private FileIndexRecord Index(string itemId = "item-1", params string[] contents)
    {
        contents = contents.Length == 0 ? [Text] : contents;
        var record = File(itemId, chunkCount: contents.Length);
        _metadata.Put(record);
        _chunks.Put(Drive, itemId, contents);
        return record;
    }

    private async Task<GraphDocumentState?> StateAsync(string itemId = "item-1") =>
        (await _store.GetDocumentStatesAsync(Tenant, [GraphDocumentIds.ForDriveItem(Drive, itemId)], default)).SingleOrDefault();

    [Fact]
    public async Task IndexedDocumentsAreExtractedArchivedThenProjected()
    {
        var record = Index();

        var outcome = await Pipeline().ProcessAsync(Request(), default);

        Assert.Equal(GraphIndexingOutcome.Extracted, outcome);
        Assert.Equal(1, _archive.Count);
        var assertion = Assert.Single(_store.Assertions);
        Assert.Equal("DEPENDS_ON", assertion.Predicate);
        Assert.Equal(EntityId("System", "CRM"), assertion.SubjectEntityId);
        Assert.Equal(GraphDocumentIds.ForChunkContent(Text), assertion.Evidence.ChunkContentHash);
        Assert.Equal(GraphDocumentIds.ForAccessScope(record.PermissionsHash), assertion.Evidence.AccessScopeRef);
        Assert.True((await StateAsync())!.IsServing(Revision(record)));
        Assert.Contains(_aliases.Entries, entry => entry.NormalizedAlias == "crm");
    }

    [Fact]
    public async Task RepeatedRequestsForTheSameRevisionDoNotCallTheModelAgain()
    {
        Index();
        await Pipeline().ProcessAsync(Request(), default);

        var outcome = await Pipeline().ProcessAsync(Request(), default);

        Assert.Equal(GraphIndexingOutcome.AlreadyCurrent, outcome);
        Assert.Equal(1, _extractor.Calls);
    }

    [Fact]
    public async Task AnEmptyGraphStoreIsRebuiltFromTheArchiveWithoutTheModel()
    {
        Index();
        await Pipeline().ProcessAsync(Request(), default);
        var original = Assert.Single(_store.Assertions);
        var rebuilt = new InMemoryGraphProjectionStore();
        var options = Options();
        var pipeline = new GraphIndexingPipeline(
            options, SharePoint(), _metadata, _chunks, _extractor, new EntityResolver(_aliases, rebuilt, Ontology), _aliases, _archive,
            new GraphProjectionWriter(rebuilt, Ontology, options, TimeProvider.System, NullLogger<GraphProjectionWriter>.Instance),
            new BoundedGraphTraversal(rebuilt, Ontology, NullLogger<BoundedGraphTraversal>.Instance), Ontology, NullLogger<GraphIndexingPipeline>.Instance);

        var outcome = await pipeline.ProcessAsync(Request(), default);

        Assert.Equal(GraphIndexingOutcome.ReplayedFromArchive, outcome);
        Assert.Equal(1, _extractor.Calls);
        Assert.Equal(original, Assert.Single(rebuilt.Assertions));
    }

    [Fact]
    public async Task RelationsTheTextDoesNotStateAreRejected()
    {
        Index();
        _extractor = new ScriptedExtractor(_ => new GraphExtractionResponse
        {
            Entities = [Extracted("e1", "System", "CRM"), Extracted("e2", "System", "ERP"), Extracted("e3", "System", "Billing")],
            Relations =
            [
                Relation("e1", "DEPENDS_ON", "e2", "CRM is mentioned with ERP"),
                Relation("e2", "DEPENDS_ON", "e3", "ERP depends on Billing")
            ]
        });

        await Pipeline().ProcessAsync(Request(), default);

        Assert.Empty(_store.Assertions);
    }

    [Fact]
    public async Task ProtectedDocumentsAreNotSentToTheModel()
    {
        var record = File("item-1", encrypted: true);
        _metadata.Put(record);
        _chunks.Put(Drive, "item-1", Text);

        var outcome = await Pipeline().ProcessAsync(Request(), default);

        Assert.Equal(GraphIndexingOutcome.SkippedProtectedContent, outcome);
        Assert.Equal(0, _extractor.Calls);
        Assert.True((await StateAsync())!.IsServing(Revision(record)));
        Assert.Empty(_store.Assertions);
    }

    [Fact]
    public async Task AnIndexMidRewriteIsRetriedLater()
    {
        Index();
        _chunks.Put(Drive, "item-1");

        var exception = await Assert.ThrowsAsync<GraphTransientFailureException>(() => Pipeline().ProcessAsync(Request(), default));

        Assert.Equal("index.inconsistent", exception.Code);
        Assert.Equal(0, _extractor.Calls);
    }

    [Fact]
    public async Task ARevisionThatChangesWhileChunksAreReadIsRetriedLater()
    {
        Index();
        _metadata.OnSecondRead = record => record with { CTag = "\"c:{1},2\"" };

        var exception = await Assert.ThrowsAsync<GraphTransientFailureException>(() => Pipeline().ProcessAsync(Request(), default));

        Assert.Equal("revision.changed", exception.Code);
    }

    [Fact]
    public async Task RemovalAndMissingRecordsRetractTheDocument()
    {
        Index();
        await Pipeline().ProcessAsync(Request(), default);

        var removed = await Pipeline().ProcessAsync(Request(GraphIndexingRequestKind.Removed), default);

        Assert.Equal(GraphIndexingOutcome.Removed, removed);
        Assert.Equal(GraphProjectionStatus.Tombstoned, (await StateAsync())!.Status);
        Assert.All(_store.Assertions, assertion => Assert.Equal(GraphAssertionStatus.Retracted, assertion.Status));
        Assert.Equal(GraphIndexingOutcome.Removed, await Pipeline().ProcessAsync(Request(itemId: "never-indexed"), default));
    }

    [Fact]
    public async Task PermissionChangesUpdateTheRecordedScopeWithoutExtraction()
    {
        var record = Index();
        await Pipeline().ProcessAsync(Request(), default);
        _metadata.Put(record with { PermissionsHash = "bmV3" });

        var outcome = await Pipeline().ProcessAsync(Request(GraphIndexingRequestKind.AccessChanged), default);

        Assert.Equal(GraphIndexingOutcome.AccessUpdated, outcome);
        Assert.Equal("perm:bmV3", (await StateAsync())!.AccessScopeRef);
        Assert.Equal(1, _extractor.Calls);
    }

    [Fact]
    public async Task AnIdentifierLinksMentionsAcrossChunks()
    {
        Index("item-1", "Policy POL-7, the Data Retention Policy, was approved.", "The Data Retention Policy applies to the CRM.");
        _extractor = new ScriptedExtractor(input => input.ChunkText.StartsWith("Policy")
            ? new GraphExtractionResponse { Entities = [Extracted("p", "Policy", "Data Retention Policy", ("policyNumber", "POL-7"))] }
            : new GraphExtractionResponse
            {
                Entities = [Extracted("p", "Policy", "Data Retention Policy"), Extracted("s", "System", "CRM")],
                Relations = [Relation("p", "APPLIES_TO", "s", "The Data Retention Policy applies to the CRM")]
            });

        await Pipeline().ProcessAsync(Request(), default);

        var policyId = GraphIds.ForEntity(Tenant, "Policy", GraphEntityKey.FromIdentifier("policynumber", "pol-7"));
        Assert.Equal(policyId, Assert.Single(_store.Assertions).SubjectEntityId);
    }

    [Fact]
    public async Task TheSameRelationExtractedTwiceIsOneAssertion()
    {
        Index();
        _extractor = new ScriptedExtractor(_ => new GraphExtractionResponse
        {
            Entities = [Extracted("e1", "System", "CRM"), Extracted("e2", "System", "ERP"), Extracted("e3", "System", "crm")],
            Relations = [Relation("e1", "DEPENDS_ON", "e2", "CRM depends on ERP"), Relation("e3", "DEPENDS_ON", "e2", "CRM depends on ERP")]
        });

        await Pipeline().ProcessAsync(Request(), default);

        Assert.Single(_store.Assertions);
    }

    [Fact]
    public async Task ContradictoryAntisymmetricClaimsAreRecordedAsDisputed()
    {
        Index("item-1", "Policy A supersedes Policy B. Policy B supersedes Policy A.");
        _extractor = new ScriptedExtractor(_ => new GraphExtractionResponse
        {
            Entities = [Extracted("a", "Policy", "Policy A"), Extracted("b", "Policy", "Policy B")],
            Relations = [Relation("a", "SUPERSEDES", "b", "Policy A supersedes Policy B"), Relation("b", "SUPERSEDES", "a", "Policy B supersedes Policy A")]
        });

        await Pipeline().ProcessAsync(Request(), default);

        Assert.Equal(2, _store.Assertions.Count);
        Assert.All(_store.Assertions, assertion => Assert.Equal(GraphAssertionStatus.Disputed, assertion.Status));
    }

    [Fact]
    public async Task ADisabledPipelineDoesNothing()
    {
        Index();

        Assert.Equal(GraphIndexingOutcome.Disabled, await Pipeline(options => options.IndexingEnabled = false).ProcessAsync(Request(), default));
        Assert.Equal(GraphIndexingOutcome.Disabled, await Pipeline(options => options.EnabledTenantIds = ["other"]).ProcessAsync(Request(), default));
        Assert.Equal(0, _extractor.Calls);
    }

    [Fact]
    public async Task NeighbouringChunksAreContextNeverEvidence()
    {
        Index("item-1", "Background about CRM and ERP.", "The CRM depends on ERP for invoicing.");
        var inputs = new List<GraphExtractionInput>();
        _extractor = new ScriptedExtractor(input =>
        {
            lock (inputs)
            {
                inputs.Add(input);
            }

            // The model claims a relation quoted from the neighbour, not from the chunk it was given.
            return input.ChunkText.StartsWith("Background")
                ? new GraphExtractionResponse
                {
                    Entities = [Extracted("e1", "System", "CRM"), Extracted("e2", "System", "ERP")],
                    Relations = [Relation("e1", "DEPENDS_ON", "e2", "The CRM depends on ERP for invoicing")]
                }
                : new GraphExtractionResponse();
        });

        await Pipeline().ProcessAsync(Request(), default);

        Assert.Empty(_store.Assertions);
        Assert.Contains(inputs, input => input.NextContext is not null && input.NextContext.Contains("depends"));
    }
}
