namespace SharePointAgent.Domain;

/// <summary>One entry of the agent's working directory, at a path relative to the top of it.</summary>
public sealed record FileSystemEntry(string Path, bool IsDirectory, long? SizeBytes, DateTimeOffset ModifiedUtc);

/// <summary>
/// One directory of the agent's working directory, as a reader sees it.
/// <para>
/// <see cref="SandboxStarted"/> is false when the conversation has no sandbox yet, which in Foundry is
/// every conversation before its first turn. The entries are then empty because there is nothing to
/// list, not because the directory is empty — and listing must never start a sandbox to find out, as
/// that would bind one to a conversation that has not run anything.
/// </para>
/// </summary>
public sealed record FileSystemListing(
    string Path,
    int Count,
    bool Truncated,
    IReadOnlyList<FileSystemEntry> Entries,
    bool SandboxStarted = true);

/// <summary>
/// One file out of the agent's working directory. <see cref="ContentType"/> is worked out from the
/// name by the process holding the file, so the reader does not have to guess it.
/// </summary>
public sealed record FileContent(string Path, string Name, string ContentType, byte[] Content);
