using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// The local workspace: this host's disk, through the same resolver every tool has always used. Code does
/// not run here through the workspace; a local skill runs on the host exactly as it did before workspaces.
/// </summary>
public sealed partial class AgentFileSystem
{
    public bool IsIsolated => false;

    public string Normalize(string path) => Relative(Resolve(path));

    Task<FileSystemListing> IAgentWorkspace.ListAsync(string? path, bool recursive, CancellationToken cancellationToken) =>
        Task.FromResult(List(path, recursive));

    public Task<FileSystemEntry?> FindAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        FileSystemEntry? entry = Directory.Exists(full) ? ToEntry(new DirectoryInfo(full))
            : File.Exists(full) ? ToEntry(new FileInfo(full))
            : null;
        return Task.FromResult(entry);
    }

    public async Task<FileSystemEntry> WriteAsync(string path, Stream content, bool overwrite, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ArgumentException($"'{Relative(full)}' is a directory.");
        }

        if (!overwrite && File.Exists(full))
        {
            throw new ArgumentException($"'{Relative(full)}' already exists. Pass overwrite to replace it, or choose another name.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var staging = $"{full}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await content.CopyToAsync(output, cancellationToken);
            }
            File.Move(staging, full, overwrite);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
        return ToEntry(new FileInfo(full));
    }

    Task<FileSystemEntry> IAgentWorkspace.CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(CreateDirectory(path));

    Task<FileSystemEntry> IAgentWorkspace.MoveAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken) =>
        Task.FromResult(Move(source, destination, overwrite));

    Task<FileSystemEntry> IAgentWorkspace.CopyAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken) =>
        Task.FromResult(Copy(source, destination, overwrite));

    Task IAgentWorkspace.DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        Delete(path, recursive);
        return Task.CompletedTask;
    }

    Task<WorkspaceExecutionResult> IAgentWorkspace.ExecuteAsync(WorkspaceExecution request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The local working directory does not run code; use an isolated workspace mode.");

    Task IAgentWorkspace.SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Every conversation shares this host's one working directory, as it always has: the provider where no
/// isolated mode is configured, and in the Foundry host, whose session is itself the isolated environment.
/// </summary>
public sealed class LocalAgentWorkspaceProvider(AgentFileSystem files) : IAgentWorkspaceProvider
{
    public IReadOnlyList<AgentWorkspaceMode> AvailableModes { get; } = [AgentWorkspaceMode.Local];

    public Task<bool> SetModeAsync(Guid conversationId, AgentWorkspaceMode? mode, CancellationToken cancellationToken) =>
        mode is null or AgentWorkspaceMode.Local
            ? Task.FromResult(true)
            : throw new ArgumentException($"{mode} is not configured on this deployment.");

    public Task<IAgentWorkspace> GetAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IAgentWorkspace>(files);

    public Task<IAgentWorkspace?> FindAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IAgentWorkspace?>(files);

    public Task<AgentWorkspaceEnvironment?> DescribeAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<AgentWorkspaceEnvironment?>(new AgentWorkspaceEnvironment(AgentWorkspaceMode.Local, null));

    // One directory for everybody, so no conversation may wipe it.
    public Task<bool> ResetAsync(Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(false);
}
