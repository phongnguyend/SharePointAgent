using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Breadth-first, application-managed adjacency traversal. Each level is one batch of partition-targeted
/// reads; there is no query language a model could write into, and every cost dimension is capped: depth,
/// entities, assertions, elapsed time, and request charge.
/// <para>
/// Only assertions from a document revision that is currently projected and <see cref="GraphProjectionStatus.Ready"/>
/// are returned. The result is still a set of candidates: the caller must verify every edge's evidence for
/// the reader before using it.
/// </para>
/// </summary>
public sealed class BoundedGraphTraversal(
    IGraphProjectionStore store,
    GraphOntology ontology,
    ILogger<BoundedGraphTraversal> logger) : IGraphReader
{
    /// <summary>A hard ceiling on depth whatever a plan asks for.</summary>
    public const int MaxDepthLimit = 3;

    private const int MaxEntityLimit = 1000;

    private const int MaxAssertionLimit = 5000;

    private const int MaxRedirectHops = 5;

    public async Task<GraphDocumentState?> GetDocumentStateAsync(string tenantId, string documentId, CancellationToken cancellationToken) =>
        (await store.GetDocumentStatesAsync(tenantId, [documentId], cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<GraphDocumentState>> GetDocumentStatesAsync(string tenantId, IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken) =>
        store.GetDocumentStatesAsync(tenantId, documentIds, cancellationToken);

    public async Task<GraphTraversalResult> TraverseAsync(GraphTraversalPlan plan, CancellationToken cancellationToken)
    {
        Validate(plan);
        using var activity = GraphRagMetrics.Activities.StartActivity("GraphRag.Traverse");
        var stopwatch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (plan.Timeout is { } timeout)
        {
            budget.CancelAfter(timeout);
        }

        try
        {
            var result = await TraverseCoreAsync(plan, budget.Token);
            GraphRagMetrics.Traversed(stopwatch.Elapsed.TotalMilliseconds, result.Truncated);
            activity?.SetTag("graph_rag.edges", result.Edges.Count);
            activity?.SetTag("graph_rag.truncated", result.Truncated);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            GraphRagMetrics.Traversed(stopwatch.Elapsed.TotalMilliseconds, truncated: true);
            throw new TimeoutException("The graph traversal exceeded its time budget.");
        }
    }

    private async Task<GraphTraversalResult> TraverseCoreAsync(GraphTraversalPlan plan, CancellationToken cancellationToken)
    {
        var tenantId = plan.TenantId;
        var entities = new EntityCache(store, tenantId);
        var states = new Dictionary<string, GraphDocumentState?>(StringComparer.Ordinal);
        var symmetric = plan.AllowedPredicates.Where(predicate => ontology.Relations[predicate].Symmetric).ToHashSet(StringComparer.Ordinal);
        var directed = plan.AllowedPredicates.Where(predicate => !symmetric.Contains(predicate)).ToHashSet(StringComparer.Ordinal);

        var starts = new List<string>();
        foreach (var start in plan.StartEntityIds.Distinct(StringComparer.Ordinal))
        {
            var canonical = await entities.CanonicalAsync(start, cancellationToken);
            if (!starts.Contains(canonical) && starts.Count < plan.MaxEntities)
            {
                starts.Add(canonical);
            }
        }

        var visited = new HashSet<string>(starts, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<GraphTraversalEdge>();
        var frontier = starts;
        var charge = 0.0;
        var maxCharge = plan.MaxRequestCharge ?? double.MaxValue;
        string? truncation = null;

        for (var depth = 1; depth <= plan.MaxDepth && frontier.Count > 0 && truncation is null; depth++)
        {
            // An entity merged into another is reached through its survivor, so its own assertions are read too.
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            await entities.LoadAsync(frontier, cancellationToken);
            foreach (var entityId in frontier)
            {
                owners[entityId] = entityId;
                foreach (var merged in entities.Get(entityId)?.MergedEntityIds ?? [])
                {
                    owners.TryAdd(merged, entityId);
                }
            }

            var remaining = plan.MaxAssertions - edges.Count;
            var assertions = new List<GraphAssertion>();
            foreach (var (predicates, direction) in new[] { (directed, plan.Direction), (symmetric, GraphTraversalDirection.Both) })
            {
                if (predicates.Count == 0)
                {
                    continue;
                }

                var page = await store.GetAdjacentAssertionsAsync(tenantId, owners.Keys, predicates, direction, remaining + 1, cancellationToken);
                charge += page.RequestCharge;
                assertions.AddRange(page.Assertions);
            }

            if (charge > maxCharge)
            {
                truncation = "requestCharge";
            }

            var candidates = assertions
                .Where(assertion => assertion.TenantId == tenantId && assertion.Evidence?.TenantId == tenantId)
                .Where(assertion => assertion.Status != GraphAssertionStatus.Retracted && plan.AllowedPredicates.Contains(assertion.Predicate))
                .DistinctBy(assertion => assertion.AssertionId, StringComparer.Ordinal)
                .OrderBy(assertion => assertion.AssertionId, StringComparer.Ordinal)
                .ToList();
            await LoadStatesAsync(tenantId, candidates.Select(assertion => assertion.Evidence.DocumentId), states, cancellationToken);

            var next = new List<string>();
            foreach (var assertion in candidates)
            {
                if (states.GetValueOrDefault(assertion.Evidence.DocumentId) is not { } state || !state.IsServing(assertion.Evidence.DocumentVersion))
                {
                    continue;
                }

                var outgoing = symmetric.Contains(assertion.Predicate) || plan.Direction != GraphTraversalDirection.Incoming;
                var incoming = symmetric.Contains(assertion.Predicate) || plan.Direction != GraphTraversalDirection.Outgoing;
                string from;
                string other;
                if (outgoing && owners.TryGetValue(assertion.SubjectEntityId, out var subjectOwner))
                {
                    from = subjectOwner;
                    other = assertion.ObjectEntityId;
                }
                else if (incoming && owners.TryGetValue(assertion.ObjectEntityId, out var objectOwner))
                {
                    from = objectOwner;
                    other = assertion.SubjectEntityId;
                }
                else
                {
                    continue;
                }

                if (!seen.Add(assertion.AssertionId))
                {
                    continue;
                }

                if (edges.Count >= plan.MaxAssertions)
                {
                    truncation = "assertions";
                    break;
                }

                var to = await entities.CanonicalAsync(other, cancellationToken);
                if (to == from)
                {
                    continue;
                }

                if (!visited.Contains(to))
                {
                    if (visited.Count >= plan.MaxEntities)
                    {
                        truncation ??= "entities";
                        continue;
                    }
                    visited.Add(to);
                    next.Add(to);
                }
                edges.Add(new GraphTraversalEdge(assertion, depth, from, to));
            }
            frontier = next;
        }

        if (truncation is not null)
        {
            logger.LogInformation("Graph traversal stopped at its {Limit} limit with {EdgeCount} edges.", truncation, edges.Count);
        }
        return new GraphTraversalResult(starts, edges, visited.Count, truncation is not null, truncation, charge);
    }

    private async Task LoadStatesAsync(
        string tenantId, IEnumerable<string> documentIds, Dictionary<string, GraphDocumentState?> states, CancellationToken cancellationToken)
    {
        var missing = documentIds.Where(id => !states.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var id in missing)
        {
            states[id] = null;
        }

        foreach (var state in await store.GetDocumentStatesAsync(tenantId, missing, cancellationToken))
        {
            if (state.TenantId == tenantId)
            {
                states[state.DocumentId] = state;
            }
        }
    }

    private void Validate(GraphTraversalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!GraphIds.IsStorageSafe(plan.TenantId))
        {
            throw new ArgumentException("The plan's tenant ID is not a valid key.", nameof(plan));
        }

        if (plan.StartEntityIds is null || plan.StartEntityIds.Any(id => !GraphIds.IsStorageSafe(id)))
        {
            throw new ArgumentException("Every start entity must be a valid key.", nameof(plan));
        }

        if (plan.AllowedPredicates is null || plan.AllowedPredicates.Count == 0 || plan.AllowedPredicates.Any(predicate => !ontology.Relations.ContainsKey(predicate)))
        {
            throw new ArgumentException("The plan must allow at least one predicate, and only predicates of the ontology.", nameof(plan));
        }

        if (plan.MaxDepth is < 1 or > MaxDepthLimit || plan.MaxEntities is < 1 or > MaxEntityLimit || plan.MaxAssertions is < 1 or > MaxAssertionLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), $"Depth must be 1-{MaxDepthLimit}, entities 1-{MaxEntityLimit}, and assertions 1-{MaxAssertionLimit}.");
        }
    }

    /// <summary>Loads entity records once per traversal and follows merge redirects to the surviving entity.</summary>
    private sealed class EntityCache(IGraphProjectionStore store, string tenantId)
    {
        private readonly Dictionary<string, GraphEntityRecord?> _records = new(StringComparer.Ordinal);

        public GraphEntityRecord? Get(string entityId) => _records.GetValueOrDefault(entityId);

        public async Task LoadAsync(IEnumerable<string> entityIds, CancellationToken cancellationToken)
        {
            var missing = entityIds.Where(id => !_records.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            foreach (var id in missing)
            {
                _records[id] = null;
            }

            foreach (var record in await store.GetEntitiesAsync(tenantId, missing, cancellationToken))
            {
                if (record.Entity.TenantId == tenantId)
                {
                    _records[record.Entity.EntityId] = record;
                }
            }
        }

        public async Task<string> CanonicalAsync(string entityId, CancellationToken cancellationToken)
        {
            var current = entityId;
            for (var hop = 0; hop < MaxRedirectHops; hop++)
            {
                await LoadAsync([current], cancellationToken);
                if (Get(current)?.RedirectToEntityId is not { } redirect)
                {
                    return current;
                }
                current = redirect;
            }
            return current;
        }
    }
}
