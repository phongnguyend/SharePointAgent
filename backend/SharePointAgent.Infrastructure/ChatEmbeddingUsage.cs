using Microsoft.Extensions.AI;

namespace SharePointAgent.Infrastructure;

/// <summary>Provider-reported query embedding tokens, isolated to the current chat turn.</summary>
public sealed class ChatEmbeddingUsage : IDisposable
{
    private static readonly AsyncLocal<ChatEmbeddingUsage?> Current = new();
    private readonly ChatEmbeddingUsage? previous;
    private long tokens;
    public long TotalTokens => Interlocked.Read(ref tokens);

    private ChatEmbeddingUsage()
    {
        previous = Current.Value;
        Current.Value = this;
    }

    public static ChatEmbeddingUsage Begin() => new();

    public static async Task<ReadOnlyMemory<float>> GenerateQueryVectorAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, string query, CancellationToken ct)
    {
        var scope = Current.Value;
        var generated = await generator.GenerateAsync([query], cancellationToken: ct);
        var count = generated.Usage?.TotalTokenCount ?? generated.Usage?.InputTokenCount ?? 0;
        if (scope is not null) Interlocked.Add(ref scope.tokens, Math.Max(0, count));
        return generated[0].Vector;
    }

    public void Dispose() => Current.Value = previous;
}
