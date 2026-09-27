using Microsoft.Extensions.AI;
using NSubstitute;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatEmbeddingUsageTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> Generator(long tokens)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1 })])
            { Usage = new UsageDetails { InputTokenCount = tokens, TotalTokenCount = tokens } });
        return generator;
    }

    [Fact]
    public async Task AccumulatesQueryEmbeddingsAcrossParallelToolCallsAndResetsPerTurn()
    {
        using (var usage = ChatEmbeddingUsage.Begin())
        {
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ChatEmbeddingUsage.GenerateQueryVectorAsync(Generator(7), "query", default)));
            Assert.Equal(35, usage.TotalTokens);
        }
        using var next = ChatEmbeddingUsage.Begin();
        Assert.Equal(0, next.TotalTokens);
    }

    [Fact]
    public async Task ConcurrentTurnsHaveIndependentCounts()
    {
        async Task<long> Turn(long count)
        {
            using var usage = ChatEmbeddingUsage.Begin();
            await Task.Yield();
            await ChatEmbeddingUsage.GenerateQueryVectorAsync(Generator(count), "query", default);
            return usage.TotalTokens;
        }
        var totals = await Task.WhenAll(Turn(4), Turn(9));
        Assert.Equal(new long[] { 4, 9 }, totals);
    }
}
