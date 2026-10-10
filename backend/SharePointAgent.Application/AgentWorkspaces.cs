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

    /// <summary>
    /// Packs files and directories into a zip whose entry names are relative to the top of the working
    /// directory. The archive is written whole or not at all, and it cannot include itself.
    /// </summary>
    Task<FileSystemEntry> ZipAsync(IReadOnlyList<string> paths, string destination, bool overwrite, CancellationToken cancellationToken);

    /// <summary>
    /// Extracts a zip into a directory of the working directory. Entries that would land outside it, or on a
    /// reserved name, refuse the whole archive, as does one that expands past the extract limit.
    /// </summary>
    Task<FileSystemEntry> UnzipAsync(string path, string destination, bool overwrite, CancellationToken cancellationToken);

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

/// <summary>
/// Finds the working directory a conversation uses: its workspace's when it is in one, else its own. Where
/// it lives is the scope's chosen <see cref="AgentWorkspaceMode"/>, or the configured default.
/// </summary>
public interface IAgentWorkspaceProvider
{
    /// <summary>The modes a scope may choose: Local, plus each isolated mode this deployment has configured.</summary>
    IReadOnlyList<AgentWorkspaceMode> AvailableModes { get; }

    /// <summary>The conversation's working directory, created or resumed as needed.</summary>
    Task<IAgentWorkspace> GetAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// The working directory only if it already exists, for browsing: looking at a conversation that has not
    /// run anything must never start a session or create a sandbox for it.
    /// </summary>
    Task<IAgentWorkspace?> FindAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// The isolated environment serving the conversation, for inspection; null for a local workspace.
    /// Reading it starts nothing.
    /// </summary>
    Task<AgentWorkspaceEnvironment?> DescribeAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Discards the isolated environment and its files, so the next turn starts a fresh one. Returns false
    /// where there is nothing of its own to reset: a local workspace or a shared development sandbox.
    /// </summary>
    Task<bool> ResetAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Chooses where the conversation's scope keeps its files from now on; null returns it to the default.
    /// The previous environment is left as it is, so choosing it again finds its files. Returns false when
    /// there is no such conversation; a mode that is not available is an <see cref="ArgumentException"/>.
    /// </summary>
    Task<bool> SetModeAsync(Guid conversationId, AgentWorkspaceMode? mode, CancellationToken cancellationToken);
}

/// <summary>
/// Where a conversation's workspace scope keeps its files. <see cref="EnvironmentId"/> names the dynamic
/// session or sandbox, and is null for Local, until the first turn creates one, and for a shared development
/// sandbox. <see cref="IsDefault"/> is true when the scope has not chosen a mode of its own.
/// </summary>
public sealed record AgentWorkspaceEnvironment(AgentWorkspaceMode Mode, string? EnvironmentId, bool IsDefault = true);

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
    /// <summary>This host's disk, shared by every conversation that uses it. In Foundry, the hosted session's own disk.</summary>
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

    /// <summary>
    /// The default for a workspace or conversation that has not chosen a mode. Every isolated mode that is
    /// configured below can be chosen as well, whatever the default is.
    /// </summary>
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

/// <summary>
/// Sandboxes are created on demand, one per chat workspace (or per conversation outside one), from the
/// SharePointAgent.SandboxHost disk image registered in the sandbox group. Each gets its own random API key
/// and auto-suspends when idle; the next turn resumes it with its disk intact.
/// </summary>
public sealed class SandboxesWorkspaceOptions
{
    /// <summary>The Sandboxes data plane. The SDK's global endpoint unless a regional one is required.</summary>
    [Required] public string DataPlaneEndpoint { get; set; } = "https://management.azuredevcompute.io";

    /// <summary>The data-plane API version whose request shapes this client sends.</summary>
    [Required] public string ApiVersion { get; set; } = "2026-02-01-preview";

    public string? SubscriptionId { get; set; }

    public string? ResourceGroup { get; set; }

    /// <summary>The sandbox group the sandboxes are created in (the <c>sandboxGroupName</c> template output).</summary>
    public string? SandboxGroup { get; set; }

    /// <summary>The ID of the SandboxHost disk image registered in the group.</summary>
    public string? DiskImageId { get; set; }

    /// <summary>The managed identity holding SandboxGroup Data Owner; empty uses AZURE_CLIENT_ID or the system identity.</summary>
    public string? ManagedIdentityClientId { get; set; }

    [Required] public string Cpu { get; set; } = "1000m";

    [Required] public string Memory { get; set; } = "2048Mi";

    /// <summary>Idle seconds before a sandbox suspends. Memory mode keeps the SandboxHost process running across resume.</summary>
    [Range(60, 86400)] public int AutoSuspendSeconds { get; set; } = 900;

    [Required] public string AutoSuspendMode { get; set; } = "Memory";

    /// <summary>Delete a sandbox this many days after it stopped; 0 keeps it until its workspace's next turn.</summary>
    [Range(0, 3650)] public int AutoDeleteAfterDays { get; set; }

    /// <summary>
    /// The SandboxHost port is reachable from these source ranges only, such as the API's outbound addresses.
    /// Empty leaves the port open to any source, protected by the per-sandbox key alone.
    /// </summary>
    public List<string> AllowedSourceCidrs { get; set; } = [];

    /// <summary>Overrides the image entrypoint, for a disk image that does not start SandboxHost itself.</summary>
    public List<string> Entrypoint { get; set; } = [];

    /// <summary>How long creating or resuming a sandbox may take before the turn reports it unavailable.</summary>
    [Range(10, 1800)] public int ProvisionTimeoutSeconds { get; set; } = 300;

    /// <summary>How long to wait for SandboxHost to answer its health check once the sandbox is running.</summary>
    [Range(1, 600)] public int HealthWaitSeconds { get; set; } = 60;

    /// <summary>
    /// Development only: one already-running sandbox every conversation shares, instead of creating one per
    /// workspace. Like <see cref="AgentWorkspaceMode.Local"/>, it is one directory for everybody.
    /// </summary>
    public string? SharedEndpoint { get; set; }

    public string? SharedApiKey { get; set; }

    public bool UsesSharedSandbox => !string.IsNullOrWhiteSpace(SharedEndpoint);

    public bool IsConfigured => UsesSharedSandbox
        ? !string.IsNullOrWhiteSpace(SharedApiKey)
        : new[] { SubscriptionId, ResourceGroup, SandboxGroup, DiskImageId }.All(value => !string.IsNullOrWhiteSpace(value))
          && Uri.TryCreate(DataPlaneEndpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps;
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
