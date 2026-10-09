using System.Diagnostics;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag;

namespace SharePointAgent.Api;

public sealed record GraphEntityMergePayload(string? SourceEntityId, string? TargetEntityId);

public sealed record GraphEvaluationCasePayload(string? Question, IReadOnlyList<string>? ExpectedChunkIds);

public sealed record GraphEvaluationPayload(string? UserId, int Top = 5, IReadOnlyList<GraphEvaluationCasePayload>? Cases = null);

/// <summary>
/// Operator endpoints for Graph RAG. They are under <c>/api/admin</c>, so the existing access rules apply:
/// reader admins may look, and only global admins may change anything or run an evaluation. Nothing here
/// returns document text, entity names, or graph paths. There is deliberately no endpoint that returns
/// traversal results: graph facts reach users only through the verified retrieval path.
/// </summary>
public static class GraphRagEndpoints
{
    private const int MaxEvaluationCases = 50;

    public static void MapGraphRagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/graph-rag");

        group.MapGet("/documents/{driveId}/{itemId}", async (
            string driveId,
            string itemId,
            HttpContext context,
            IFileMetadataRepository metadata,
            IOptions<SharePointOptions> sharePoint,
            CancellationToken cancellationToken) =>
        {
            if (context.RequestServices.GetService<IGraphReader>() is not { } reader)
            {
                return NotEnabled();
            }

            if (driveId.Contains(':') || itemId.Contains(':'))
            {
                return Results.BadRequest(new { error = "Drive and item IDs must not contain ':'." });
            }

            var documentId = GraphDocumentIds.ForDriveItem(driveId, itemId);
            var state = await reader.GetDocumentStateAsync(sharePoint.Value.TenantId, documentId, cancellationToken);
            var record = await metadata.GetAsync(driveId, itemId, cancellationToken);
            var currentRevision = record is null ? null : GraphDocumentIds.ForIndexedFile(record);
            return Results.Ok(new
            {
                documentId,
                indexed = record is not null,
                currentRevision,
                status = state?.Status.ToString(),
                activeRevision = state?.ActiveRevision,
                servingCurrentRevision = state is not null && currentRevision is not null && state.IsServing(currentRevision),
                activeExtractionVersion = state?.ActiveExtractionVersion,
                assertionCount = state?.Manifest.Count ?? 0,
                pendingCount = state?.PendingManifest.Count ?? 0,
                failureCode = state?.FailureCode,
                updatedAtUtc = state?.UpdatedAtUtc
            });
        });

        group.MapPost("/entities/merge", async (
            GraphEntityMergePayload payload,
            HttpContext context,
            IOptions<SharePointOptions> sharePoint,
            CancellationToken cancellationToken) =>
        {
            if (context.RequestServices.GetService<IGraphWriter>() is not { } writer)
            {
                return NotEnabled();
            }

            if (string.IsNullOrWhiteSpace(payload.SourceEntityId) || string.IsNullOrWhiteSpace(payload.TargetEntityId))
            {
                return Results.BadRequest(new { error = "'sourceEntityId' and 'targetEntityId' are required." });
            }

            try
            {
                await writer.MergeEntitiesAsync(sharePoint.Value.TenantId, payload.SourceEntityId, payload.TargetEntityId, context.EntraObjectId(), cancellationToken);
                return Results.Ok(new { merged = payload.SourceEntityId, into = payload.TargetEntityId });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        // Retrieval-level comparison of baseline and graph-augmented search for one user's permissions, on a
        // labelled set of cross-document questions. Graph results are computed even in shadow mode, but only
        // their chunk IDs and the summary leave this endpoint.
        group.MapPost("/evaluate", async (
            GraphEvaluationPayload payload,
            HttpContext context,
            ISearchQueryStore search,
            CancellationToken cancellationToken) =>
        {
            if (context.RequestServices.GetService<IGraphRetrievalService>() is not { } retrieval)
            {
                return NotEnabled();
            }

            var cases = payload.Cases?
                .Where(item => !string.IsNullOrWhiteSpace(item.Question))
                .Select(item => new GraphEvaluationCase(item.Question!, item.ExpectedChunkIds ?? []))
                .ToList() ?? [];
            if (string.IsNullOrWhiteSpace(payload.UserId) || cases.Count == 0 || cases.Count > MaxEvaluationCases || payload.Top is < 1 or > 20)
            {
                return Results.BadRequest(new { error = $"'userId', 1-{MaxEvaluationCases} cases with questions, and 'top' between 1 and 20 are required." });
            }

            var baselineRuns = new List<GraphEvaluationRun>();
            var graphRuns = new List<GraphEvaluationRun>();
            var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in cases)
            {
                var stopwatch = Stopwatch.StartNew();
                var baseline = await search.SearchAsync(SearchQueryMode.Hybrid, new SearchQueryRequest(item.Question, payload.UserId, payload.Top, 0), cancellationToken);
                var baselineMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                var baselineIds = baseline.Items.Select(hit => hit.Id).ToList();
                baselineRuns.Add(new GraphEvaluationRun(item.Question, baselineIds, baselineMilliseconds));

                var graph = await retrieval.AugmentAsync(new GraphRetrievalRequest(payload.UserId, item.Question, baseline.Items, IncludeShadowResults: true), cancellationToken);
                graphRuns.Add(new GraphEvaluationRun(item.Question, baselineIds.Concat(graph.Chunks.Select(chunk => chunk.Hit.Id)).ToList(), stopwatch.Elapsed.TotalMilliseconds));
                statuses[graph.Status.ToString()] = statuses.GetValueOrDefault(graph.Status.ToString()) + 1;
            }

            return Results.Ok(new
            {
                baseline = GraphRagEvaluation.Summarize(cases, baselineRuns),
                graph = GraphRagEvaluation.Summarize(cases, graphRuns),
                graphStatuses = statuses
            });
        });
    }

    private static IResult NotEnabled() =>
        Results.NotFound(new { error = "Graph RAG is not enabled on this host. Set GraphRag:RetrievalEnabled or GraphRag:ShadowRetrieval." });
}
