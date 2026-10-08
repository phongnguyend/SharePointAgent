namespace SharePointAgent.SandboxHost;

/// <summary>
/// Settings under <c>Sandbox</c>. The same image runs in a Dynamic Sessions pool and in a Container Apps
/// sandbox, so the defaults are the safe ones and each deployment relaxes what its platform covers: a
/// session pool authenticates callers with Entra and forwards each request through ingress with a
/// 240-second limit, so it sets <c>RequireApiKey=false</c> and <c>MaxTimeoutSeconds=220</c>. A sandbox
/// port has only an IP allow list in front of it, so it keeps the key requirement.
/// </summary>
public sealed class SandboxHostOptions
{
    public const string SectionName = "Sandbox";

    /// <summary>
    /// The directory every file operation and script works in. The image sets <c>/workspace</c>; blank
    /// uses a folder under the temp directory, which is what a local <c>dotnet run</c> gets.
    /// </summary>
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>When set, every route except <c>/health</c> requires it in the <c>X-Api-Key</c> header.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Refuse to start without <see cref="ApiKey"/>. On by default so a container started without a
    /// key fails instead of serving an open port; turn it off only where something else authenticates
    /// callers, such as a session pool, or for local development.
    /// </summary>
    public bool RequireApiKey { get; set; } = true;

    /// <summary>The largest single file upload, text write, or edit.</summary>
    public long MaxFileBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>The most uncompressed bytes one unzip may write, so an archive cannot fill the disk.</summary>
    public long MaxExtractBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>The most characters a text read returns; ask for a line range to read past it.</summary>
    public int MaxTextReadChars { get; set; } = 1024 * 1024;

    public int MaxListEntries { get; set; } = 5000;

    public int MaxSearchResults { get; set; } = 1000;

    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// The longest a single execution may run. Behind a session pool, keep it under the 240-second
    /// Container Apps ingress timeout the request travels through.
    /// </summary>
    public int MaxTimeoutSeconds { get; set; } = 1800;

    /// <summary>The most characters kept from each of stdout and stderr; the rest is drained and dropped.</summary>
    public int MaxOutputChars { get; set; } = 1024 * 1024;

    /// <summary>Per-language executable overrides, keyed by <c>powershell</c>, <c>python</c>, <c>node</c>, or <c>bash</c>.</summary>
    public Dictionary<string, string> Executables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ResolvedWorkspaceRoot => string.IsNullOrWhiteSpace(WorkspaceRoot)
        ? Path.Combine(Path.GetTempPath(), "sharepointagent-sandbox")
        : Path.GetFullPath(WorkspaceRoot);
}
