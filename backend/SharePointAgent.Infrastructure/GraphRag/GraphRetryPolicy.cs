namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Exponential backoff with jitter, shared by model calls and redelivered indexing requests. The jitter
/// keeps a burst of throttled workers from retrying in lockstep.
/// </summary>
public static class GraphRetryPolicy
{
    /// <summary>The delay before retry <paramref name="attempt"/> (1-based): half to all of the capped exponential delay.</summary>
    public static TimeSpan Delay(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var exponential = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 20));
        var capped = Math.Min(exponential, maxDelay.TotalMilliseconds);
        var jitter = 0.5 + ((random ?? Random.Shared).NextDouble() * 0.5);
        return TimeSpan.FromMilliseconds(capped * jitter);
    }
}
