using Microsoft.Extensions.Options;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace SharePointAgent.Tests;

public sealed class WorkspaceFileManagementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "workspace-files-" + Guid.NewGuid().ToString("N"));

    private readonly AgentFileSystem files;

    public WorkspaceFileManagementTests()
    {
        files = new(Options.Create(new LocalWorkingDirectoryOptions { Directory = root }));
    }

    [Fact]
    public async Task CreatesUploadsCopiesMovesRenamesAndDeletesFilesAndFolders()
    {
        var browser = new WorkspaceAgentFileBrowser(new LocalAgentWorkspaceProvider(files));
        var id = Guid.NewGuid();
        await browser.ManageAsync(id, new("mkdir", "Folder"), default);
        await browser.ManageAsync(id, new("mkdir", "Folder/Nested"), default);
        await browser.ManageAsync(id, new("upload", "Folder/Nested/data.bin", Content: [0, 1, 255]), default);
        await browser.ManageAsync(id, new("copy", "Folder", "Copy"), default);
        Assert.Equal(new byte[] { 0, 1, 255 }, (await browser.ReadAsync(id, "Copy/Nested/data.bin", default)).Content);
        await browser.ManageAsync(id, new("rename", "Copy", "Renamed"), default);
        await browser.ManageAsync(id, new("move", "Renamed", "Folder/Renamed"), default);
        await browser.ManageAsync(id, new("copy", "Folder/Nested/data.bin", "copy.bin"), default);
        await browser.ManageAsync(id, new("rename", "copy.bin", "renamed.bin"), default);
        await browser.ManageAsync(id, new("move", "renamed.bin", "Folder/renamed.bin"), default);
        await browser.ManageAsync(id, new("delete", "Folder/renamed.bin"), default);
        await browser.ManageAsync(id, new("delete", "Folder"), default);
        Assert.Empty(files.List(".", true).Entries);
    }

    [Fact]
    public async Task WorkspaceOperationsCreateEditArchiveAndRejectLocalExecution()
    {
        var id = Guid.NewGuid();
        var chats = Substitute.For<IChatRepository>();
        chats.GetConversationAsync(id, Arg.Any<CancellationToken>()).Returns(
            new ChatConversation(id, "Test", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0));
        var modes = Substitute.For<IChatAgentModeSelector>();
        modes.ResolveAsync(id, Arg.Any<CancellationToken>()).Returns(new ChatAgentModeChoice(ChatAgentExecutionMode.Local, true));
        var provider = Substitute.For<IAgentWorkspaceProvider>();
        provider.GetAsync(id, Arg.Any<CancellationToken>()).Returns((IAgentWorkspace)files);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(chats);
        builder.Services.AddSingleton(modes);
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton(Substitute.For<IFoundrySessionRepository>());
        builder.Services.AddSingleton(Substitute.For<IAgentFileBrowser>());
        builder.Services.Configure<ChatAgentHostingOptions>(_ => { });
        await using var app = builder.Build();
        app.MapWorkspaceFileEndpoints();
        await app.StartAsync();
        using var http = app.GetTestClient();
        var url = $"/api/chat/conversations/{id}/files/operations";
        (await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("text", Path: "test.txt", Content: "before"))).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("text", Path: "test.txt", Content: "after", Overwrite: true))).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("zip", Destination: "files.zip", Paths: ["test.txt"]))).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("unzip", Destination: "expanded", Paths: ["files.zip"]))).EnsureSuccessStatusCode();
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(root, "expanded", "test.txt")));
        var execution = new WorkspaceExecution("python", "print('test')", null, [], ".", 60);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("execute", Execution: execution))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("text", Path: "../escape", Content: "bad"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync($"/api/chat/conversations/{Guid.NewGuid()}/files/operations", new FoundryWorkspaceRequest("text", Path: "test"))).StatusCode);
        var isolated = Substitute.For<IAgentWorkspace>();
        isolated.IsIsolated.Returns(true);
        isolated.ExecuteAsync(Arg.Any<WorkspaceExecution>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceExecutionResult(0, false, "test", ""));
        provider.GetAsync(id, Arg.Any<CancellationToken>()).Returns(isolated);
        var result = await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("execute", Execution: execution));
        result.EnsureSuccessStatusCode();
        Assert.Equal("test", (await result.Content.ReadFromJsonAsync<WorkspaceExecutionResult>())!.Stdout);
        await isolated.Received(1).SaveAsync(Arg.Any<CancellationToken>());
        var invalidExecution = execution with { TimeoutSeconds = 221 };
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(url, new FoundryWorkspaceRequest("execute", Execution: invalidExecution))).StatusCode);
        await isolated.Received(1).ExecuteAsync(Arg.Any<WorkspaceExecution>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("./test")]
    [InlineData("././test")]
    public async Task ManagementAcceptsCurrentDirectoryPrefix(string path)
    {
        var result = await files.ManageAsync(new("mkdir", path), default);
        Assert.Equal("test", result.Path);
        Assert.True(Directory.Exists(Path.Combine(root, "test")));
        await files.ManageAsync(new("copy", path, "./copy"), default);
        Assert.True(Directory.Exists(Path.Combine(root, "copy")));
    }

    [Theory]
    [InlineData("./")]
    [InlineData("./.")]
    [InlineData("./../escape")]
    [InlineData(".//outside")]
    [InlineData("./C:/outside")]
    [InlineData("../escape")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("C:/outside")]
    [InlineData("folder/../../escape")]
    [InlineData("file:stream")]
    [InlineData("folder\\escape")]
    public async Task ManagementRejectsUnsafePaths(string path)
    {
        foreach (var operation in new[] { "mkdir", "upload", "copy", "move", "rename", "delete" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new(operation, path, "target", []), default));
        }
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task RejectsSelfDescendantAndExternalDestinations(string operation)
    {
        await files.ManageAsync(new("mkdir", "folder"), default);
        foreach (var target in new[] { "folder", "folder/child", "../escape" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new(operation, "folder", target), default));
        }
        Assert.Empty(files.List("folder", true).Entries);
    }

    [Fact]
    public async Task ConflictsNeverReplaceExistingContentOrMergeDirectories()
    {
        await files.ManageAsync(new("upload", "source.bin", Content: [1]), default);
        await files.ManageAsync(new("upload", "target.bin", Content: [2]), default);
        foreach (var operation in new[] { "copy", "move", "rename" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new(operation, "source.bin", "target.bin"), default));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new("upload", "target.bin", Content: [3]), default));
        Assert.Equal(new byte[] { 2 }, (await files.ReadAsync("target.bin", default)).Content);
        await files.ManageAsync(new("mkdir", "directory"), default);
        await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new("mkdir", "directory"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new("copy", "source.bin", "directory"), default));
    }

    [Fact]
    public async Task UploadLimitsCancellationAndMissingParentLeaveNoPartialFiles()
    {
        await files.ManageAsync(new("mkdir", "folder"), default);
        await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new("upload", "folder/large", Content: new byte[AgentFileSystem.MaxUploadBytes + 1]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new("upload", "missing/file", Content: []), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.ManageAsync(new("upload", "folder/cancel", Content: [1]), new CancellationToken(true)));
        Assert.Empty(files.List("folder", true).Entries);
        await files.ManageAsync(new("upload", "folder/empty", Content: []), default);
        Assert.Empty((await files.ReadAsync("folder/empty", default)).Content);
    }

    [Fact]
    public async Task RenameCannotChangeParent()
    {
        await files.ManageAsync(new("mkdir", "folder"), default);
        await files.ManageAsync(new("upload", "file", Content: [1]), default);
        await Assert.ThrowsAsync<ArgumentException>(() => files.ManageAsync(new("rename", "file", "folder/file"), default));
    }

    [Theory]
    [InlineData("Global Admin", true, false)]
    [InlineData("User", true, true)]
    [InlineData("Global Reader Admin", false, true)]
    public void MutationsUseConversationOwnershipAndRejectReadOnlyRoles(string role, bool allowed, bool ownership)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], "POST", "/api/chat/conversations/123/files/manage"));
        Assert.Equal(allowed, AppAccess.Allows([role], "POST", "/api/chat/conversations/123/files/operations"));
        Assert.Equal(ownership, AppAccess.RequiresOwnership([role], "POST"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
