using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;
using SharePointAgent.Infrastructure.GraphRag.Cosmos;
using Xunit;
using static SharePointAgent.Tests.GraphFixtures;

namespace SharePointAgent.Tests;

/// <summary>Runs only when <c>GRAPHRAG_TEST_COSMOS_CONNECTION_STRING</c> names a Cosmos account or the emulator.</summary>
public sealed class CosmosFactAttribute : FactAttribute
{
    public const string Variable = "GRAPHRAG_TEST_COSMOS_CONNECTION_STRING";

    public CosmosFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to run the Cosmos DB integration tests.";
        }
    }
}

/// <summary>Runs only when <c>GRAPHRAG_TEST_BLOB_CONNECTION_STRING</c> names a storage account or Azurite.</summary>
public sealed class BlobFactAttribute : FactAttribute
{
    public const string Variable = "GRAPHRAG_TEST_BLOB_CONNECTION_STRING";

    public BlobFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to run the Blob Storage integration tests.";
        }
    }
}

/// <summary>
/// The Cosmos adapter against a real account, each test in a database of its own that is deleted afterwards.
/// These check what the in-memory store cannot: partition-targeted queries, the reverse projection, ETag
/// conflicts, patching, TTL, and request charges.
/// </summary>
public sealed class CosmosGraphStoreIntegrationTests : IAsyncLifetime
{
    private readonly string _database = $"graphrag-test-{Guid.NewGuid():N}";
    private GraphCosmosClient? _client;
    private CosmosGraphProjectionStore? _store;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable(CosmosFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }

