using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class TextChunkerTests
{
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
