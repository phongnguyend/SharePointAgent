using System.Net.Http.Json;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.Workspaces;

public sealed class FoundrySessionWorkspace(FoundryWorkspacePlayground client, string sessionId) : IAgentWorkspace
{
    public bool IsIsolated => true;

    public string Normalize(string path) => SandboxHostWorkspace.NormalizePath(path);

    private Task<T> Invoke<T>(FoundryWorkspaceRequest request, CancellationToken ct) => client.InvokeAsync<T>(sessionId, request, ct);

    public Task<FileSystemListing> ListAsync(string? path, bool recursive, CancellationToken ct) => Invoke<FileSystemListing>(new("list", Path: path, Recursive: recursive), ct);

    public Task<FileSystemEntry?> FindAsync(string path, CancellationToken ct) => Invoke<FileSystemEntry?>(new("find", Path: path), ct);

    public async Task<FileContent> ReadAsync(string path, CancellationToken ct, long? maxBytes = null)
    {
        var file = await Invoke<FileContent>(new("read", Path: path), ct);
        if (maxBytes is { } limit && file.Content.LongLength > limit)
        {
            throw new ArgumentException("The file exceeds the requested read limit.");
        }
        return file;
    }

    public async Task<FileSystemEntry> WriteAsync(string path, Stream content, bool overwrite, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > 5 * 1024 * 1024)
            {
                throw new ArgumentException("Uploads are limited to 5 MB.");
            }
            buffer.Write(bytes, 0, read);
        }
        return await Invoke<FileSystemEntry>(new("write", Path: path, Content: Convert.ToBase64String(buffer.ToArray()), Overwrite: overwrite), ct);
    }

    public Task<FileSystemEntry> WriteTextAsync(string path, string content, bool overwrite, CancellationToken ct) => Invoke<FileSystemEntry>(new("text", Path: path, Content: content, Overwrite: overwrite), ct);

    public Task<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken ct) => Invoke<FileSystemEntry>(new("mkdir", Path: path), ct);

    public Task<FileSystemEntry> MoveAsync(string source, string destination, bool overwrite, CancellationToken ct) => Invoke<FileSystemEntry>(new("move", Path: source, Destination: destination, Overwrite: overwrite), ct);

    public Task<FileSystemEntry> CopyAsync(string source, string destination, bool overwrite, CancellationToken ct) => Invoke<FileSystemEntry>(new("copy", Path: source, Destination: destination, Overwrite: overwrite), ct);

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct) => await Invoke<object>(new("delete", Path: path, Recursive: recursive), ct);

    public Task<FileSystemEntry> ZipAsync(IReadOnlyList<string> paths, string destination, bool overwrite, CancellationToken ct) => Invoke<FileSystemEntry>(new("zip", Paths: paths, Destination: destination, Overwrite: overwrite), ct);

    public Task<FileSystemEntry> UnzipAsync(string path, string destination, bool overwrite, CancellationToken ct) => Invoke<FileSystemEntry>(new("unzip", Path: path, Destination: destination, Overwrite: overwrite), ct);

    public Task<WorkspaceFileChangeResult> ManageAsync(WorkspaceFileChange change, CancellationToken ct) => Invoke<WorkspaceFileChangeResult>(new("manage", Change: change), ct);

    public Task<WorkspaceExecutionResult> ExecuteAsync(WorkspaceExecution request, CancellationToken ct) => Invoke<WorkspaceExecutionResult>(new("execute", Execution: request), ct);

    public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
}
