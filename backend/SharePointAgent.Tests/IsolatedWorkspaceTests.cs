using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.Workspaces;
using SharePointAgent.Persistence;
using SharePointAgent.SandboxHost;
using Xunit;

namespace SharePointAgent.Tests;

/// <summary>
/// The isolated working directory against the real SandboxHost, hosted in process: the agent's tools see
/// the same paths, limits, and errors as on the local disk, and a fresh session gets its files back.
/// </summary>
public sealed class IsolatedWorkspaceTests : IAsyncLifetime
{
    private const string ApiKey = "test-key";

    private readonly string _sandboxRoot = Path.Combine(Path.GetTempPath(), "isolated-workspace-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySnapshots _snapshots = new();
    private WebApplication _host = null!;

    public async Task InitializeAsync()
    {
        _host = await StartHostAsync(_sandboxRoot);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_sandboxRoot))
        {
            Directory.Delete(_sandboxRoot, recursive: true);
        }
    }

    private static async Task<WebApplication> StartHostAsync(string root)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:WorkspaceRoot"] = root,
            ["Sandbox:ApiKey"] = ApiKey
        });
        builder.AddSandboxHost();
        var app = builder.Build();
        app.MapSandboxHost();
        await app.StartAsync();
        return app;
    }

    private SandboxHostWorkspace Workspace(WebApplication? host = null, IWorkspaceSnapshotStore? snapshots = null, string apiKey = ApiKey) => new(
        (host ?? _host).GetTestClient(),
        new SandboxEndpoint(new Uri("http://localhost/"), apiKey, TimeSpan.FromSeconds(5)),
        snapshots,
        "scope-1",
        new AgentWorkspaceOptions(),
        new LocalWorkingDirectoryOptions(),
        NullLogger.Instance);

    [Fact]
    public async Task ToolsSeeRelativePathsAndLocalSemantics()
    {
        var workspace = Workspace();

        var written = await workspace.WriteTextAsync("Notes/plan.md", "# Plan\nStep one", overwrite: false, default);
        await workspace.CreateDirectoryAsync("Archive", default);
        var moved = await workspace.MoveAsync("Notes/plan.md", "Archive", overwrite: false, default);
        var copied = await workspace.CopyAsync("Archive/plan.md", "Notes/copy.md", overwrite: false, default);

        Assert.Equal("Notes/plan.md", written.Path);
        Assert.Equal("Archive/plan.md", moved.Path);
        Assert.Equal("Notes/copy.md", copied.Path);
        Assert.Null(await workspace.FindAsync("Notes/plan.md", default));
        Assert.Equal("# Plan\nStep one", System.Text.Encoding.UTF8.GetString((await workspace.ReadAsync("./Archive\\plan.md", default)).Content));
        Assert.Equal(["Archive", "Archive/plan.md", "Notes", "Notes/copy.md"],
            (await workspace.ListAsync(".", recursive: true, default)).Entries.Select(entry => entry.Path).Order().ToArray());
        await Assert.ThrowsAsync<IOException>(() => workspace.WriteTextAsync("Notes/copy.md", "again", overwrite: false, default));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("Notes/../../outside.txt")]
    [InlineData(".agent-snapshot.zip")]
    public async Task PathsOutsideTheWorkingDirectoryOrReservedAreRefusedBeforeSending(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Workspace().ReadAsync(path, default));
    }

    [Fact]
    public async Task ReadsAreBoundedLikeTheLocalWorkspace()
    {
        var workspace = Workspace();
        await workspace.WriteTextAsync("big.txt", new string('x', 2048), overwrite: false, default);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => workspace.ReadAsync("big.txt", default, maxBytes: 1024));

        Assert.Contains("read limit", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.ReadAsync("missing.txt", default));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.DeleteAsync(".", recursive: true, default));
    }

    [Fact]
    public async Task TheFileBrowserKeepsItsStricterRules()
    {
        var workspace = Workspace();
        await workspace.ManageAsync(new SandboxFileChange("mkdir", "Reports"), default);
        await workspace.ManageAsync(new SandboxFileChange("upload", "Reports/a.txt", Content: "a"u8.ToArray()), default);

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.ManageAsync(new SandboxFileChange("mkdir", "Reports"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.ManageAsync(new SandboxFileChange("upload", "Missing/a.txt", Content: "a"u8.ToArray()), default));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.ManageAsync(new SandboxFileChange("upload", "Reports/big.bin", Content: new byte[AgentFileSystem.MaxUploadBytes + 1]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.ManageAsync(new SandboxFileChange("rename", "Reports/a.txt", "b.txt"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.ManageAsync(new SandboxFileChange("move", "Reports", "Reports/inner"), default));
        Assert.Equal("Reports/b.txt", (await workspace.ManageAsync(new SandboxFileChange("rename", "Reports/a.txt", "Reports/b.txt"), default)).Path);
        Assert.Equal("Reports", (await workspace.ManageAsync(new SandboxFileChange("delete", "Reports"), default)).Path);
        Assert.Null(await workspace.FindAsync("Reports", default));
    }

    [Fact]
    public async Task AFreshSessionGetsTheLastSnapshotBack()
    {
        var first = Workspace(snapshots: _snapshots);
        await first.WriteTextAsync("Downloads/report.md", "kept", overwrite: false, default);
        await first.SaveAsync(default);

        // The cooldown ended: a new session starts from an empty disk.
        var freshRoot = Path.Combine(Path.GetTempPath(), "isolated-fresh-" + Guid.NewGuid().ToString("N"));
        await using var fresh = await StartHostAsync(freshRoot);
        try
        {
            var second = Workspace(fresh, _snapshots);

            var listing = await second.ListAsync(".", recursive: true, default);

            Assert.Equal("kept", System.Text.Encoding.UTF8.GetString((await second.ReadAsync("Downloads/report.md", default)).Content));
            Assert.DoesNotContain(listing.Entries, entry => entry.Path.Contains(".agent-"));
            Assert.True(File.Exists(Path.Combine(freshRoot, ".agent-workspace")));
        }
        finally
        {
            Directory.Delete(freshRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotsAreSavedOnlyAfterAChange()
    {
        var workspace = Workspace(snapshots: _snapshots);
        await workspace.ListAsync(".", recursive: false, default);
        await workspace.SaveAsync(default);
        Assert.Equal(0, _snapshots.Saves);

        await workspace.WriteTextAsync("a.txt", "a", overwrite: false, default);
        await workspace.SaveAsync(default);
        await workspace.SaveAsync(default);
        Assert.Equal(1, _snapshots.Saves);

        await workspace.DeleteAsync("a.txt", recursive: false, default);
        await workspace.SaveAsync(default);
        Assert.False(_snapshots.Has("scope-1"));
    }

    [Fact]
    public async Task SkillScriptsRunInsideTheWorkspaceAgainstItsFiles()
    {
        var workspace = Workspace();
        await workspace.WriteTextAsync("data.txt", "from the workspace", overwrite: false, default);

        var result = await workspace.ExecuteAsync(new WorkspaceExecution(
            "python", "import sys\nprint(open('data.txt').read() + ' ' + sys.argv[1])", null, ["ok"], null, 30), default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("from the workspace ok", result.Stdout.Trim());
    }

    [Fact]
    public async Task AWrongKeyMakesTheWorkspaceUnavailableWithoutLeakingDetails()
    {
        var error = await Assert.ThrowsAsync<AgentWorkspaceUnavailableException>(() => Workspace(apiKey: "wrong").ListAsync(".", recursive: false, default));

        Assert.DoesNotContain(ApiKey, error.Message);
    }

    [Fact]
    public async Task UploadsOfDecryptedCopiesStayBlockedInIsolatedWorkspaces()
    {
        var workspace = Workspace();
        await workspace.WriteTextAsync("doc.docx", "decrypted", overwrite: false, default);
        await workspace.WriteTextAsync("doc.docx" + ProtectedFileService.ProtectedOriginalSuffix, "protected", overwrite: false, default);
        using var transfers = new AgentSharePointFiles(null!, Options.Create(new LocalWorkingDirectoryOptions()), NullLogger<AgentSharePointFiles>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.UploadAsync(workspace, "item", "doc.docx", "doc.docx", default));

        Assert.Contains("Upload is blocked", error.Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => transfers.UploadAsync(workspace, "item", "doc.docx", "missing.docx", default));
    }

    [Fact]
    public async Task IsolatedSkillsReportFailuresAndRunInTheWorkspace()
    {
        var workspace = Substitute.For<IAgentWorkspace>();
        workspace.ExecuteAsync(Arg.Any<WorkspaceExecution>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceExecutionResult(0, false, " resolved \n", ""), new WorkspaceExecutionResult(2, false, "", "boom"));
        var script = Path.Combine(_sandboxRoot, "probe.py");
        Directory.CreateDirectory(_sandboxRoot);
        await File.WriteAllTextAsync(script, "print('x')");

        var output = await ChatAgentSkills.RunInWorkspaceAsync(workspace, script, System.Text.Json.JsonDocument.Parse("[\"a\", 2]").RootElement, 30, default);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChatAgentSkills.RunInWorkspaceAsync(workspace, script, null, 30, default));

        Assert.Equal("resolved", output);
        Assert.Contains("boom", failure.Message);
        await workspace.Received().ExecuteAsync(
            Arg.Is<WorkspaceExecution>(run => run.Language == "python" && run.Code == "print('x')" && run.Arguments.SequenceEqual(new[] { "a", "2" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DynamicSessionsAreAddressedByIdentifierWithAnEntraToken()
    {
        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("session-token", DateTimeOffset.UtcNow.AddHours(1)));
        var endpoint = new DynamicSessionEndpoint(new Uri("https://pool.example/"), "scope-1", credential);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.BuildUri("files/info", new Dictionary<string, string?> { ["path"] = "a b.txt" }));

        await endpoint.AuthorizeAsync(request, default);

        Assert.Equal("https://pool.example/files/info?path=a%20b.txt&identifier=scope-1", request.RequestUri!.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "session-token"), request.Headers.Authorization);
        await credential.Received().GetTokenAsync(
            Arg.Is<TokenRequestContext>(context => context.Scopes.Single() == "https://dynamicsessions.io/.default"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AWorkspacesConversationsShareOneScopeAndOthersGetTheirOwn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var workspaceId = Guid.NewGuid();
        var inWorkspace = Guid.NewGuid();
        var alone = Guid.NewGuid();
        await using (var db = new SharePointIndexDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ChatWorkspaces.Add(new ChatWorkspaceEntity { Id = workspaceId, Name = "Team" });
            db.ChatConversations.Add(new ChatConversationEntity { Id = inWorkspace, Title = "a", WorkspaceId = workspaceId });
            db.ChatConversations.Add(new ChatConversationEntity { Id = alone, Title = "b" });
            await db.SaveChangesAsync();
        }

        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        var registry = Substitute.For<ISandboxRegistry>();
        registry.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SandboxBinding(new Uri("https://sandbox.example/"), "key"));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var provider = new IsolatedAgentWorkspaceProvider(http, factory,
            Options.Create(new AgentWorkspaceOptions { Mode = AgentWorkspaceMode.Sandboxes }), Options.Create(new LocalWorkingDirectoryOptions()),
            NullLogger<SandboxHostWorkspace>.Instance, sandboxes: registry);

        await provider.GetAsync(inWorkspace, default);
        await provider.GetAsync(alone, default);

        await registry.Received(1).GetAsync(workspaceId.ToString("N"), Arg.Any<CancellationToken>());
        await registry.Received(1).GetAsync(alone.ToString("N"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASandboxThatIsNotSetUpIsReportedPlainly()
    {
        var registry = Substitute.For<ISandboxRegistry>();
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var db = new SharePointIndexDbContext(options);
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            return db;
        });
        var provider = new IsolatedAgentWorkspaceProvider(Substitute.For<IHttpClientFactory>(), factory,
            Options.Create(new AgentWorkspaceOptions { Mode = AgentWorkspaceMode.Sandboxes }), Options.Create(new LocalWorkingDirectoryOptions()),
            NullLogger<SandboxHostWorkspace>.Instance, sandboxes: registry);

        var error = await Assert.ThrowsAsync<AgentWorkspaceUnavailableException>(() => provider.GetAsync(Guid.NewGuid(), default));

        Assert.Contains("No sandbox is set up", error.Message);
    }

    private sealed class InMemorySnapshots : IWorkspaceSnapshotStore
    {
        private readonly Dictionary<string, byte[]> _archives = [];

        public int Saves { get; private set; }

        public bool Has(string scope) => _archives.ContainsKey(scope);

        public Task<Stream?> OpenAsync(string scope, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(_archives.TryGetValue(scope, out var archive) ? new MemoryStream(archive) : null);

        public async Task SaveAsync(string scope, Stream archive, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await archive.CopyToAsync(copy, cancellationToken);
            _archives[scope] = copy.ToArray();
            Saves++;
        }

        public Task DeleteAsync(string scope, CancellationToken cancellationToken)
        {
            _archives.Remove(scope);
            return Task.CompletedTask;
        }
    }
}
