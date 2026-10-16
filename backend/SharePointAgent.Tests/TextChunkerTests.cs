using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class TextChunkerTests
{
    [Fact]
    public void EndsAndOverlapsUseWholeWords()
    {
        Assert.Equal(new[] { "alpha beta", "beta gamma", "gamma delta" }, TextChunker.Split("alpha beta gamma delta", 11, 5));
    }

    [Fact]
    public void NoOverlapPreservesEveryWord()
    {
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, TextChunker.Split("alpha beta gamma", 8, 0));
    }

    [Fact]
    public void OversizedWordsStayIntactAndMakeProgress()
    {
        Assert.Equal(new[] { "extraordinary", "word" }, TextChunker.Split("extraordinary word", 5, 4));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(5, -1)]
    [InlineData(5, 5)]
    public void InvalidSizesAreRejected(int size, int overlap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextChunker.Split("text", size, overlap));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("\u00a0\u2003")]
    public void EmptyTextProducesNoEmbeddingInputs(string text)
    {
        Assert.Empty(TextChunker.Split(text, 100, 10));
    }

    [Fact]
    public void NonEmptyTextStillProducesChunks()
    {
        Assert.Equal(new[] { "hello world" }, TextChunker.Split(" hello\nworld ", 100, 10));
    }
}
