using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Adds graph-derived chunks to a baseline search the reader has already been authorized for. The steps are:
/// <list type="number">
/// <item>Route the question. Only relationship questions continue.</item>
/// <item>Seed the traversal from entities the question names unambiguously, and from entities asserted in
/// the authorized baseline chunks.</item>
/// <item>Traverse within strict budgets.</item>
/// <item>Verify every candidate edge's evidence for this reader, and keep only edges reachable through
/// authorized edges alone.</item>
/// <item>Add those edges' chunks: deduplicated against the baseline, ordered by hop count, and capped by
/// count and size.</item>
/// </list>
/// Anything that fails, times out, or is uncertain yields no graph chunks, so the caller simply keeps its
/// baseline results. Nothing returned names an entity, a path, or a count from evidence the reader cannot see.
/// </summary>
public sealed class GraphRetrievalService(
    IOptions<GraphRagOptions> options,
    IOptions<SharePointOptions> sharePointOptions,
    GraphQueryRouter router,
    GraphEntityLinker linker,
    IGraphReader reader,
    GraphEvidenceVerifier verifier,
    ILogger<GraphRetrievalService> logger) : IGraphRetrievalService
{
    public bool ReturnsResults => options.Value.RetrievalEnabled && options.Value.IsEnabledForTenant(sharePointOptions.Value.TenantId);

    public async Task<GraphRetrievalResult> AugmentAsync(GraphRetrievalRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = GraphRagMetrics.Activities.StartActivity("GraphRag.Retrieve");
        GraphRetrievalResult result;
        try
        {
            result = await AugmentCoreAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Only the exception type is logged: messages from the store or the search service can carry
            // identifiers or text of evidence the reader was not cleared for.
            logger.LogWarning("Graph retrieval fell back to the baseline results after a {ExceptionType}.", exception.GetType().Name);
            result = GraphRetrievalResult.Empty(GraphRetrievalStatus.FellBack);
        }

        activity?.SetTag("graph_rag.status", result.Status.ToString());
        activity?.SetTag("graph_rag.chunks", result.Chunks.Count);
        GraphRagMetrics.Retrieval(result.Status.ToString(), stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    private async Task<GraphRetrievalResult> AugmentCoreAsync(GraphRetrievalRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var tenantId = sharePointOptions.Value.TenantId;
        if ((!settings.RetrievalEnabled && !settings.ShadowRetrieval) || !settings.IsEnabledForTenant(tenantId))
        {
            return GraphRetrievalResult.Empty(GraphRetrievalStatus.Disabled);
        }

        // Unfiltered search is how the existing code treats a missing user; the graph never does.
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return GraphRetrievalResult.Empty(GraphRetrievalStatus.NoUser);
        }

        var route = router.Route(request.Query);
        if (!route.IsGraphQuestion)
        {
            return GraphRetrievalResult.Empty(GraphRetrievalStatus.NotGraphQuestion);
        }

        var traversal = settings.Traversal;
        var seeds = await SeedsAsync(tenantId, request, traversal, cancellationToken);
        if (seeds.Count == 0)
        {
            return GraphRetrievalResult.Empty(GraphRetrievalStatus.NoSeedEntities);
        }

        var plan = new GraphTraversalPlan(
            tenantId,
            seeds,
            route.Predicates,
            traversal.DefaultMaxDepth,
            traversal.MaxEntities,
            traversal.MaxAssertions,
            GraphTraversalDirection.Both,
            TimeSpan.FromMilliseconds(traversal.TimeoutMilliseconds),
            traversal.MaxRequestCharge);
        var graph = await reader.TraverseAsync(plan, cancellationToken);
        if (graph.Edges.Count == 0)
        {
            return GraphRetrievalResult.Empty(GraphRetrievalStatus.NoAuthorizedEvidence);
        }

        var verification = await verifier.VerifyAsync(
            tenantId, request.UserId, graph.Edges.Select(edge => edge.Assertion).ToList(), cancellationToken);
        var usable = GraphPathAuthorization.SelectReachableEdges(graph.StartEntityIds, graph.Edges, verification.AuthorizedAssertionIds);
        GraphRagMetrics.AclRejected("unreachable", verification.AuthorizedAssertionIds.Count - usable.Count);

        var baselineIds = request.BaselineHits.Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal);
        var chunks = new List<GraphRelatedChunk>();
        var characters = 0;
        foreach (var edge in usable)
        {
            if (chunks.Count >= traversal.MaxAdditionalChunks)
            {
                break;
            }

            var chunkId = edge.Assertion.Evidence.ChunkId;
            if (baselineIds.Contains(chunkId) || chunks.Any(chunk => chunk.Hit.Id == chunkId)
                || !verification.Chunks.TryGetValue(chunkId, out var hit))
            {
                continue;
            }

            if (characters + hit.Content.Length > traversal.MaxAdditionalCharacters)
            {
                continue;
            }

            characters += hit.Content.Length;
            chunks.Add(new GraphRelatedChunk(hit, edge.Assertion.Predicate, edge.Depth, edge.Assertion.Status == GraphAssertionStatus.Disputed));
        }

        logger.LogInformation(
            "Graph retrieval traversed {EdgeCount} candidate edges, verified {AuthorizedCount}, and added {ChunkCount} chunks.",
            graph.Edges.Count, usable.Count, chunks.Count);
        if (chunks.Count == 0)
        {
            return GraphRetrievalResult.Empty(GraphRetrievalStatus.NoAuthorizedEvidence);
        }

        if (!settings.RetrievalEnabled)
        {
            return new GraphRetrievalResult(GraphRetrievalStatus.Shadow, request.IncludeShadowResults ? chunks : []);
        }
        return new GraphRetrievalResult(GraphRetrievalStatus.Augmented, chunks);
    }

    /// <summary>
    /// Entities to start from: those the question names without ambiguity first, then those asserted in the
    /// baseline and seed chunks. Those chunks were returned by the permission-trimmed search, so starting from
    /// what they assert reveals nothing; every edge found from there is still verified.
    /// </summary>
    private async Task<IReadOnlyList<string>> SeedsAsync(
        string tenantId, GraphRetrievalRequest request, GraphTraversalOptions traversal, CancellationToken cancellationToken)
    {
        var seeds = new List<string>();
        var linked = await linker.LinkAsync(tenantId, request.Query, cancellationToken);
        seeds.AddRange(linked.EntityIds);

        var authorizedHits = request.BaselineHits.Concat(request.SeedHits ?? []).ToList();
        var baselineChunks = authorizedHits.Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal);
        var documentIds = authorizedHits
            .Select(hit => $"{hit.DriveId}:{hit.ItemId}")
            .Distinct(StringComparer.Ordinal)
            .Take(traversal.MaxEvidenceDocuments)
            .ToList();
        if (documentIds.Count > 0)
        {
            var states = await reader.GetDocumentStatesAsync(tenantId, documentIds, cancellationToken);
            foreach (var entry in states
                .Where(state => state.TenantId == tenantId && state.Status == GraphProjectionStatus.Ready)
                .SelectMany(state => state.Manifest)
                .Where(entry => baselineChunks.Contains(entry.ChunkId)))
            {
                seeds.Add(entry.SubjectEntityId);
                seeds.Add(entry.ObjectEntityId);
            }
        }

        return seeds.Distinct(StringComparer.Ordinal).Take(traversal.MaxSeedEntities).ToList();
    }
}
