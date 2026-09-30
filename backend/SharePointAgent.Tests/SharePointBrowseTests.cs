using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using NSubstitute;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SharePointBrowseTests
{
    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("folder\\file")]
    [InlineData("file.")]
    [InlineData(" name")]
    [InlineData("name ")]
    [InlineData("~$document")]
    [InlineData("bad:name")]
    public void RejectsInvalidNames(string name)
    {
        Assert.Throws<ArgumentException>(() => SharePointClient.ValidateBrowseName(name));
    }

    [Theory]
    [InlineData("Global Admin", "GET", true)]
    [InlineData("Global Admin", "PUT", true)]
    [InlineData("Global Reader Admin", "GET", true)]
    [InlineData("Global Reader Admin", "POST", false)]
    [InlineData("Global Reader Admin", "DELETE", false)]
    [InlineData("User", "GET", false)]
    [InlineData("User", "PATCH", false)]
    public void BrowseUsesAdministrativeAccess(string role, string method, bool allowed)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], method, "/api/browse/item"));
    }

    [Fact]
    public async Task ListsAllPagesAndBuildsBreadcrumbs()
    {
        using var fixture = new Fixture((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(Json(path switch
            {
                "/v1.0/drives/drive/items/folder" => """{"id":"folder","name":"Team","folder":{},"parentReference":{"id":"root"}}""",
                "/v1.0/drives/drive/items/root" => Root,
                "/v1.0/drives/drive/items/folder/children" => """{"value":[{"id":"file","name":"A.txt","file":{}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/next"}""",
                "/v1.0/next" => """{"value":[{"id":"sub","name":"Z","folder":{}}]}""",
                _ => throw new InvalidOperationException(path)
            }));
        });
        var listing = await fixture.Client.BrowseAsync("folder", default);
        Assert.Equal(["root", "folder"], listing.Breadcrumbs.Select(item => item.Id));
        Assert.Equal(["sub", "file"], listing.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransferRejectsDescendantDestination(bool copy)
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.EndsWith("/source")
                ? """{"id":"source","name":"Team","folder":{},"parentReference":{"id":"root"}}"""
                : """{"id":"child","name":"Child","folder":{},"parentReference":{"id":"source"}}""")));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.TransferBrowseItemAsync("source", "child", "Team", "tag", copy, default));
    }

    [Fact]
    public async Task RootCannotBeDeleted()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Root)));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.DeleteBrowseItemAsync("root", "tag", default));
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task WritesUseExpectedMethodDestinationAndConcurrency(string operation)
    {
        var mutations = 0;
        using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(request.RequestUri!.AbsolutePath.EndsWith("/root") ? Root : Source);
            }
            mutations++;
            if (operation != "copy")
            {
                Assert.Equal("tag", request.Headers.GetValues("If-Match").Single());
            }
            if (operation == "delete")
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("New.txt", body.RootElement.GetProperty("name").GetString());
            if (operation is "move" or "copy")
            {
                Assert.Equal("root", body.RootElement.GetProperty("parentReference").GetProperty("id").GetString());
            }
            Assert.Equal(operation == "copy" ? HttpMethod.Post : HttpMethod.Patch, request.Method);
            return operation == "copy" ? new HttpResponseMessage(HttpStatusCode.Accepted) : Json(Source);
        });
        switch (operation)
        {
            case "rename":
                await fixture.Client.RenameBrowseItemAsync("source", "New.txt", "tag", default);
                break;
            case "delete":
                await fixture.Client.DeleteBrowseItemAsync("source", "tag", default);
                break;
            default:
                await fixture.Client.TransferBrowseItemAsync("source", "root", "New.txt", "tag", operation == "copy", default);
                break;
        }
        Assert.Equal(1, mutations);
    }

    [Fact]
    public async Task UploadEncodesNamesAndRejectsReplacement()
    {
        using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(Root);
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Contains("conflictBehavior=fail", request.RequestUri!.Query);
            Assert.Contains("report%20%231.txt", request.RequestUri.AbsoluteUri);
            Assert.Equal("hello", await request.Content!.ReadAsStringAsync(ct));
            return Json(Source);
        });
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        await fixture.Client.UploadBrowseFileAsync("root", "report #1.txt", content, default);
    }

    [Fact]
    public async Task LargeUploadUsesSessionWithFailConflictPolicy()
    {
        var length = 5 * 1024 * 1024;
        var uploaded = 0L;
        using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(Root);
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/createUploadSession", request.RequestUri!.AbsolutePath);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("fail", body.RootElement.GetProperty("item").GetProperty("@microsoft.graph.conflictBehavior").GetString());
                return Json("""{"uploadUrl":"https://upload.example.test/session","nextExpectedRanges":["0-"]}""");
            }
            Assert.Equal("upload.example.test", request.RequestUri!.Host);
            Assert.Equal(uploaded, request.Content!.Headers.ContentRange!.From);
            uploaded += (await request.Content.ReadAsByteArrayAsync(ct)).Length;
            return uploaded == length ? Json(Source) : Json($$"""{"nextExpectedRanges":["{{uploaded}}-"]}""");
        });
        using var content = new MemoryStream(new byte[length]);
        await fixture.Client.UploadBrowseFileAsync("root", "large.bin", content, default);
        Assert.Equal(length, uploaded);
    }

    [Fact]
    public async Task DuplicateUploadSurfacesConflict()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Json(Root)
            : new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"error":{"code":"nameAlreadyExists","message":"Exists"}}""", Encoding.UTF8, "application/json")
            }));
        using var content = new MemoryStream([1]);
        var error = await Assert.ThrowsAsync<Microsoft.Graph.Models.ODataErrors.ODataError>(() =>
            fixture.Client.UploadBrowseFileAsync("root", "exists.txt", content, default));
        Assert.Equal(409, error.ResponseStatusCode);
    }

    [Fact]
    public async Task FolderCreationFailsOnConflict()
    {
        using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(Root);
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("fail", body.RootElement.GetProperty("@microsoft.graph.conflictBehavior").GetString());
            Assert.Equal("Team", body.RootElement.GetProperty("name").GetString());
            Assert.True(body.RootElement.TryGetProperty("folder", out _));
            return Json("""{"id":"new","name":"Team","folder":{}}""");
        });
        Assert.True((await fixture.Client.CreateBrowseFolderAsync("root", "Team", default)).IsFolder);
    }

    [Fact]
    public async Task RecycleBinReadsBetaPagesAndSortsNewestFirst()
    {
        var calls = 0;
        using var fixture = new Fixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/beta/sites/site/recycleBin/items", request.RequestUri!.AbsolutePath);
            calls++;
            return Task.FromResult(Json(calls == 1
                ? """{"value":[{"id":"old","name":"Old.txt","size":10,"deletedDateTime":"2026-01-01T00:00:00Z","deletedFromLocation":"Documents/Team"}],"@odata.nextLink":"https://graph.microsoft.com/beta/sites/site/recycleBin/items?$skiptoken=next"}"""
                : """{"value":[{"id":"new","name":"New.txt","deletedDateTime":"2026-02-01T00:00:00Z","deletedFromLocation":"Other Library"}]}"""));
        });
        var listing = await fixture.Client.BrowseRecycleBinAsync(default);
        Assert.Equal(2, calls);
        Assert.Equal(["new", "old"], listing.Items.Select(item => item.Id));
        Assert.Equal("Documents/Team", listing.Items[1].DeletedFromLocation);
        Assert.Equal("Example", listing.SiteName);
        Assert.Equal("https://example.sharepoint.com/sites/team/_layouts/15/RecycleBin.aspx", listing.RecycleBinUrl);
    }

    [Theory]
    [InlineData("https://attacker.example/beta/sites/site/recycleBin/items")]
    [InlineData("https://graph.microsoft.com/beta/sites/another/recycleBin/items")]
    [InlineData("http://graph.microsoft.com/beta/sites/site/recycleBin/items")]
    [InlineData("https://graph.microsoft.com/beta/sites/site/recycleBin/items?$top=200")]
    public async Task RecycleBinRejectsUnsafeOrRepeatedContinuation(string next)
    {
        var calls = 0;
        using var fixture = new Fixture((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["value"] = Array.Empty<object>(),
                ["@odata.nextLink"] = next
            })));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.BrowseRecycleBinAsync(default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":[{\"name\":\"missing id\"}]}")]
    [InlineData("not json")]
    [InlineData("{\"value\":[null]}")]
    public async Task RecycleBinDoesNotTreatMalformedResponsesAsEmpty(string response)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(response)));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.BrowseRecycleBinAsync(default));
    }

    [Fact]
    public async Task RecycleBinEmptyResponseIsValid()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("""{"value":[]}""")));
        Assert.Empty((await fixture.Client.BrowseRecycleBinAsync(default)).Items);
    }

    [Fact]
    public async Task RecycleBinPreservesPermissionFailure()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"accessDenied","message":"Denied"}}""", Encoding.UTF8, "application/json")
        }));
        var error = await Assert.ThrowsAsync<Microsoft.Graph.Models.ODataErrors.ODataError>(() => fixture.Client.BrowseRecycleBinAsync(default));
        Assert.Equal(403, error.ResponseStatusCode);
    }

    [Theory]
    [InlineData("Global Admin", true)]
    [InlineData("Global Reader Admin", true)]
    [InlineData("User", false)]
    public void RecycleBinRequiresAdministrativeReadAccess(string role, bool allowed)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], "GET", "/api/browse/recycle-bin"));
    }

    private const string Root = """{"id":"root","name":"Documents","folder":{},"root":{}}""";

    private const string Source = """{"id":"source","name":"Old.txt","file":{},"eTag":"tag","parentReference":{"id":"root"}}""";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryCache cache = new(new MemoryCacheOptions());

        private readonly HttpClient http;

        private readonly GraphServiceClient graph;

        public SharePointClient Client { get; }

        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            http = new HttpClient(new Handler(send));
            graph = new GraphServiceClient(http);
            cache.Set("SharePointDrive___", new Drive { Id = "drive" });
            cache.Set("SharePointSite__", new Site { Id = "site", DisplayName = "Example", WebUrl = "https://example.sharepoint.com/sites/team" });
            Client = new SharePointClient(graph, cache, Options.Create(new SharePointOptions()), Substitute.For<IProtectedFileService>());
        }

        public void Dispose()
        {
            graph.Dispose();
            http.Dispose();
            cache.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
