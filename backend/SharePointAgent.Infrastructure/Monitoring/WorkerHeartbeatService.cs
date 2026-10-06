using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure.Monitoring;

public sealed class WorkerHeartbeatService(
    IDbContextFactory<SharePointIndexDbContext> factory,
    WorkerHealthState state,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private Guid? _id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await PublishAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A monitoring failure must not stop indexing; the persisted heartbeat will go stale.
                logger.LogWarning(ex, "Could not persist Background heartbeat. Retrying in 30 seconds.");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var snapshot = state.Snapshot();
        var row = _id is { } id
            ? await db.Set<WorkerHeartbeatEntity>().FindAsync([id], cancellationToken)
            : null;
        if (row is null)
        {
            db.Add(snapshot);
            row = snapshot;
        }
        else
        {
            row.LastHeartbeatUtc = snapshot.LastHeartbeatUtc;
            row.LastSyncSucceededUtc = snapshot.LastSyncSucceededUtc;
            row.LastFailureUtc = snapshot.LastFailureUtc;
            row.ActiveFailure = snapshot.ActiveFailure;
            row.SubscriptionExpiresUtc = snapshot.SubscriptionExpiresUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        _id = row.Id;
    }
}
