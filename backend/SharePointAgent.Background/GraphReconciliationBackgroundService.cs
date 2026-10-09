using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure.GraphRag;

namespace SharePointAgent.Background;

/// <summary>
/// Runs graph reconciliation on a timer. It repairs lost indexing requests, unfinished projections, extraction
/// upgrades, and orphaned graph documents, and it rebuilds an empty graph store from the snapshot archive.
/// A cycle that fails is logged and the next one tries again.
/// </summary>
public sealed class GraphReconciliationBackgroundService(
    GraphReconciliationService reconciliation,
    IOptions<GraphRagOptions> options,
    ILogger<GraphReconciliationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Reconciliation;
        if (!settings.Enabled)
        {
            logger.LogInformation("Graph reconciliation is disabled.");
            return;
        }

        // Let the change listener's startup sync run first.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.IntervalMinutes));
        do
        {
            try
            {
                await reconciliation.ReconcileAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Graph reconciliation failed; it will run again at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
