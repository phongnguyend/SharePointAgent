namespace SharePointAgent.SandboxHost;

/// <summary>A file or directory, with its path relative to the workspace and forward slashes.</summary>
public sealed record FileEntry(string Path, string Name, bool IsDirectory, long? Size, DateTimeOffset LastModified);

public sealed record FileListing(string Path, IReadOnlyList<FileEntry> Entries, bool Truncated);

/// <summary>A range of a text file. <c>StartLine</c> is one-based; <c>TotalLines</c> counts the whole file.</summary>
public sealed record TextFileContent(string Path, string Content, int StartLine, int LineCount, int TotalLines, bool Truncated);

public sealed record WriteTextRequest(string Path, string Content, bool Append = false, bool Overwrite = true);

/// <summary>Replaces exact text. Without <c>ReplaceAll</c>, the old text must occur exactly once.</summary>
public sealed record EditTextRequest(string Path, string OldText, string NewText, bool ReplaceAll = false);

public sealed record EditTextResult(string Path, int Replacements);

public sealed record CreateDirectoryRequest(string Path);

public sealed record TransferRequest(string Source, string Destination, bool Overwrite = false);

/// <summary>Searches file contents. <c>Glob</c> filters by file name, such as <c>*.py</c>.</summary>
public sealed record SearchRequest(
    string Query,
    string? Path = null,
    string? Glob = null,
    bool IsRegex = false,
    bool CaseSensitive = false,
    int? MaxResults = null);

public sealed record SearchMatch(string Path, int Line, string Text);

public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, bool Truncated);

public sealed record ZipRequest(IReadOnlyList<string> Paths, string Destination, bool Overwrite = false);

public sealed record UnzipRequest(string Path, string Destination, bool Overwrite = false);

/// <summary>
/// Runs either inline <c>Code</c> or a workspace <c>ScriptPath</c>. <c>Language</c> may be omitted for a
/// script path with a known extension.
/// </summary>
public sealed record ExecutionRequest(
    string? Language = null,
    string? Code = null,
    string? ScriptPath = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    string? Stdin = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int? TimeoutSeconds = null);

/// <summary>A finished run. A non-zero exit is still a 200: the script ran, and failed. <c>ExitCode</c> is null on timeout.</summary>
public sealed record ExecutionResult(
    string Language,
    int? ExitCode,
    bool TimedOut,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    long DurationMs);

public sealed record RuntimeInfo(string Language, string Executable, bool Available, string? Version);
