using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure.Monitoring;

// Shared only by hosted services in the Background process, never by the API's processor.
public sealed class WorkerHealthState(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly DateTimeOffset _started = clock.GetUtcNow();
    private readonly HashSet<string> _failures = [];
    private DateTimeOffset? _syncSucceeded;
    private DateTimeOffset? _lastFailure;
    private DateTimeOffset? _subscriptionExpires;

    public void Failed(string component)
    {
        lock (_gate)
        {
            _failures.Add(component);
            _lastFailure = clock.GetUtcNow();
        }
    }

    public void Succeeded(string component)
    {
        lock (_gate)
        {
            _failures.Remove(component);
            if (component == "Synchronization")
            {
                _syncSucceeded = clock.GetUtcNow();
            }
        }
    }

    public void SubscriptionChecked(DateTimeOffset? expires)
    {
        lock (_gate)
        {
            _failures.Remove("Subscription renewal");
            _subscriptionExpires = expires;
        }
    }

    public WorkerHeartbeatEntity Snapshot()
    {
        lock (_gate)
        {
            return new()
            {
                StartedAtUtc = _started,
                LastHeartbeatUtc = clock.GetUtcNow(),
                LastSyncSucceededUtc = _syncSucceeded,
                LastFailureUtc = _lastFailure,
                ActiveFailure = _failures.Count == 0 ? null : string.Join(", ", _failures.Order()) + " failed. Check Background logs.",
                SubscriptionExpiresUtc = _subscriptionExpires
            };
        }
    }
}
