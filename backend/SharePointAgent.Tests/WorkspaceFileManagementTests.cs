using Microsoft.Extensions.Options;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

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

    [Theory]
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
