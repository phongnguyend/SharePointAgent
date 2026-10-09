using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag.Cosmos;

/// <summary>
/// Wraps the Cosmos client used for the graph, so it cannot be confused with any other Cosmos client the
/// application registers.
/// </summary>
public sealed class GraphCosmosClient(CosmosClient client) : IDisposable
{
    public CosmosClient Client { get; } = client;

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// The Cosmos DB for NoSQL implementation of the graph projection. Every read names its partition: entities
/// and document states are point reads, adjacency is a query inside the partitions the requested entity IDs
/// hash to, and incoming edges come from the reverse projection rather than a cross-partition scan. The only
/// cross-partition query is the reconciliation listing of document states.
/// </summary>
public sealed class CosmosGraphProjectionStore : IGraphProjectionStore
{
    private const int WriteParallelism = 16;

    private readonly Container _entities;
    private readonly Container _assertions;
    private readonly Container _reverseAssertions;
    private readonly Container _documentStates;
    private readonly GraphPartitionLocator _locator;
    private readonly TimeProvider _time;
    private readonly ILogger<CosmosGraphProjectionStore> _logger;

    public CosmosGraphProjectionStore(
        GraphCosmosClient client,
        GraphPartitionLocator locator,
        IOptions<GraphRagOptions> options,
        TimeProvider time,
        ILogger<CosmosGraphProjectionStore> logger)
    {
        var cosmos = options.Value.Cosmos;
        var database = client.Client.GetDatabase(cosmos.DatabaseName);
        _entities = database.GetContainer(cosmos.EntitiesContainer);
        _assertions = database.GetContainer(cosmos.AssertionsContainer);
        _reverseAssertions = database.GetContainer(cosmos.ReverseAssertionsContainer);
        _documentStates = database.GetContainer(cosmos.DocumentStateContainer);
        _locator = locator;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GraphDocumentState>> GetDocumentStatesAsync(
        string tenantId, IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken)
    {
        var keys = documentIds.Distinct(StringComparer.Ordinal)
            .Select(documentId => (StateId(documentId), new PartitionKey(_locator.For(tenantId, documentId))))
            .ToList();
        var documents = await ReadManyAsync<DocumentStateDocument>(_documentStates, keys, "readDocumentStates", cancellationToken);
        return documents.Where(document => document.TenantId == tenantId).Select(document => document.ToState()).ToList();
    }

    public async Task<string> SaveDocumentStateAsync(GraphDocumentState state, TimeSpan? timeToLive, CancellationToken cancellationToken)
    {
        var partitionKey = _locator.For(state.TenantId, state.DocumentId);
        var document = DocumentStateDocument.From(state, StateId(state.DocumentId), partitionKey, timeToLive);
        return await WriteConditionallyAsync(_documentStates, document, document.Id, partitionKey, state.ConcurrencyToken, "saveDocumentState", cancellationToken);
    }

    public async IAsyncEnumerable<GraphDocumentState> ListDocumentStatesAsync(string tenantId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId").WithParameter("@tenantId", tenantId);
        using var iterator = _documentStates.GetItemQueryIterator<DocumentStateDocument>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 100 });
        while (iterator.HasMoreResults)
        {
            var page = await ExecuteAsync("listDocumentStates", () => iterator.ReadNextAsync(cancellationToken));
            GraphRagMetrics.Charge("listDocumentStates", page.RequestCharge);
            foreach (var document in page)
            {
                yield return document.ToState();
            }
        }
    }

    public async Task<IReadOnlyList<GraphEntityRecord>> GetEntitiesAsync(string tenantId, IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken)
    {
        var keys = entityIds.Distinct(StringComparer.Ordinal)
            .Select(entityId => (entityId, new PartitionKey(_locator.For(tenantId, entityId))))
            .ToList();
        var documents = await ReadManyAsync<EntityDocument>(_entities, keys, "readEntities", cancellationToken);
        return documents.Where(document => document.TenantId == tenantId).Select(document => document.ToRecord()).ToList();
    }

