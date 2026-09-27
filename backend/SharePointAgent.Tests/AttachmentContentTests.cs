using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AttachmentContentTests
{
    [Fact]
    public async Task OriginalDownloadNeverReturnsMarkdownEvenWhenMarkdownIsCached()
    {
        var root = Path.Combine(Path.GetTempPath(), "attachment-distinct-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            var service = Substitute.For<BlobServiceClient>();
            var container = Substitute.For<BlobContainerClient>();
            var originalBlob = Substitute.For<BlobClient>();
            var markdownBlob = Substitute.For<BlobClient>();
            service.GetBlobContainerClient("chat-uploads").Returns(container);
            container.GetBlobClient("original.docx").Returns(originalBlob);
            container.GetBlobClient($"markdown-cache/{id:N}/content.md").Returns(markdownBlob);
            byte[] originalBytes = [0x50, 0x4b, 3, 4, 0, 255];
            originalBlob.DownloadContentAsync(Arg.Any<CancellationToken>()).Returns(Response.FromValue(
                BlobsModelFactory.BlobDownloadResult(new BinaryData(originalBytes)), Substitute.For<Response>()));
            var etag = new ETag("markdown-v1");
            markdownBlob.GetPropertiesAsync(cancellationToken: Arg.Any<CancellationToken>()).Returns(Response.FromValue(
                BlobsModelFactory.BlobProperties(eTag: etag), Substitute.For<Response>()));
            markdownBlob.DownloadContentAsync(Arg.Any<CancellationToken>()).Returns(Response.FromValue(
                BlobsModelFactory.BlobDownloadResult(BinaryData.FromString("# Converted text"), BlobsModelFactory.BlobDownloadDetails(eTag: etag)), Substitute.For<Response>()));
            using var cache = new AttachmentContentCache(service, null!, Options.Create(new UploadOptions { CacheDirectory = root }));
            var file = new ChatMessageAttachmentFileEntity { Id = id, FileName = "report.docx", BlobName = "original.docx", SizeBytes = originalBytes.Length, Status = UploadIndexStatus.Indexed };
            var markdown = await cache.GetMarkdownAsync(file, default);
            var original = await cache.DownloadAsync(file, default);
            Assert.NotEqual(markdown.LocalPath, original.LocalPath);
            Assert.Equal(".docx", Path.GetExtension(original.LocalPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(original.LocalPath));
            Assert.Equal("# Converted text", await File.ReadAllTextAsync(markdown.LocalPath));
            Assert.True((await cache.DownloadAsync(file, default)).CacheHit);
            Assert.True((await cache.GetMarkdownAsync(file, default)).MarkdownCacheHit);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(original.LocalPath));
        }
        finally { if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData("notes.MD", false)]
    [InlineData("data.json", false)]
    [InlineData("notes.txt", true)]
    [InlineData("data.csv", false)]
    public async Task TextAttachmentsPreserveContentWithoutCallingConverter(string name, bool utf16)
    {
        var root = Path.Combine(Path.GetTempPath(), "attachment-text-tests-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var directory = Path.Combine(root, id.ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string text = "# Title\r\n  {\"value\": \"\u03b1\"}\n\n";
            var path = Path.Combine(directory, "original" + Path.GetExtension(name));
            await File.WriteAllTextAsync(path, text, utf16 ? System.Text.Encoding.Unicode : new System.Text.UTF8Encoding(false));
            // No converter or Blob client: cached text must be read directly on every index/reindex.
            using var cache = new AttachmentContentCache(null!, null!, Options.Create(new UploadOptions
            {
                CacheDirectory = root,
                TextFileExtensions = [" txt ", ".MD", ".json", "CSV"]
            }));
            var file = new ChatMessageAttachmentFileEntity { Id = id, FileName = name, SizeBytes = new FileInfo(path).Length };
            Assert.Equal(text, await cache.ConvertForIndexAsync(file, default));
            Assert.Equal(text, await cache.ConvertForIndexAsync(file, default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishingMarkdownOverwritesTheSameBlobWithExactIndexedText()
    {
        var service = Substitute.For<BlobServiceClient>();
        var container = Substitute.For<BlobContainerClient>();
        var blob = Substitute.For<BlobClient>();
        service.GetBlobContainerClient("chat-uploads").Returns(container);
        var id = Guid.NewGuid();
        container.GetBlobClient($"markdown-cache/{id:N}/content.md").Returns(blob);
        blob.UploadAsync(Arg.Any<BinaryData>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Response<BlobContentInfo>>(null!));
        using var cache = new AttachmentContentCache(service, null!, Options.Create(new UploadOptions()));
        await cache.StoreMarkdownAsync(id, "# First\r\nα", default);
        await cache.StoreMarkdownAsync(id, "# Reindexed\nβ", default);
        await blob.Received(1).UploadAsync(Arg.Is<BinaryData>(b => b.ToString() == "# First\r\nα"), true, Arg.Any<CancellationToken>());
        await blob.Received(1).UploadAsync(Arg.Is<BinaryData>(b => b.ToString() == "# Reindexed\nβ"), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingMarkdownRequiresReindexInsteadOfReconversion()
    {
        var root = Path.Combine(Path.GetTempPath(), "attachment-tests-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var directory = Path.Combine(root, id.ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "original.bin"), "source");
            var service = Substitute.For<BlobServiceClient>();
            var container = Substitute.For<BlobContainerClient>();
            var blob = Substitute.For<BlobClient>();
            service.GetBlobContainerClient("chat-uploads").Returns(container);
            container.GetBlobClient($"markdown-cache/{id:N}/content.md").Returns(blob);
            blob.GetPropertiesAsync(cancellationToken: Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Response<BlobProperties>>(new RequestFailedException(404, "Missing")));
            // A converter is deliberately absent: a read must never attempt conversion.
            using var cache = new AttachmentContentCache(service, null!, Options.Create(new UploadOptions { CacheDirectory = root }));
            var error = await Assert.ThrowsAsync<AttachmentMarkdownUnavailableException>(() => cache.GetMarkdownAsync(
                new ChatMessageAttachmentFileEntity { Id = id, SizeBytes = 6, Status = UploadIndexStatus.Indexed }, default));
            Assert.Contains("Reindex", error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnindexedFilesCannotReadOrGenerateMarkdown()
    {
        using var cache = new AttachmentContentCache(null!, null!, Options.Create(new UploadOptions()));
        await Assert.ThrowsAsync<AttachmentMarkdownUnavailableException>(() => cache.GetMarkdownAsync(
            new ChatMessageAttachmentFileEntity { Status = UploadIndexStatus.Failed }, default));
    }

    [Fact]
    public void LineRangesAreInclusiveAndHandleMixedNewlines()
    {
        var result = AttachmentMarkdownReader.Read(Guid.NewGuid(), "file", new("one\r\ntwo\nthree\rfour", "", true, true), 2, 3);
        Assert.Equal("two\nthree", result.Markdown);
        Assert.Equal(4, result.TotalLines);
        Assert.Equal(4, result.NextLine);
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData(1000, 500)]
    public void LargeDocumentsReturnContinuation(int? endLine, int expectedEnd)
    {
        var text = string.Join('\n', Enumerable.Range(1, 1000));
        var result = AttachmentMarkdownReader.Read(Guid.NewGuid(), "file", new(text, "", true, true), 1, endLine);
        Assert.Equal(expectedEnd, result.EndLine);
        Assert.Equal(expectedEnd + 1, result.NextLine);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    public void InvalidRangesAreRejected(int start, int end) =>
        Assert.Throws<ArgumentException>(() => AttachmentMarkdownReader.ValidateRange(start, end));
}