        var options = Options(options =>
        {
            options.Cosmos.UsedManagedIdentity = false;
            options.Cosmos.ConnectionString = connection;
            options.Cosmos.DatabaseName = _database;
            options.Cosmos.CreateIfNotExists = true;
            options.Cosmos.PartitionBuckets = 4;
        });
        _client = new GraphCosmosClient(new CosmosClient(connection, new CosmosClientOptions
        {
            // The emulator serves the gateway only; real accounts accept it too.
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            UseSystemTextJsonSerializerWithOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }
        }));
        await new CosmosGraphInitializer(_client, options, NullLogger<CosmosGraphInitializer>.Instance).StartAsync(default);
        _store = new CosmosGraphProjectionStore(_client, new GraphPartitionLocator(4), options, TimeProvider.System, NullLogger<CosmosGraphProjectionStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.Client.GetDatabase(_database).DeleteAsync();
            _client.Dispose();
        }
    }

    private GraphProjectionWriter Writer() => new(_store!, Ontology, Options(), TimeProvider.System, NullLogger<GraphProjectionWriter>.Instance);

    private static CanonicalGraphSnapshot Chain(FileIndexRecord record, string tenant = Tenant)
    {
        var a = Entity("System", "A", tenant);
        var b = Entity("System", "B", tenant);
        var c = Entity("System", "C", tenant);
        var evidence = Evidence(record, 0, "A depends on B; B depends on C", tenant);
        var snapshot = Snapshot(record, [a, b, c], [Assertion(a, "DEPENDS_ON", b, evidence), Assertion(b, "DEPENDS_ON", c, evidence)]);
        return snapshot with
        {
            TenantId = tenant,
            Resolutions = snapshot.Resolutions,
            Assertions = snapshot.Assertions.Select(assertion => assertion with { TenantId = tenant }).ToList()
        };
    }

    [CosmosFact]
    public async Task SnapshotsProjectIdempotentlyAndServeBothDirections()
    {
        var record = File("item-1");
        var snapshot = Chain(record);
        await Writer().ApplySnapshotAsync(snapshot, default);
        await Writer().ApplySnapshotAsync(snapshot, default);

        var outgoing = await _store!.GetAdjacentAssertionsAsync(Tenant, [EntityId("System", "A")], new HashSet<string> { "DEPENDS_ON" }, GraphTraversalDirection.Outgoing, 10, default);
        var incoming = await _store.GetAdjacentAssertionsAsync(Tenant, [EntityId("System", "C")], new HashSet<string> { "DEPENDS_ON" }, GraphTraversalDirection.Incoming, 10, default);
        var traversal = await new BoundedGraphTraversal(_store, Ontology, NullLogger<BoundedGraphTraversal>.Instance)
            .TraverseAsync(new GraphTraversalPlan(Tenant, [EntityId("System", "A")], new HashSet<string> { "DEPENDS_ON" }, 2, 10, 10), default);

        Assert.Single(outgoing.Assertions);
        Assert.Single(incoming.Assertions);
        Assert.True(outgoing.RequestCharge > 0);
        Assert.Equal(2, traversal.Edges.Count);
    }

    [CosmosFact]
    public async Task StaleConcurrencyTokensAreRejected()
    {
        var record = File("item-1");
        await Writer().ApplySnapshotAsync(Chain(record), default);
        var state = (await _store!.GetDocumentStatesAsync(Tenant, [GraphDocumentIds.ForDriveItem(Drive, "item-1")], default)).Single();
        await _store.SaveDocumentStateAsync(state with { AccessScopeRef = "perm:first" }, null, default);

        await Assert.ThrowsAsync<GraphConcurrencyException>(() => _store.SaveDocumentStateAsync(state with { AccessScopeRef = "perm:second" }, null, default));
        await Assert.ThrowsAsync<GraphConcurrencyException>(() => _store.SaveDocumentStateAsync(state with { ConcurrencyToken = null }, null, default));
    }

    [CosmosFact]
    public async Task DeletedDocumentsAreTombstonedAndTheirAssertionsLeaveTraversal()
    {
        var record = File("item-1");
        var snapshot = Chain(record);
        await Writer().ApplySnapshotAsync(snapshot, default);

        await Writer().RetractDocumentAsync(Tenant, snapshot.DocumentId, default);

        var state = (await _store!.GetDocumentStatesAsync(Tenant, [snapshot.DocumentId], default)).Single();
        var adjacency = await _store.GetAdjacentAssertionsAsync(Tenant, [EntityId("System", "A")], new HashSet<string> { "DEPENDS_ON" }, GraphTraversalDirection.Both, 10, default);
        Assert.Equal(GraphProjectionStatus.Tombstoned, state.Status);
        Assert.Empty(adjacency.Assertions);
        Assert.Equal(3, (await _store.GetEntitiesAsync(Tenant, snapshot.Entities.Select(entity => entity.EntityId).ToList(), default)).Count);
    }

    [CosmosFact]
    public async Task TenantsNeverSeeEachOthersGraphs()
    {
        await Writer().ApplySnapshotAsync(Chain(File("item-1")), default);
        await Writer().ApplySnapshotAsync(Chain(File("item-1"), "tenant-b"), default);

        var tenantA = await _store!.GetAdjacentAssertionsAsync(Tenant, [EntityId("System", "A")], new HashSet<string> { "DEPENDS_ON" }, GraphTraversalDirection.Both, 10, default);
        var crossed = await _store.GetAdjacentAssertionsAsync("tenant-b", [EntityId("System", "A")], new HashSet<string> { "DEPENDS_ON" }, GraphTraversalDirection.Both, 10, default);
        var states = new List<GraphDocumentState>();
        await foreach (var state in _store.ListDocumentStatesAsync(Tenant, default))
        {
            states.Add(state);
        }

        Assert.All(tenantA.Assertions, assertion => Assert.Equal(Tenant, assertion.TenantId));
        Assert.Empty(crossed.Assertions);
        Assert.Single(states);
    }

    [CosmosFact]
    public async Task TheGraphIsRebuiltFromArchivedSnapshots()
    {
        var snapshot = Chain(File("item-1"));
        var archive = new InMemoryArchive();
        await Writer().ApplySnapshotAsync(await archive.SaveAsync(snapshot, default), default);
        foreach (var container in new[] { "graphEntities", "graphAssertions", "graphAssertionsByObject", "graphDocumentState" })
        {
            await _client!.Client.GetContainer(_database, container).DeleteContainerAsync();
        }
        await InitializeAsync();

        var replayed = await archive.TryLoadAsync(Tenant, snapshot.DocumentId, snapshot.DocumentVersion, snapshot.Extraction.ExtractionVersion, default);
        await Writer().ApplySnapshotAsync(replayed!, default);

        var adjacency = await _store!.GetAdjacentAssertionsAsync(Tenant, [EntityId("System", "B")], new HashSet<string> { "DEPENDS_ON" }, GraphTraversalDirection.Both, 10, default);
        Assert.Equal(2, adjacency.Assertions.Count);
    }
}

public sealed class BlobArchiveIntegrationTests
{
    [BlobFact]
    public async Task SnapshotsAreArchivedOnceAndReadBackExactly()
    {
        var container = new BlobContainerClient(Environment.GetEnvironmentVariable(BlobFactAttribute.Variable), $"graphrag-test-{Guid.NewGuid():N}");
        try
        {
            var archive = new BlobCanonicalGraphArchive(container);
            var record = File("item-1");
            var crm = Entity("System", "CRM");
            var erp = Entity("System", "ERP");
            var first = Snapshot(record, [crm, erp], [Assertion(crm, "DEPENDS_ON", erp, Evidence(record, 0, "CRM depends on ERP"))]);
            var second = first with { Assertions = [] };

            Assert.Same(first, await archive.SaveAsync(first, default));
            var kept = await archive.SaveAsync(second, default);
            var loaded = await archive.TryLoadAsync(Tenant, first.DocumentId, first.DocumentVersion, "x1", default);

            Assert.Single(kept.Assertions);
            Assert.Equal(first.Assertions, loaded!.Assertions);
            Assert.Null(await archive.TryLoadAsync(Tenant, first.DocumentId, "rev:other", "x1", default));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }
}
