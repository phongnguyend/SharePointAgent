using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ProtectedDownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "protected-download-tests", Guid.NewGuid().ToString("N"));

    private string CreateCachedFile()
    {
        var path = Path.Combine(_directory, "item", "document.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "encrypted input");
        return path;
    }

    private SharePointFileCache CreateCache(IProtectedFileService service) => new(null!, service,
        Options.Create(new DownloadOptions { Directory = _directory }), NullLogger<SharePointFileCache>.Instance);

    [Fact]
    public async Task CachedDownloadChecksProtectionAndReturnsDecryptedSize()
    {
        var path = CreateCachedFile();
        var service = Substitute.For<IProtectedFileService>();
        service.EnsureReadableAsync(path, "document.docx", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
{
    await File.WriteAllTextAsync(path, "readable");
    return new FileSensitivity(null, null, false, false, DateTimeOffset.UtcNow);
});
        using var cache = CreateCache(service);

        var result = await cache.DownloadAsync("item", "document.docx", default);

        Assert.Equal(path, result.LocalPath);
        Assert.Equal(8, result.SizeBytes);
        Assert.True(result.AlreadyOnDisk);
        Assert.Equal("readable", File.ReadAllText(path));
    }

    [Fact]
    public async Task CachedDownloadDoesNotReturnPathWhenProtectionAccessIsDenied()
    {
        CreateCachedFile();
        var service = Substitute.For<IProtectedFileService>();
        service.EnsureReadableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FileSensitivity>(new UnauthorizedAccessException("EXTRACT denied")));
        using var cache = CreateCache(service);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cache.DownloadAsync("item", "document.docx", default));
    }

    [Fact]
    public async Task DecryptedCopyCannotOverwriteProtectedSharePointDocument()
    {
        var path = CreateCachedFile();
        File.WriteAllText(path + ProtectedFileService.ProtectedOriginalSuffix, "protected original");
        using var cache = CreateCache(Substitute.For<IProtectedFileService>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.UploadAsync("item", "document.docx", default));

        Assert.Contains("Upload is blocked", error.Message);
        Assert.Equal("protected original", File.ReadAllText(path + ProtectedFileService.ProtectedOriginalSuffix));
    }

    [Fact]
    public async Task FreshDownloadPublishesDecryptedCopyAndRetainsProtectedOriginal()
    {
        var service = Substitute.For<IProtectedFileService>();
        service.EnsureReadableAsync(Arg.Any<string>(), "document.docx", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var path = call.ArgAt<string>(0);
                File.Copy(path, path + ProtectedFileService.ProtectedOriginalSuffix);
                await File.WriteAllTextAsync(path, "readable");
                return new FileSensitivity(null, null, true, true, DateTimeOffset.UtcNow);
            });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var http = new HttpClient(new DownloadHandler());
        using var graph = new GraphServiceClient(http);
        memory.Set("SharePointDrive___", new Drive { Id = "drive" });
        var client = new SharePointClient(graph, memory, Options.Create(new SharePointOptions()), service);
        using var cache = new SharePointFileCache(client, service, Options.Create(new DownloadOptions { Directory = _directory }), NullLogger<SharePointFileCache>.Instance);

        var result = await cache.DownloadAsync("item", "document.docx", default);

        Assert.Equal("readable", File.ReadAllText(result.LocalPath));
        Assert.Equal("encrypted input", File.ReadAllText(result.LocalPath + ProtectedFileService.ProtectedOriginalSuffix));
        Assert.Equal(8, result.SizeBytes);
        Assert.False(result.AlreadyOnDisk);
    }

    [Fact]
    public async Task FailedRefreshPreservesPreviousCopyAndProtection()
    {
        var path = CreateCachedFile();
        File.WriteAllText(path + ProtectedFileService.ProtectedOriginalSuffix, "previous protection");
        var service = Substitute.For<IProtectedFileService>();
        service.EnsureReadableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FileSensitivity>(new UnauthorizedAccessException("EXTRACT denied")));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var http = new HttpClient(new DownloadHandler());
        using var graph = new GraphServiceClient(http);
        memory.Set("SharePointDrive___", new Drive { Id = "drive" });
        var client = new SharePointClient(graph, memory, Options.Create(new SharePointOptions()), service);
        using var cache = new SharePointFileCache(client, service, Options.Create(new DownloadOptions { Directory = _directory }), NullLogger<SharePointFileCache>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cache.RefreshAsync("item", "document.docx", default));

        Assert.Equal("encrypted input", File.ReadAllText(path));
        Assert.Equal("previous protection", File.ReadAllText(path + ProtectedFileService.ProtectedOriginalSuffix));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    private sealed class DownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("encrypted input") });
    }

    [Fact]
    public async Task UnprotectedFileIsUnchangedAndNeedsNoTenantAuthentication()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "plain.txt");
        await File.WriteAllTextAsync(path, "Plain document");
        using var service = new ProtectedFileService(Options.Create(new SharePointOptions
        {
            ClientId = "00000000-0000-0000-0000-000000000001"
        }));

        await service.EnsureReadableAsync(path, "plain.txt", 1024, default);

        Assert.Equal("Plain document", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ProtectedFileService.ProtectedOriginalSuffix));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
