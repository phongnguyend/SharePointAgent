using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.Workspaces;

/// <summary>
/// A working directory inside SharePointAgent.SandboxHost, in a dynamic session or a sandbox. The agent's
/// paths are the host's workspace paths, so the agent's picture of a local folder is literally true there.
/// The semantics follow the local workspace: the same limits, "into a directory" for moves and copies, and
/// the same kinds of errors, which the tools already turn into messages the model can act on.
/// <para>
/// Bookkeeping files start with <c>.agent-</c>; they are never listed and cannot be named by the agent.
/// When a snapshot store is supplied, the workspace is restored into a fresh environment before its first
/// use and saved after a change, so a session's cooldown does not lose the agent's files.
/// </para>
/// </summary>
public sealed class SandboxHostWorkspace : IAgentWorkspace
{
    public const string ReservedPrefix = ".agent-";

    private const string ReadyMarker = ".agent-workspace";

    private const string SnapshotFile = ".agent-snapshot.zip";

    private const string RestoreFile = ".agent-restore.zip";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly HttpClient _http;
    private readonly ISandboxEndpoint _endpoint;
    private readonly IWorkspaceSnapshotStore? _snapshots;
    private readonly string _scope;
    private readonly AgentWorkspaceOptions _options;
    private readonly long _maxReadBytes;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ready = new(1, 1);
    private bool _isReady;
    private int _dirty;

    public SandboxHostWorkspace(
        HttpClient http,
        ISandboxEndpoint endpoint,
        IWorkspaceSnapshotStore? snapshots,
        string scope,
        AgentWorkspaceOptions options,
        LocalWorkingDirectoryOptions limits,
        ILogger logger)
    {
        _http = http;
        _endpoint = endpoint;
        _snapshots = snapshots;
        _scope = scope;
        _options = options;
        _maxReadBytes = limits.Downloads.MaxFileBytes;
        _logger = logger;
    }

    public bool IsIsolated => true;

    public string Normalize(string path) => NormalizePath(path);

    public async Task<FileSystemListing> ListAsync(string? path, bool recursive, CancellationToken cancellationToken)
    {
        var listing = await SendAsync<SandboxListing>(HttpMethod.Get, "files",
            Query(("path", NormalizePath(path)), ("recursive", recursive ? "true" : "false")), null, cancellationToken);
        var visible = listing.Entries.Where(entry => !IsReserved(entry.Path)).ToList();
        var entries = visible.Take(AgentFileSystem.MaxEntries).Select(ToEntry).ToArray();
        return new FileSystemListing(listing.Path, entries.Length, listing.Truncated || visible.Count > AgentFileSystem.MaxEntries, entries);
    }