    public async Task<string> SaveEntityAsync(GraphEntityRecord entity, CancellationToken cancellationToken)
    {
        var partitionKey = _locator.For(entity.Entity.TenantId, entity.Entity.EntityId);
        var document = EntityDocument.From(entity, partitionKey, _time.GetUtcNow());
        return await WriteConditionallyAsync(_entities, document, document.Id, partitionKey, entity.ConcurrencyToken, "saveEntity", cancellationToken);
    }

    public async Task UpsertAssertionsAsync(IReadOnlyList<GraphAssertion> assertions, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var writes = assertions.SelectMany(assertion => new[]
        {
            (Container: _assertions, Document: AssertionDocument.From(assertion, _locator.For(assertion.TenantId, assertion.SubjectEntityId), now)),
            (Container: _reverseAssertions, Document: AssertionDocument.From(assertion, _locator.For(assertion.TenantId, assertion.ObjectEntityId), now))
        });
        await Parallel.ForEachAsync(writes, new ParallelOptions { MaxDegreeOfParallelism = WriteParallelism, CancellationToken = cancellationToken }, async (write, token) =>
        {
            var response = await ExecuteAsync("upsertAssertion", () => write.Container.UpsertItemAsync(
                write.Document, new PartitionKey(write.Document.PartitionKey), new ItemRequestOptions { EnableContentResponseOnWrite = false }, token));
            GraphRagMetrics.Charge("upsertAssertion", response.RequestCharge);
        });
    }

    public async Task SetAssertionStatusAsync(
        IReadOnlyList<GraphAssertionKey> keys, GraphAssertionStatus status, TimeSpan? timeToLive, CancellationToken cancellationToken)
    {
        var operations = new List<PatchOperation>
        {
            PatchOperation.Set("/status", AssertionDocument.StatusName(status)),
            PatchOperation.Set("/updatedAtUtc", _time.GetUtcNow())
        };
        if (timeToLive is { } ttl)
        {
            operations.Add(PatchOperation.Set("/ttl", (int)Math.Ceiling(ttl.TotalSeconds)));
        }

        var patches = keys.SelectMany(key => new[]
        {
            (Container: _assertions, Key: key, PartitionKey: _locator.For(key.TenantId, key.SubjectEntityId)),
            (Container: _reverseAssertions, Key: key, PartitionKey: _locator.For(key.TenantId, key.ObjectEntityId))
        });
        await Parallel.ForEachAsync(patches, new ParallelOptions { MaxDegreeOfParallelism = WriteParallelism, CancellationToken = cancellationToken }, async (patch, token) =>
        {
            try
            {
                var response = await ExecuteAsync("patchAssertion", () => patch.Container.PatchItemAsync<AssertionDocument>(
                    patch.Key.AssertionId, new PartitionKey(patch.PartitionKey), operations,
                    new PatchItemRequestOptions { EnableContentResponseOnWrite = false }, token));
                GraphRagMetrics.Charge("patchAssertion", response.RequestCharge);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                // Never written, or already expired: nothing left to retract.
            }
        });
    }

    public async Task<GraphAdjacencyPage> GetAdjacentAssertionsAsync(
        string tenantId,
        IReadOnlyCollection<string> entityIds,
        IReadOnlySet<string> predicates,
        GraphTraversalDirection direction,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var reads = new List<Task<(List<GraphAssertion> Assertions, double Charge, bool Truncated)>>();
        if (direction is GraphTraversalDirection.Outgoing or GraphTraversalDirection.Both)
        {
            reads.AddRange(PartitionedReads(_assertions, "subjectEntityId", tenantId, entityIds, predicates, maxResults, cancellationToken));
        }

        if (direction is GraphTraversalDirection.Incoming or GraphTraversalDirection.Both)
        {
            reads.AddRange(PartitionedReads(_reverseAssertions, "objectEntityId", tenantId, entityIds, predicates, maxResults, cancellationToken));
        }

        var pages = await Task.WhenAll(reads);
        var assertions = pages.SelectMany(page => page.Assertions)
            .DistinctBy(assertion => assertion.AssertionId, StringComparer.Ordinal)
            .OrderBy(assertion => assertion.AssertionId, StringComparer.Ordinal)
            .ToList();
        var truncated = pages.Any(page => page.Truncated) || assertions.Count > maxResults;
        return new GraphAdjacencyPage(assertions.Take(maxResults).ToList(), pages.Sum(page => page.Charge), truncated);
    }

