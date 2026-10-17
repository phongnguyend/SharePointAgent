using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure.KnowledgeGraph;

public sealed record GraphModelUsage(string ModelId, long? InputTokens, long? OutputTokens, long? TotalTokens);

public sealed class GraphModelUsageScope : IDisposable
{
    private static readonly AsyncLocal<GraphModelUsageScope?> Slot = new();
    private readonly GraphModelUsageScope? _previous;
    private readonly List<GraphModelUsage> _requests = [];

    public static GraphModelUsageScope? Current => Slot.Value;

    public string Operation { get; }

    public IReadOnlyList<GraphModelUsage> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public GraphModelUsageScope(string operation)
    {
        Operation = operation;
        _previous = Slot.Value;
        Slot.Value = this;
    }

    internal void Add(GraphModelUsage usage)
    {
        lock (_requests)
        {
            _requests.Add(usage);
        }
    }

    public void Dispose()
    {
        Slot.Value = _previous;
    }
}

public sealed class GraphChatUsageRecorder(IDbContextFactory<SharePointIndexDbContext> factory)
{
    public async ValueTask RecordAsync(string modelId, string operation, ChatRequestUsage request, CancellationToken ct)
    {
        var usage = request.Usage;
        var total = usage?.TotalTokenCount ?? (usage?.InputTokenCount is { } input && usage.OutputTokenCount is { } output ? input + output : (long?)null);
        GraphModelUsageScope.Current?.Add(new(modelId, usage?.InputTokenCount, usage?.OutputTokenCount, total));
        var attribution = EmbeddingUsageScope.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ChatTokenUsage.Add(new ChatTokenUsageEntity
        {
            CreatedAtUtc = now,
            Day = MonthlyTokenQuota.DayKey(now),
            Month = MonthlyTokenQuota.MonthKey(now),
            UserId = attribution.UserId,
            Operation = GraphModelUsageScope.Current?.Operation ?? operation,
            ModelId = modelId,
            Sequence = request.Sequence,
            InputTokens = usage?.InputTokenCount,
            OutputTokens = usage?.OutputTokenCount,
            TotalTokens = total
        });
        await db.SaveChangesAsync(ct);
    }
}
