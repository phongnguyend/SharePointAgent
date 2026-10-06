using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SharePointAgent.Infrastructure.Monitoring;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class WorkerHeartbeatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "healthy")]
    [InlineData(119, "healthy")]
    [InlineData(120, "unhealthy")]
    [InlineData(300, "unhealthy")]
    public void IdleWorkerStaysHealthyUntilHeartbeatExpires(int ageSeconds, string expected)
    {
        var row = new WorkerHeartbeatEntity { LastHeartbeatUtc = Now.AddSeconds(-ageSeconds) };
        Assert.Equal(expected, BackgroundHealthMonitor.Evaluate(row, Now).Status);
    }

    [Fact]
    public void MissingWorkerIsUnknown()
    {
        Assert.Equal("unknown", BackgroundHealthMonitor.Evaluate(null, Now).Status);
    }

    [Fact]
    public void RecoveryClearsOnlyItsOwnFailureAndPreservesFailureTime()
    {
        var clock = new Clock();
        var state = new WorkerHealthState(clock);
        state.Failed("Synchronization");
        state.Failed("Subscription renewal");
        clock.Now = Now.AddSeconds(30);
        state.Succeeded("Synchronization");
        var degraded = state.Snapshot();
        Assert.Equal("degraded", BackgroundHealthMonitor.Evaluate(degraded, clock.Now).Status);
        Assert.Equal(Now, degraded.LastFailureUtc);
        Assert.Equal(clock.Now, degraded.LastSyncSucceededUtc);
        Assert.DoesNotContain("Synchronization", degraded.ActiveFailure!);
        state.SubscriptionChecked(clock.Now.AddDays(2));
        var recovered = state.Snapshot();
        Assert.Equal("healthy", BackgroundHealthMonitor.Evaluate(recovered, clock.Now).Status);
        Assert.Equal(Now, recovered.LastFailureUtc);
    }

    [Fact]
    public void ExpiredSubscriptionDegradesCurrentWorkerButStaleHeartbeatTakesPriority()
    {
        var row = new WorkerHeartbeatEntity { LastHeartbeatUtc = Now, SubscriptionExpiresUtc = Now.AddMinutes(-1) };
        Assert.Equal("degraded", BackgroundHealthMonitor.Evaluate(row, Now).Status);
        Assert.Equal("unhealthy", BackgroundHealthMonitor.Evaluate(row, Now.AddMinutes(3)).Status);
    }

    [Fact]
    public async Task PublisherUpdatesItsRowAndRestartGetsADatabaseGeneratedId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using (var db = new SharePointIndexDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        var clock = new Clock();
        var state = new WorkerHealthState(clock);
        Assert.Equal(Guid.Empty, state.Snapshot().Id);
        using var publisher = new WorkerHeartbeatService(factory, state, NullLogger<WorkerHeartbeatService>.Instance);
        await publisher.PublishAsync(default);
        clock.Now = Now.AddSeconds(30);
        state.Failed("Synchronization");
        await publisher.PublishAsync(default);
        await using (var db = new SharePointIndexDbContext(options))
        {
            var row = Assert.Single(await db.Set<WorkerHeartbeatEntity>().ToListAsync());
            Assert.NotEqual(Guid.Empty, row.Id);
            Assert.Equal(clock.Now, row.LastHeartbeatUtc);
            Assert.Contains("Synchronization", row.ActiveFailure!);
            Assert.Equal(Now, row.StartedAtUtc);
        }
        using var restarted = new WorkerHeartbeatService(factory, new WorkerHealthState(clock), NullLogger<WorkerHeartbeatService>.Instance);
        await restarted.PublishAsync(default);
        await using var verify = new SharePointIndexDbContext(options);
        Assert.Equal(2, await verify.Set<WorkerHeartbeatEntity>().CountAsync());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = WorkerHeartbeatTests.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