    public async Task<FileSystemEntry?> FindAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        await EnsureReadyAsync(cancellationToken);
        return await DescribeAsync(normalized, cancellationToken);
    }

    public async Task<FileContent> ReadAsync(string path, CancellationToken cancellationToken, long? maxBytes = null)
    {
        var normalized = NormalizePath(path);
        var limit = maxBytes ?? _maxReadBytes;
        await EnsureReadyAsync(cancellationToken);
        using var request = await CreateRequestAsync(HttpMethod.Get, "files/content", Query(("path", normalized)), null, cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentLength > limit)
        {
            throw TooLarge(normalized, response.Content.Headers.ContentLength.Value, limit);
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw TooLarge(normalized, buffer.Length + read, limit);
            }
            buffer.Write(chunk, 0, read);
        }

        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return new FileContent(normalized, name,
            ContentTypes.TryGetContentType(name, out var contentType) ? contentType : "application/octet-stream", buffer.ToArray());
    }

    public async Task<FileSystemEntry> WriteAsync(string path, Stream content, bool overwrite, CancellationToken cancellationToken)
    {
        var entry = await SendAsync<SandboxEntry>(HttpMethod.Put, "files/content",
            Query(("path", RequireFilePath(path)), ("overwrite", overwrite ? "true" : "false")), new StreamContent(content), cancellationToken);
        MarkDirty();
        return ToEntry(entry);
    }

    public async Task<FileSystemEntry> WriteTextAsync(string path, string content, bool overwrite, CancellationToken cancellationToken)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(content) > AgentFileSystem.MaxWriteBytes)
        {
            throw new ArgumentException($"The text exceeds the {AgentFileSystem.MaxWriteBytes / (1024 * 1024)} MB write limit.");
        }

        var entry = await SendAsync<SandboxEntry>(HttpMethod.Put, "files/text", Query(),
            JsonContent.Create(new { path = RequireFilePath(path), content, append = false, overwrite }, options: Json), cancellationToken);
        MarkDirty();
        return ToEntry(entry);
    }

    public async Task<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var entry = await SendAsync<SandboxEntry>(HttpMethod.Post, "files/directories", Query(),
            JsonContent.Create(new { path = RequireFilePath(path) }, options: Json), cancellationToken);
        MarkDirty();
        return ToEntry(entry);
    }

    public Task<FileSystemEntry> MoveAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken) =>
        TransferAsync("files/move", source, destination, overwrite, cancellationToken);

    public Task<FileSystemEntry> CopyAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken) =>
        TransferAsync("files/copy", source, destination, overwrite, cancellationToken);

    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        if (normalized == ".")
        {
            throw new ArgumentException("The working directory itself cannot be deleted.");
        }

        await SendAsync<object?>(HttpMethod.Delete, "files", Query(("path", normalized), ("recursive", recursive ? "true" : "false")), null, cancellationToken);
        MarkDirty();
    }

    public async Task<FileSystemEntry> ZipAsync(IReadOnlyList<string> paths, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (paths is not { Count: > 0 })
        {
            throw new ArgumentException("Name at least one file or directory to zip.");
        }

        var sources = paths.Select(NormalizePath).ToList();
        var entry = await SendAsync<SandboxEntry>(HttpMethod.Post, "files/zip", Query(),
            JsonContent.Create(new { paths = sources, destination = RequireFilePath(destination), overwrite }, options: Json), cancellationToken);
        MarkDirty();
        return ToEntry(entry);
    }

    /// <summary>
    /// Checks the archive here before SandboxHost extracts it: SandboxHost keeps entries inside the
    /// destination, but only this side knows the reserved bookkeeping names an archive must not overwrite.
    /// </summary>
    public async Task<FileSystemEntry> UnzipAsync(string path, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var source = RequireFilePath(path);
        var target = NormalizePath(destination);
        var content = await ReadAsync(source, cancellationToken);
        try
        {
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(content.Content), System.IO.Compression.ZipArchiveMode.Read);
            WorkspaceArchives.Check(archive);
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException($"'{source}' is not a valid zip file.");
        }

        var entry = await SendAsync<SandboxEntry>(HttpMethod.Post, "files/unzip", Query(),
            JsonContent.Create(new { path = source, destination = target, overwrite }, options: Json), cancellationToken);
        MarkDirty();
        return ToEntry(entry);
    }

    /// <summary>The file browser's operations, with the same path rule, size limits, and refusals as the local workspace.</summary>
    public async Task<SandboxFileChangeResult> ManageAsync(SandboxFileChange change, CancellationToken cancellationToken)
    {
        if (change.Operation is not ("mkdir" or "upload" or "rename" or "move" or "copy" or "delete"))
        {
            throw new ArgumentException("Unknown file operation.");
        }

        var source = NormalizePath(AgentFileSystem.ValidateManagedPath(change.Path));
        await EnsureReadyAsync(cancellationToken);
        if (change.Operation is "mkdir" or "upload")
        {
            await EnsureNewDestinationAsync(source, cancellationToken);
            if (change.Operation == "mkdir")
            {
                await CreateDirectoryAsync(source, cancellationToken);
            }
            else
            {
                if (change.Content is null || change.Content.Length > AgentFileSystem.MaxUploadBytes)
                {
                    throw new ArgumentException("Upload a file of 5 MB or smaller.");
                }
                await WriteAsync(source, new MemoryStream(change.Content), overwrite: false, cancellationToken);
            }
            return new SandboxFileChangeResult(source);
        }

        if (await DescribeAsync(source, cancellationToken) is null)
        {
            throw new ArgumentException($"'{source}' does not exist.");
        }

        if (change.Operation == "delete")
        {
            await DeleteAsync(source, recursive: true, cancellationToken);
            return new SandboxFileChangeResult(source);
        }

        var destination = NormalizePath(AgentFileSystem.ValidateManagedPath(change.Destination));
        if (destination == source || destination.StartsWith(source + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Choose a different destination outside the source folder.");
        }

        if (change.Operation == "rename" && Parent(source) != Parent(destination))
        {
            throw new ArgumentException("Rename must keep the item in the same folder. Use Move to change folders.");
        }

        await EnsureNewDestinationAsync(destination, cancellationToken);
        await SendAsync<SandboxEntry>(HttpMethod.Post, change.Operation == "copy" ? "files/copy" : "files/move", Query(),
            JsonContent.Create(new { source, destination, overwrite = false }, options: Json), cancellationToken);
        MarkDirty();
        return new SandboxFileChangeResult(destination);
    }

    public async Task<WorkspaceExecutionResult> ExecuteAsync(WorkspaceExecution request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds + _options.RequestTimeoutSeconds));
        SandboxExecution result;
        try
        {
            result = await SendAsync<SandboxExecution>(HttpMethod.Post, "executions", Query(), JsonContent.Create(new
        {
            language = request.Language,
            code = request.Code,
            scriptPath = request.ScriptPath is null ? null : NormalizePath(request.ScriptPath),
            arguments = request.Arguments,
            workingDirectory = request.WorkingDirectory is null ? null : NormalizePath(request.WorkingDirectory),
            timeoutSeconds = request.TimeoutSeconds
        }, options: Json), timeout.Token, perRequestTimeout: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentWorkspaceUnavailableException("The script did not finish in time.");
        }
        finally
        {
            // Whatever the script did, files may have changed.
            MarkDirty();
        }
        return new WorkspaceExecutionResult(result.ExitCode, result.TimedOut, result.Stdout, result.Stderr);
    }

    /// <summary>
    /// Saves a snapshot when this workspace changed and its environment forgets files. A workspace too large
    /// to snapshot is left as it is and logged; the agent's files stay in the session until its cooldown.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_snapshots is null || Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            await SaveSnapshotAsync(_snapshots, cancellationToken);
        }
        catch
        {
            MarkDirty();
            throw;
        }
    }

    private async Task SaveSnapshotAsync(IWorkspaceSnapshotStore snapshots, CancellationToken cancellationToken)
    {
        var top = await SendAsync<SandboxListing>(HttpMethod.Get, "files", Query(("path", "."), ("recursive", "false")), null, cancellationToken);
        var paths = top.Entries.Select(entry => entry.Path).Where(path => !IsReserved(path)).ToList();
        if (paths.Count == 0)
        {
            await snapshots.DeleteAsync(_scope, cancellationToken);
            return;
        }

        await SendAsync<SandboxEntry>(HttpMethod.Post, "files/zip", Query(),
            JsonContent.Create(new { paths, destination = SnapshotFile, overwrite = true }, options: Json), cancellationToken);
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, "files/content", Query(("path", SnapshotFile)), null, cancellationToken);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var maxBytes = (long)_options.Snapshots.MaxMegabytes * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                _logger.LogWarning("The working directory of {Scope} is {Bytes} bytes zipped, over the snapshot limit; it was not saved.",
                    _scope, response.Content.Headers.ContentLength);
                return;
            }

            await using var archive = await response.Content.ReadAsStreamAsync(cancellationToken);
            await snapshots.SaveAsync(_scope, archive, cancellationToken);
        }
        finally
        {
            await TryDeleteAsync(SnapshotFile, cancellationToken);
        }
    }

    /// <summary>
    /// Makes the environment usable once per workspace object: waits for it, and in a fresh session puts the
    /// last snapshot back before anything else happens, then marks the session as restored.
    /// </summary>
    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_isReady)
        {
            return;
        }

        await _ready.WaitAsync(cancellationToken);
        try
        {
            if (_isReady)
            {
                return;
            }

            await _endpoint.WaitUntilReadyAsync(_http, cancellationToken);
            if (_snapshots is not null && await DescribeAsync(ReadyMarker, cancellationToken) is null)
            {
                await RestoreAsync(_snapshots, cancellationToken);
                await SendCoreAsync<SandboxEntry>(HttpMethod.Put, "files/text", Query(),
                    JsonContent.Create(new { path = ReadyMarker, content = _scope, append = false, overwrite = true }, options: Json), cancellationToken);
            }
            _isReady = true;
        }
        finally
        {
            _ready.Release();
        }
    }

    private async Task RestoreAsync(IWorkspaceSnapshotStore snapshots, CancellationToken cancellationToken)
    {
        await using var archive = await snapshots.OpenAsync(_scope, cancellationToken);
        if (archive is null)
        {
            return;
        }

        await SendCoreAsync<SandboxEntry>(HttpMethod.Put, "files/content", Query(("path", RestoreFile), ("overwrite", "true")),
            new StreamContent(archive), cancellationToken);
        try
        {
            await SendCoreAsync<SandboxEntry>(HttpMethod.Post, "files/unzip", Query(),
                JsonContent.Create(new { path = RestoreFile, destination = ".", overwrite = true }, options: Json), cancellationToken);
            _logger.LogInformation("Restored the working directory of {Scope} into {Environment}.", _scope, _endpoint.Description);
        }
        finally
        {
            await TryDeleteAsync(RestoreFile, cancellationToken);
        }
    }

    private async Task<FileSystemEntry> TransferAsync(string operation, string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var from = NormalizePath(source);
        var to = NormalizePath(destination);
        await EnsureReadyAsync(cancellationToken);

        // A directory as the destination means "into it", as on the local workspace.
        if (to != from && await DescribeAsync(to, cancellationToken) is { IsDirectory: true })
        {
            to = to == "." ? Name(from) : $"{to}/{Name(from)}";
        }

        if (to == from)
        {
            throw new ArgumentException("The source and the destination are the same file.");
        }

        var entry = await SendAsync<SandboxEntry>(HttpMethod.Post, operation, Query(),
            JsonContent.Create(new { source = from, destination = to, overwrite }, options: Json), cancellationToken);
        MarkDirty();
        return ToEntry(entry);
    }

    private async Task EnsureNewDestinationAsync(string path, CancellationToken cancellationToken)
    {
        if (await DescribeAsync(path, cancellationToken) is not null)
        {
            throw new ArgumentException("The destination already exists. Choose another name.");
        }

        var parent = Parent(path);
        if (parent != "." && await DescribeAsync(parent, cancellationToken) is not { IsDirectory: true })
        {
            throw new ArgumentException("The destination folder does not exist. Create it first.");
        }
    }

    private async Task<FileSystemEntry?> DescribeAsync(string normalized, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "files/info", Query(("path", normalized)), null, cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return ToEntry((await response.Content.ReadFromJsonAsync<SandboxEntry>(Json, cancellationToken))!);
    }

    private async Task TryDeleteAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Delete, "files", Query(("path", path)), null, cancellationToken);
            using var _ = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, Dictionary<string, string?> query, HttpContent? content, CancellationToken cancellationToken, bool perRequestTimeout = true)
    {
        await EnsureReadyAsync(cancellationToken);
        return await SendCoreAsync<T>(method, path, query, content, cancellationToken, perRequestTimeout);
    }

    private async Task<T> SendCoreAsync<T>(
        HttpMethod method, string path, Dictionary<string, string?> query, HttpContent? content, CancellationToken cancellationToken, bool perRequestTimeout = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (perRequestTimeout)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        }

        try
        {
            using var request = await CreateRequestAsync(method, path, query, content, timeout.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            await EnsureSuccessAsync(response, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
            {
                return default!;
            }
            return (await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token))!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentWorkspaceUnavailableException("The working directory did not respond in time. Try again.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Could not reach the working directory in {Environment}.", _endpoint.Description);
            throw new AgentWorkspaceUnavailableException("The working directory could not be reached. Try again shortly.", exception);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method, string path, Dictionary<string, string?> query, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, _endpoint.BuildUri(path, query)) { Content = content };
        await _endpoint.AuthorizeAsync(request, cancellationToken);
        return request;
    }

    /// <summary>
    /// Turns SandboxHost errors into the exceptions the local workspace throws for the same situations, so
    /// tools report them to the model in the same way. Anything unexpected becomes "unavailable" without
    /// passing the environment's own details on.
    /// </summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await ReadErrorAsync(response, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.BadRequest:
            case HttpStatusCode.NotFound:
            case HttpStatusCode.RequestEntityTooLarge:
                throw new ArgumentException(message ?? "The working directory refused the request.");
            case HttpStatusCode.Conflict:
                throw new IOException(message ?? "The working directory could not complete the request.");
            case HttpStatusCode.Forbidden:
                throw new UnauthorizedAccessException(message ?? "Access was denied in the working directory.");
            default:
                _logger.LogWarning("{Environment} answered {StatusCode}.", _endpoint.Description, (int)response.StatusCode);
                throw new AgentWorkspaceUnavailableException("The working directory is unavailable. Try again shortly.");
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A path relative to the top of the working directory, with forward slashes. Absolute paths, '..', and
    /// the reserved bookkeeping names are refused before anything is sent.
    /// </summary>
    internal static string NormalizePath(string? path)
    {
        var trimmed = (path ?? "").Trim().Replace('\\', '/');
        if (trimmed.StartsWith('/') || (trimmed.Length > 1 && trimmed[1] == ':'))
        {
            throw new ArgumentException($"'{path}' is outside the agent's working directory. Use a path relative to it.");
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToList();
        if (parts.Contains(".."))
        {
            throw new ArgumentException($"'{path}' is outside the agent's working directory. Use a path relative to it.");
        }

        if (parts.Any(part => part.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Names starting with '{ReservedPrefix}' are reserved in the working directory.");
        }
        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    private static string RequireFilePath(string path)
    {
        var normalized = NormalizePath(path);
        return normalized == "." ? throw new ArgumentException("A file or directory name is required.") : normalized;
    }

    private static bool IsReserved(string path) =>
        path.Split('/').Any(part => part.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase));

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static ArgumentException TooLarge(string path, long size, long limit) =>
        new($"'{path}' is {size / (1024 * 1024)} MB, over the {limit / (1024 * 1024)} MB read limit.");

    private static Dictionary<string, string?> Query(params (string Key, string? Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value);

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    private static FileSystemEntry ToEntry(SandboxEntry entry) => new(entry.Path, entry.IsDirectory, entry.Size, entry.LastModified);

    private sealed record SandboxEntry(string Path, string Name, bool IsDirectory, long? Size, DateTimeOffset LastModified);

    private sealed record SandboxListing(string Path, IReadOnlyList<SandboxEntry> Entries, bool Truncated);

    private sealed record SandboxExecution(string Language, int? ExitCode, bool TimedOut, string Stdout, string Stderr);
}
