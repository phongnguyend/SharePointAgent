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
        var provisioner = Substitute.For<ISandboxProvisioner>();
        provisioner.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SandboxBinding("sbx", new Uri("https://sandbox.example/"), "key"));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var provider = new IsolatedAgentWorkspaceProvider(http, factory,
            Options.Create(new AgentWorkspaceOptions { Mode = AgentWorkspaceMode.Sandboxes }), Options.Create(new LocalWorkingDirectoryOptions()),
            NullLogger<SandboxHostWorkspace>.Instance, sandboxes: provisioner);

        await provider.GetAsync(inWorkspace, default);
        await provider.GetAsync(alone, default);

        await provisioner.Received(1).AcquireAsync(workspaceId.ToString("N"), Arg.Any<CancellationToken>());
        await provisioner.Received(1).AcquireAsync(alone.ToString("N"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BrowsingAConversationThatNeverRanCreatesNothing()
    {
        var bindings = Substitute.For<ISandboxRegistry>();
        var provisioner = Substitute.For<ISandboxProvisioner>();
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite("Data Source=:memory:").Options;
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var db = new SharePointIndexDbContext(options);
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            return db;
        });
        IAgentWorkspaceProvider Provider(AgentWorkspaceMode mode) => new IsolatedAgentWorkspaceProvider(
            Substitute.For<IHttpClientFactory>(), factory, Options.Create(new AgentWorkspaceOptions { Mode = mode }),
            Options.Create(new LocalWorkingDirectoryOptions()), NullLogger<SandboxHostWorkspace>.Instance,
            Substitute.For<TokenCredential>(), _snapshots, provisioner, bindings);
        var browser = new WorkspaceAgentFileBrowser(Provider(AgentWorkspaceMode.Sandboxes));

        var listing = await browser.ListAsync(Guid.NewGuid(), ".", recursive: false, default);

        Assert.False(listing.SandboxStarted);
        Assert.Null(await Provider(AgentWorkspaceMode.DynamicSessions).FindAsync(Guid.NewGuid(), default));
        await provisioner.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default);
    }

    [Fact]
    public async Task ZippingAndUnzippingWorkInsideTheIsolatedWorkspace()
    {
        var workspace = Workspace();
        await workspace.WriteTextAsync("report/summary.md", "# Summary", overwrite: false, default);

        var archive = await workspace.ZipAsync(["report"], "report.zip", overwrite: false, default);
        var extracted = await workspace.UnzipAsync("report.zip", "restored", overwrite: false, default);

        Assert.Equal("report.zip", archive.Path);
        Assert.Equal("restored", extracted.Path);
        Assert.Equal("# Summary", await File.ReadAllTextAsync(Path.Combine(_sandboxRoot, "restored", "report", "summary.md")));
    }

    [Theory]
    [InlineData(".agent-workspace")]
    [InlineData("nested/.agent-restore.zip")]
    [InlineData("../escaped.txt")]
    public async Task AnArchiveThatWouldTouchBookkeepingOrEscapeIsRefusedBeforeExtraction(string entryName)
    {
        var workspace = Workspace();
        using (var buffer = new MemoryStream())
        {
            using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
                writer.Write("bad");
            }
            buffer.Position = 0;
            await workspace.WriteAsync("bad.zip", buffer, overwrite: false, default);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.UnzipAsync("bad.zip", "out", overwrite: false, default));

        Assert.False(Directory.Exists(Path.Combine(_sandboxRoot, "out")));
    }

    [Fact]
    public async Task ADynamicSessionIdIsRecordedOnTheScopeRowApartFromTheScope()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var provider = DynamicSessionsProvider(database.Factory, new RecordingHandler());

        await provider.GetAsync(database.InWorkspace, default);
        await provider.GetAsync(database.InWorkspace, default);

        await using var db = database.Open();
        var recorded = (await db.ChatWorkspaces.SingleAsync()).DynamicSessionId;
        Assert.False(string.IsNullOrEmpty(recorded));
        Assert.NotEqual(database.WorkspaceId.ToString("N"), recorded);
        Assert.Null((await db.ChatWorkspaces.SingleAsync()).FoundrySessionId);
        Assert.Equal(new AgentWorkspaceEnvironment(AgentWorkspaceMode.DynamicSessions, recorded),
            await provider.DescribeAsync(database.InWorkspace, default));
    }

    [Fact]
    public async Task ASandboxIdIsRecordedOnTheScopeRowWhenItIsAcquired()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var provisioner = Substitute.For<ISandboxProvisioner>();
        provisioner.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SandboxBinding("sbx-1", new Uri("https://sandbox.example/"), "key"));
        var provider = SandboxesProvider(database.Factory, provisioner);

        Assert.Equal(new AgentWorkspaceEnvironment(AgentWorkspaceMode.Sandboxes, null), await provider.DescribeAsync(database.Alone, default));
        await provider.GetAsync(database.Alone, default);

        await using var db = database.Open();
        Assert.Equal("sbx-1", (await db.ChatConversations.SingleAsync(c => c.Id == database.Alone)).SandboxId);
        Assert.Equal(new AgentWorkspaceEnvironment(AgentWorkspaceMode.Sandboxes, "sbx-1"), await provider.DescribeAsync(database.Alone, default));
    }

    [Fact]
    public async Task ResettingADynamicSessionDropsItsFilesStopsItAndStartsANewOne()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var handler = new RecordingHandler();
        var provider = DynamicSessionsProvider(database.Factory, handler);
        await provider.GetAsync(database.Alone, default);
        var first = (await provider.DescribeAsync(database.Alone, default))!.EnvironmentId!;
        await _snapshots.SaveAsync(database.Alone.ToString("N"), new MemoryStream([1, 2, 3]), default);

        Assert.True(await provider.ResetAsync(database.Alone, default));

        Assert.False(_snapshots.Has(database.Alone.ToString("N")));
        Assert.Null((await provider.DescribeAsync(database.Alone, default))!.EnvironmentId);
        var stop = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, stop.Method);
        Assert.Equal($"https://pool.example/.management/stopSession?api-version=2025-02-02-preview&identifier={first}", stop.RequestUri!.AbsoluteUri);

        await provider.GetAsync(database.Alone, default);
        Assert.NotEqual(first, (await provider.DescribeAsync(database.Alone, default))!.EnvironmentId);
    }

    [Fact]
    public async Task ResettingASandboxReleasesItForTheWholeWorkspace()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var provisioner = Substitute.For<ISandboxProvisioner>();
        provisioner.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SandboxBinding("sbx-1", new Uri("https://sandbox.example/"), "key"));
        var provider = SandboxesProvider(database.Factory, provisioner);
        await provider.GetAsync(database.InWorkspace, default);

        Assert.True(await provider.ResetAsync(database.InWorkspace, default));

        await provisioner.Received(1).ReleaseAsync(database.WorkspaceId.ToString("N"), Arg.Any<CancellationToken>());
        await using var db = database.Open();
        Assert.Null((await db.ChatWorkspaces.SingleAsync()).SandboxId);
    }

    [Fact]
    public async Task ASharedDevelopmentSandboxOrALocalWorkspaceCannotBeReset()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var provisioner = Substitute.For<ISandboxProvisioner>();
        var shared = new IsolatedAgentWorkspaceProvider(Substitute.For<IHttpClientFactory>(), database.Factory,
            Options.Create(new AgentWorkspaceOptions
            {
                Mode = AgentWorkspaceMode.Sandboxes,
                Sandboxes = new SandboxesWorkspaceOptions { SharedEndpoint = "https://shared.example/", SharedApiKey = "key" }
            }),
            Options.Create(new LocalWorkingDirectoryOptions()), NullLogger<SandboxHostWorkspace>.Instance, sandboxes: provisioner);
        var local = new LocalAgentWorkspaceProvider(null!);

        Assert.False(await shared.ResetAsync(database.Alone, default));
        Assert.False(await local.ResetAsync(database.Alone, default));
        Assert.Equal(new AgentWorkspaceEnvironment(AgentWorkspaceMode.Local, null), await local.DescribeAsync(database.Alone, default));
        await provisioner.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default);
    }

    [Fact]
    public async Task AScopeCanChooseLocalWhileTheDefaultIsASandbox()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var provisioner = SandboxProvisioner();
        var local = LocalDirectory();
        var provider = SandboxesProvider(database.Factory, provisioner, local);

        Assert.True(await provider.SetModeAsync(database.Alone, AgentWorkspaceMode.Local, default));

        Assert.Same(local, await provider.GetAsync(database.Alone, default));
        Assert.Equal(new AgentWorkspaceEnvironment(AgentWorkspaceMode.Local, null, IsDefault: false), await provider.DescribeAsync(database.Alone, default));
        Assert.False(await provider.ResetAsync(database.Alone, default));
        await provisioner.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default);
    }

    [Fact]
    public async Task AWorkspacesModeIsSharedByItsConversationsAndSwitchingBackKeepsItsSandbox()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var provisioner = SandboxProvisioner();
        var provider = SandboxesProvider(database.Factory, provisioner, LocalDirectory());
        await provider.GetAsync(database.InWorkspace, default);

        await provider.SetModeAsync(database.InWorkspace, AgentWorkspaceMode.Local, default);
        await using (var db = database.Open())
        {
            var workspace = await db.ChatWorkspaces.SingleAsync();
            Assert.Equal(AgentWorkspaceMode.Local, workspace.WorkspaceMode);
            Assert.Equal("sbx-1", workspace.SandboxId);
            Assert.Null((await db.ChatConversations.SingleAsync(c => c.Id == database.InWorkspace)).WorkspaceMode);
        }

        await provider.SetModeAsync(database.InWorkspace, null, default);
        Assert.Equal(new AgentWorkspaceEnvironment(AgentWorkspaceMode.Sandboxes, "sbx-1"), await provider.DescribeAsync(database.InWorkspace, default));
        await provisioner.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default);
    }

    [Fact]
    public async Task OnlyConfiguredModesCanBeChosen()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var provider = SandboxesProvider(database.Factory, SandboxProvisioner(), local: null);

        Assert.Equal([AgentWorkspaceMode.Sandboxes], provider.AvailableModes);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetModeAsync(database.Alone, AgentWorkspaceMode.DynamicSessions, default));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetModeAsync(database.Alone, AgentWorkspaceMode.Local, default));
        Assert.False(await provider.SetModeAsync(Guid.NewGuid(), null, default));
    }

    [Fact]
    public async Task AnIsolatedModeCanBeChosenWhenTheDefaultIsLocal()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var credential = Substitute.For<TokenCredential>();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new RecordingHandler(), disposeHandler: false));
        var local = LocalDirectory();
        var provider = new IsolatedAgentWorkspaceProvider(http, database.Factory,
            Options.Create(new AgentWorkspaceOptions
            {
                Mode = AgentWorkspaceMode.Local,
                DynamicSessions = new DynamicSessionsWorkspaceOptions { PoolManagementEndpoint = "https://pool.example/" }
            }),
            Options.Create(new LocalWorkingDirectoryOptions()), NullLogger<SandboxHostWorkspace>.Instance, credential, _snapshots, local: local);

        Assert.Equal([AgentWorkspaceMode.Local, AgentWorkspaceMode.DynamicSessions], provider.AvailableModes);
        Assert.Same(local, await provider.GetAsync(database.Alone, default));

        await provider.SetModeAsync(database.Alone, AgentWorkspaceMode.DynamicSessions, default);

        Assert.True((await provider.GetAsync(database.Alone, default)).IsIsolated);
        Assert.False((await provider.DescribeAsync(database.Alone, default))!.IsDefault);
    }

    private static ISandboxProvisioner SandboxProvisioner()
    {
        var provisioner = Substitute.For<ISandboxProvisioner>();
        provisioner.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SandboxBinding("sbx-1", new Uri("https://sandbox.example/"), "key"));
        return provisioner;
    }

    private AgentFileSystem LocalDirectory() =>
        new(Options.Create(new LocalWorkingDirectoryOptions { Directory = Path.Combine(_sandboxRoot, "local") }));

    private IsolatedAgentWorkspaceProvider DynamicSessionsProvider(IDbContextFactory<SharePointIndexDbContext> factory, RecordingHandler handler)
    {
        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("session-token", DateTimeOffset.UtcNow.AddHours(1)));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new IsolatedAgentWorkspaceProvider(http, factory,
            Options.Create(new AgentWorkspaceOptions
            {
                Mode = AgentWorkspaceMode.DynamicSessions,
                DynamicSessions = new DynamicSessionsWorkspaceOptions { PoolManagementEndpoint = "https://pool.example/" }
            }),
            Options.Create(new LocalWorkingDirectoryOptions()), NullLogger<SandboxHostWorkspace>.Instance, credential, _snapshots);
    }

    private static IsolatedAgentWorkspaceProvider SandboxesProvider(
        IDbContextFactory<SharePointIndexDbContext> factory, ISandboxProvisioner provisioner, AgentFileSystem? local = null)
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return new IsolatedAgentWorkspaceProvider(http, factory,
            Options.Create(new AgentWorkspaceOptions { Mode = AgentWorkspaceMode.Sandboxes }),
            Options.Create(new LocalWorkingDirectoryOptions()), NullLogger<SandboxHostWorkspace>.Instance, sandboxes: provisioner, local: local);
    }

    /// <summary>A workspace with one conversation, and one conversation outside any workspace, in SQLite.</summary>
    private sealed class ChatDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<SharePointIndexDbContext> _options;

        private ChatDatabase(SqliteConnection connection)
        {
            _connection = connection;
            _options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
            Factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
            Factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Open());
        }

        public IDbContextFactory<SharePointIndexDbContext> Factory { get; }

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public Guid InWorkspace { get; } = Guid.NewGuid();

        public Guid Alone { get; } = Guid.NewGuid();

        public static async Task<ChatDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
            var database = new ChatDatabase(connection);
            await using var db = database.Open();
            await db.Database.EnsureCreatedAsync();
            db.ChatWorkspaces.Add(new ChatWorkspaceEntity { Id = database.WorkspaceId, Name = "Team" });
            db.ChatConversations.Add(new ChatConversationEntity { Id = database.InWorkspace, Title = "a", WorkspaceId = database.WorkspaceId });
            db.ChatConversations.Add(new ChatConversationEntity { Id = database.Alone, Title = "b" });
            await db.SaveChangesAsync();
            return database;
        }

        public SharePointIndexDbContext Open() => new(_options);

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    /// <summary>Records requests, such as stopping a session, and answers each with 200.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class InMemorySnapshots : IWorkspaceSnapshotStore
    {
        private readonly Dictionary<string, byte[]> _archives = [];

        public int Saves { get; private set; }

        public bool Has(string scope) => _archives.ContainsKey(scope);

        public Task<bool> ExistsAsync(string scope, CancellationToken cancellationToken) => Task.FromResult(_archives.ContainsKey(scope));

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
