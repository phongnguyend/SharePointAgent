using System.ComponentModel.DataAnnotations;
using SharePointAgent.Domain;

namespace SharePointAgent.Application;

/// <summary>
/// The agent's working directory, wherever it actually is. The agent always sees one local-looking tree
/// with paths relative to its top, such as <c>Downloads/SharePoint/…/report.docx</c>; whether that tree is
/// this host's disk, a dynamic session, or a sandbox is decided here and nowhere else, so no tool, prompt,
/// or path has to change between them.
/// </summary>
public interface IAgentWorkspace
{
    /// <summary>
    /// True when the files live in an isolated environment rather than on this host. Code, such as a skill
    /// script, runs there too; on a local workspace it runs on this host as it always has.
    /// </summary>
    bool IsIsolated { get; }

    /// <summary>The path as the agent should see and quote it: relative to the top, with forward slashes.</summary>
    string Normalize(string path);

    Task<FileSystemListing> ListAsync(string? path, bool recursive, CancellationToken cancellationToken);

    /// <summary>The entry at a path, or null when nothing is there. A path outside the workspace is an error.</summary>
    Task<FileSystemEntry?> FindAsync(string path, CancellationToken cancellationToken);

    /// <summary>A file's bytes. <paramref name="maxBytes"/> overrides the default download-sized read limit.</summary>
    Task<FileContent> ReadAsync(string path, CancellationToken cancellationToken, long? maxBytes = null);

    /// <summary>Writes a file from a stream, creating parent directories; a failed write leaves no partial file.</summary>
    Task<FileSystemEntry> WriteAsync(string path, Stream content, bool overwrite, CancellationToken cancellationToken);

    Task<FileSystemEntry> WriteTextAsync(string path, string content, bool overwrite, CancellationToken cancellationToken);

    Task<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>A directory as the destination means "into it", as shell commands do.</summary>
    Task<FileSystemEntry> MoveAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken);

    Task<FileSystemEntry> CopyAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken);

    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    /// <summary>The file browser's operations, with its stricter path and size rules.</summary>
    Task<SandboxFileChangeResult> ManageAsync(SandboxFileChange change, CancellationToken cancellationToken);

    /// <summary>Runs code inside an isolated workspace. Only valid when <see cref="IsIsolated"/> is true.</summary>
    Task<WorkspaceExecutionResult> ExecuteAsync(WorkspaceExecution request, CancellationToken cancellationToken);

    /// <summary>
    /// Makes this turn's changes outlive the environment. A no-op where the files already persist; for a
    /// dynamic session, whose files vanish after its cooldown, it saves a snapshot to restore next time.
    /// </summary>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Finds the working directory a conversation uses: its workspace's when it is in one, else its own.</summary>
public interface IAgentWorkspaceProvider
{
    Task<IAgentWorkspace> GetAsync(Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>Inline code or a workspace script, run with the workspace as its default working directory.</summary>
public sealed record WorkspaceExecution(
    string Language,
    string? Code,
    string? ScriptPath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int TimeoutSeconds);

public sealed record WorkspaceExecutionResult(int? ExitCode, bool TimedOut, string Stdout, string Stderr);

/// <summary>Raised when the isolated environment cannot be reached or refused a request it should accept.</summary>
public sealed class AgentWorkspaceUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public enum AgentWorkspaceMode
{
    /// <summary>This host's disk, shared by every conversation. In Foundry, the hosted session's own disk.</summary>
    Local,

    /// <summary>A custom-container session per workspace or conversation in an Azure Container Apps session pool.</summary>
    DynamicSessions,

    /// <summary>An Azure Container Apps sandbox per workspace or conversation, reached on its exposed port.</summary>
    Sandboxes
}

/// <summary>
/// Where the agent's working directory lives. <see cref="AgentWorkspaceMode.Local"/> is the default and
/// changes nothing. The isolated modes run SharePointAgent.SandboxHost and keep this application's
/// credentials out of it: this host downloads, converts, and uploads, and only moves bytes in and out.
/// </summary>
public sealed class AgentWorkspaceOptions
{
    public const string SectionName = "AgentWorkspace";

    public AgentWorkspaceMode Mode { get; set; } = AgentWorkspaceMode.Local;

    /// <summary>Per request to the isolated environment; executions get their own timeout on top.</summary>
    [Range(5, 600)] public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>How long a skill script may run inside the isolated environment. A session pool caps this at about 220.</summary>
    [Range(1, 1800)] public int ScriptTimeoutSeconds { get; set; } = 60;

    public DynamicSessionsWorkspaceOptions DynamicSessions { get; set; } = new();

    public SandboxesWorkspaceOptions Sandboxes { get; set; } = new();

    public WorkspaceSnapshotOptions Snapshots { get; set; } = new();
}

public sealed class DynamicSessionsWorkspaceOptions
{
    /// <summary>The pool's management endpoint (the <c>dynamicSessionsPoolEndpoint</c> output of the template).</summary>
    public string? PoolManagementEndpoint { get; set; }

    /// <summary>The managed identity holding the Session Executor role; empty uses AZURE_CLIENT_ID or the system identity.</summary>
    public string? ManagedIdentityClientId { get; set; }

    public bool IsConfigured => Uri.TryCreate(PoolManagementEndpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

public sealed class SandboxesWorkspaceOptions
{
    /// <summary>
    /// A sandbox every conversation shares, for development only: like <see cref="AgentWorkspaceMode.Local"/>,
    /// it is one directory for everybody. Production binds a sandbox per workspace (see the binding registry).
    /// </summary>
    public string? SharedEndpoint { get; set; }

    public string? SharedApiKey { get; set; }

    /// <summary>How long to wait for a sandbox to answer its health check after it is created or resumed.</summary>
    [Range(1, 600)] public int HealthWaitSeconds { get; set; } = 60;
}

/// <summary>
/// Snapshots keep a dynamic session's files across its cooldown: the workspace is zipped to Blob Storage
/// after each turn and unzipped into a fresh session before it is used. Sandboxes keep their own disk.
/// </summary>
public sealed class WorkspaceSnapshotOptions
{
    public bool UsedManagedIdentity { get; set; } = true;

    public string? ServiceUri { get; set; }

    public string? ConnectionString { get; set; }

    [Required] public string ContainerName { get; set; } = "agent-workspaces";

    /// <summary>A workspace larger than this, zipped, is not snapshotted, and the next fresh session starts without it.</summary>
    [Range(1, 2048)] public int MaxMegabytes { get; set; } = 200;

    public bool IsConfigured => UsedManagedIdentity
        ? Uri.TryCreate(ServiceUri, UriKind.Absolute, out _)
        : !string.IsNullOrWhiteSpace(ConnectionString);
}
