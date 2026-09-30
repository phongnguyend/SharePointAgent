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

    private LocalWorkingDirectoryOptions WorkingDirectory => new() { Directory = _directory };

    private string CreateLocalFile()
    {
        // An existing file in the sandbox must remain untouched unless explicitly overwritten.
        var path = Path.Combine(WorkingDirectory.ResolvedSharePointDirectory, "item", "document.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "encrypted input");
        return path;
    }

    private AgentSharePointFiles CreateTransfers() => new(null!,
        Options.Create(WorkingDirectory), NullLogger<AgentSharePointFiles>.Instance);

    [Fact]
    public async Task DecryptedCopyCannotOverwriteProtectedSharePointDocument()
    {
        var path = CreateLocalFile();
        File.WriteAllText(path + ProtectedFileService.ProtectedOriginalSuffix, "protected original");
        using var cache = CreateTransfers();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.UploadAsync("item", "document.docx", path, default));

        Assert.Contains("Upload is blocked", error.Message);
        Assert.Equal("protected original", File.ReadAllText(path + ProtectedFileService.ProtectedOriginalSuffix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Documents/nested/copy.docx")]
    public async Task FreshDownloadPublishesDecryptedCopyAndRetainsProtectedOriginal(string? destinationPath)
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
        using var cache = new AgentSharePointFiles(client, Options.Create(WorkingDirectory), NullLogger<AgentSharePointFiles>.Instance);

        var result = await cache.DownloadAsync("item", "document.docx", default, destinationPath);

        if (destinationPath is not null)
        {
            Assert.Equal(Path.GetFullPath(Path.Combine(_directory, destinationPath)), result.LocalPath);
            await File.WriteAllTextAsync(result.LocalPath, "local edits");
            await Assert.ThrowsAsync<ArgumentException>(() => cache.DownloadAsync("item", "document.docx", default, destinationPath));
            Assert.Equal("local edits", File.ReadAllText(result.LocalPath));
            result = await cache.DownloadAsync("item", "document.docx", default, destinationPath, overwrite: true);

        }

        Assert.Equal("readable", File.ReadAllText(result.LocalPath));
        Assert.Equal("encrypted input", File.ReadAllText(result.LocalPath + ProtectedFileService.ProtectedOriginalSuffix));
        Assert.Equal(8, result.SizeBytes);
        Assert.False(result.AlreadyOnDisk);
        if (destinationPath is null)
        {
            var second = await cache.DownloadAsync("item", "document.docx", default);
            Assert.NotEqual(result.LocalPath, second.LocalPath);
            Assert.False(second.AlreadyOnDisk);
            await service.Received(2).EnsureReadableAsync(Arg.Any<string>(), "document.docx", Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task FailedOverwritePreservesPreviousCopyAndProtection()
    {
        var path = CreateLocalFile();
        File.WriteAllText(path + ProtectedFileService.ProtectedOriginalSuffix, "previous protection");
        var service = Substitute.For<IProtectedFileService>();
        service.EnsureReadableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FileSensitivity>(new UnauthorizedAccessException("EXTRACT denied")));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var http = new HttpClient(new DownloadHandler());
        using var graph = new GraphServiceClient(http);
        memory.Set("SharePointDrive___", new Drive { Id = "drive" });
        var client = new SharePointClient(graph, memory, Options.Create(new SharePointOptions()), service);
        using var cache = new AgentSharePointFiles(client, Options.Create(WorkingDirectory), NullLogger<AgentSharePointFiles>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cache.DownloadAsync("item", "document.docx", default, path, overwrite: true));

        Assert.Equal("encrypted input", File.ReadAllText(path));
        Assert.Equal("previous protection", File.ReadAllText(path + ProtectedFileService.ProtectedOriginalSuffix));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    [Fact]
    public async Task CustomDestinationRejectsEscapesAndDirectoriesBeforeDownload()
    {
        Directory.CreateDirectory(_directory);
        using var cache = CreateTransfers();
        await Assert.ThrowsAsync<ArgumentException>(() => cache.DownloadAsync("item", "document.docx", default, "../outside.docx", true));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.DownloadAsync("item", "document.docx", default, ".", true));
    }

    [Fact]
    public async Task UploadUsesExplicitSourceInsteadOfCachedFile()
    {
        var cached = CreateLocalFile();
        var source = Path.Combine(_directory, "edited.docx");
        await File.WriteAllTextAsync(source, "explicit source content");
        using var handler = new UploadHandler();
        using var http = new HttpClient(handler);
        using var graph = new GraphServiceClient(http);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        memory.Set("SharePointDrive___", new Drive { Id = "drive" });
        var service = Substitute.For<IProtectedFileService>();
        var client = new SharePointClient(graph, memory, Options.Create(new SharePointOptions()), service);
        using var cache = new AgentSharePointFiles(client, Options.Create(WorkingDirectory), NullLogger<AgentSharePointFiles>.Instance);

        await cache.UploadAsync("item", "document.docx", "edited.docx", default);

        Assert.Equal("explicit source content", handler.Content);
        Assert.Equal("encrypted input", await File.ReadAllTextAsync(cached));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.UploadAsync("item", "document.docx", "", default));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.UploadAsync("item", "document.docx", "../outside.docx", default));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.UploadAsync("item", "document.docx", ".", default));
        await Assert.ThrowsAsync<FileNotFoundException>(() => cache.UploadAsync("item", "document.docx", "missing.docx", default));
        await File.WriteAllTextAsync(source + ProtectedFileService.ProtectedOriginalSuffix, "protected");
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.UploadAsync("item", "document.docx", source, default));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class UploadHandler : HttpMessageHandler
    {
        public string? Content { get; private set; }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Put, request.Method);
            Content = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"item\",\"name\":\"document.docx\"}", System.Text.Encoding.UTF8, "application/json")
            };
        }
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
