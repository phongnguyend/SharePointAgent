using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

public sealed record GraphReconciliationReport(int Checked, int Repaired, int Removed, int Failed);

/// <summary>
/// Compares what the indexer tracks with what the graph serves and repairs the difference. It covers indexing
/// requests that were lost, projections that failed or stopped half-way, extraction-version upgrades, and
/// documents deleted while the worker was down. Repairs go through the indexing pipeline, which replays
/// archived snapshots before it considers calling the model, so a rebuild of an empty graph store costs no
/// extraction at all.
/// </summary>
public sealed class GraphReconciliationService(
    IOptions<GraphRagOptions> options,
    IOptions<SharePointOptions> sharePointOptions,
    IIndexStateRepository indexState,
    IFileMetadataRepository metadata,
    IGraphReader reader,
    IGraphProjectionStore store,
    IGraphWriter writer,
    IGraphIndexingPipeline pipeline,
    TimeProvider time,
    ILogger<GraphReconciliationService> logger)
{
    public async Task<GraphReconciliationReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var tenantId = sharePointOptions.Value.TenantId;
        if (!settings.IndexingEnabled || !settings.IsEnabledForTenant(tenantId))
        {
            return new GraphReconciliationReport(0, 0, 0, 0);
        }

        using var activity = GraphRagMetrics.Activities.StartActivity("GraphRag.Reconcile");
        var maxRepairs = settings.Reconciliation.MaxRepairsPerCycle;
        int checkedCount = 0, repaired = 0, removed = 0, failed = 0;

        // Indexed files whose graph is missing, stale, failed, or unsettled.
        for (var skip = 0; repaired + failed < maxRepairs; )
        {
            var page = await indexState.ListFilesAsync(
                new IndexedFileQuery(Sort: "name", Descending: false, Skip: skip, Top: settings.Reconciliation.PageSize), cancellationToken);
            if (page.Items.Count == 0)
            {
                break;
            }

            var states = (await reader.GetDocumentStatesAsync(
                    tenantId, page.Items.Select(row => GraphDocumentIds.ForDriveItem(row.DriveId, row.ItemId)).ToList(), cancellationToken))
                .ToDictionary(state => state.DocumentId, StringComparer.Ordinal);
            foreach (var row in page.Items)
            {
                checkedCount++;
                var revision = GraphDocumentIds.ForIndexedFile(row.CTag, row.ETag, row.IndexFingerprint);
                if (revision is null || repaired + failed >= maxRepairs)
                {
                    continue;
                }

                var reason = RepairReason(states.GetValueOrDefault(GraphDocumentIds.ForDriveItem(row.DriveId, row.ItemId)), revision);
                if (reason is null)
                {
                    continue;
                }

                if (await TryProcessAsync(new GraphIndexingRequest(GraphIndexingRequestKind.Indexed, row.DriveId, row.ItemId, time.GetUtcNow()), cancellationToken))
                {
                    repaired++;
                    GraphRagMetrics.Repaired(reason);
                }
                else
                {
                    failed++;
                }
            }

            skip += page.Items.Count;
            if (skip >= page.TotalCount)
            {
                break;
            }
        }

        // Graph documents the indexer no longer tracks, and tombstones whose cleanup did not finish.
        await foreach (var state in store.ListDocumentStatesAsync(tenantId, cancellationToken))
        {
            if (repaired + removed + failed >= maxRepairs)
            {
                break;
            }

            if (state.Status == GraphProjectionStatus.Tombstoned)
            {
                if (state.PendingManifest.Count > 0)
                {
                    await writer.RetractDocumentAsync(tenantId, state.DocumentId, cancellationToken);
                    removed++;
                    GraphRagMetrics.Repaired("tombstoneCleanup");
                }
                continue;
            }

            if (GraphDocumentIds.TryParseDriveItem(state.DocumentId, out var driveId, out var itemId)
                && await metadata.GetAsync(driveId, itemId, cancellationToken) is null)
            {
                if (await TryProcessAsync(new GraphIndexingRequest(GraphIndexingRequestKind.Removed, driveId, itemId, time.GetUtcNow()), cancellationToken))
                {
                    removed++;
                    GraphRagMetrics.Repaired("orphan");
                }
                else
                {
                    failed++;
                }
            }
        }

        logger.LogInformation(
            "Graph reconciliation checked {Checked} indexed files, repaired {Repaired}, removed {Removed}, and failed on {Failed}.",
            checkedCount, repaired, removed, failed);
        return new GraphReconciliationReport(checkedCount, repaired, removed, failed);
    }

    private string? RepairReason(GraphDocumentState? state, string revision)
    {
        if (state is null)
        {
            return "missing";
        }

        if (state.Status == GraphProjectionStatus.Failed)
        {
            return "failed";
        }

        if (!state.IsServing(revision))
        {
            return "stale";
        }

        if (state.ActiveExtractionVersion != pipeline.CurrentExtractionVersion)
        {
            return "extractionVersion";
        }
        return state.PendingManifest.Count > 0 ? "unsettled" : null;
    }

    private async Task<bool> TryProcessAsync(GraphIndexingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await pipeline.ProcessAsync(request, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception switch
            {
                GraphPermanentFailureException permanent => permanent.Code,
                GraphTransientFailureException transient => transient.Code,
                _ => exception.GetType().Name
            };
            GraphRagMetrics.IndexingFailed(code, retried: false);
            logger.LogWarning("Graph reconciliation could not repair item {ItemId}: {FailureCode}.", request.ItemId, code);
            return false;
        }
    }
}
