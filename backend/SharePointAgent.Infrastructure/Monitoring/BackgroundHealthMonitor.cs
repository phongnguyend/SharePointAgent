using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure.Monitoring;

public sealed class BackgroundHealthMonitor(IDbContextFactory<SharePointIndexDbContext> factory, TimeProvider clock)
{
    public async Task<ServiceHealthStatus> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var db = await factory.CreateDbContextAsync(timeout.Token);
            var row = await db.Set<WorkerHeartbeatEntity>().AsNoTracking()
                .OrderByDescending(x => x.LastHeartbeatUtc).FirstOrDefaultAsync(timeout.Token);
            return Evaluate(row, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown();
        }
        catch (DbException)
        {
            return Unknown();
        }

        ServiceHealthStatus Unknown() => new("Background", "unknown", "Unable to read heartbeat. Check database connectivity and apply the heartbeat migration.", null, clock.GetUtcNow());
    }

    public static ServiceHealthStatus Evaluate(WorkerHeartbeatEntity? row, DateTimeOffset now)
    {
        if (row is null)
        {
            return new("Background", "unknown", "No heartbeat received. Start or deploy the Background worker.", null, now);
        }
        var status = "healthy";
        var message = "Background heartbeat is current.";
        if (now - row.LastHeartbeatUtc >= TimeSpan.FromMinutes(2))
        {
            status = "unhealthy";
            message = "No heartbeat for at least two minutes. The worker may be stopped or unable to reach the database.";
        }
        else if (row.ActiveFailure is not null || row.SubscriptionExpiresUtc <= now)
        {
            status = "degraded";
            message = row.ActiveFailure ?? "The last observed webhook subscription has expired.";
        }
        return new("Background", status, message, null, now, row.LastHeartbeatUtc,
            row.LastSyncSucceededUtc, row.LastFailureUtc, row.SubscriptionExpiresUtc);
    }
}