    private IEnumerable<Task<(List<GraphAssertion> Assertions, double Charge, bool Truncated)>> PartitionedReads(
        Container container, string endpointField, string tenantId, IReadOnlyCollection<string> entityIds,
        IReadOnlySet<string> predicates, int maxResults, CancellationToken cancellationToken)
    {
        // One query per partition the entities hash to, each targeted at that partition only.
        foreach (var group in entityIds.Distinct(StringComparer.Ordinal).GroupBy(entityId => _locator.For(tenantId, entityId), StringComparer.Ordinal))
        {
            yield return ReadPartitionAsync(container, endpointField, tenantId, group.Key, group.ToList(), predicates, maxResults, cancellationToken);
        }
    }

    private async Task<(List<GraphAssertion> Assertions, double Charge, bool Truncated)> ReadPartitionAsync(
        Container container, string endpointField, string tenantId, string partitionKey, List<string> entityIds,
        IReadOnlySet<string> predicates, int maxResults, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
                $"SELECT * FROM c WHERE c.tenantId = @tenantId AND ARRAY_CONTAINS(@entityIds, c.{endpointField}) " +
                "AND ARRAY_CONTAINS(@predicates, c.predicate) AND c.status != 'retracted'")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@entityIds", entityIds)
            .WithParameter("@predicates", predicates.ToList());
        var assertions = new List<GraphAssertion>();
        var charge = 0.0;
        using var iterator = container.GetItemQueryIterator<AssertionDocument>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(partitionKey),
            MaxItemCount = Math.Clamp(maxResults, 1, 1000)
        });
        while (iterator.HasMoreResults && assertions.Count <= maxResults)
        {
            var page = await ExecuteAsync("readAdjacency", () => iterator.ReadNextAsync(cancellationToken));
            charge += page.RequestCharge;
            assertions.AddRange(page.Select(document => document.ToAssertion()));
        }

        GraphRagMetrics.Charge("readAdjacency", charge);
        return (assertions, charge, iterator.HasMoreResults || assertions.Count > maxResults);
    }

    private async Task<IReadOnlyList<T>> ReadManyAsync<T>(
        Container container, List<(string Id, PartitionKey PartitionKey)> keys, string operation, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var results = new List<T>();
        foreach (var batch in keys.Chunk(100))
        {
            var response = await ExecuteAsync(operation, () => container.ReadManyItemsAsync<T>(batch, cancellationToken: cancellationToken));
            GraphRagMetrics.Charge(operation, response.RequestCharge);
            results.AddRange(response);
        }
        return results;
    }

    private async Task<string> WriteConditionallyAsync<T>(
        Container container, T document, string id, string partitionKey, string? concurrencyToken, string operation, CancellationToken cancellationToken)
    {
        var options = new ItemRequestOptions { EnableContentResponseOnWrite = false };
        try
        {
            ItemResponse<T> response;
            if (concurrencyToken is null)
            {
                response = await ExecuteAsync(operation, () => container.CreateItemAsync(document, new PartitionKey(partitionKey), options, cancellationToken));
            }
            else
            {
                options.IfMatchEtag = concurrencyToken;
                response = await ExecuteAsync(operation, () => container.ReplaceItemAsync(document, id, new PartitionKey(partitionKey), options, cancellationToken));
            }

            GraphRagMetrics.Charge(operation, response.RequestCharge);
            return response.ETag;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new GraphConcurrencyException("The record changed after it was read.");
        }
    }

    private async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // The SDK has already retried with the server's back-off; what is left is sustained throttling.
            GraphRagMetrics.Throttle(operation);
            _logger.LogWarning("Graph store operation {Operation} was throttled after the client's retries.", operation);
            throw new GraphTransientFailureException("store.throttled", "The graph store is throttling requests.", exception);
        }
        catch (CosmosException exception) when ((int)exception.StatusCode >= 500 || exception.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new GraphTransientFailureException("store.unavailable", "The graph store is unavailable.", exception);
        }
    }

    /// <summary>Document IDs can be long and contain characters keys should not, so the state is keyed by a hash.</summary>
    internal static string StateId(string documentId) =>
        "doc-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(documentId)), 0, 16);
}
