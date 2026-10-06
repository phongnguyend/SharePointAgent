namespace SharePointAgent.Persistence;

public sealed class WorkerHeartbeatEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset LastHeartbeatUtc { get; set; }

    public DateTimeOffset? LastSyncSucceededUtc { get; set; }

    public DateTimeOffset? LastFailureUtc { get; set; }

    public string? ActiveFailure { get; set; }

    public DateTimeOffset? SubscriptionExpiresUtc { get; set; }
}
