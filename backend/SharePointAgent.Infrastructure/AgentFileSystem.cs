using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// The agent's view of the local disk: the download directory and nothing else. Every tool path is
/// resolved through <see cref="Resolve"/>, which is the single place that decides whether a path is
/// inside the sandbox, so no tool can reach the host's own files by being handed <c>..</c>, an
/// absolute path, or a symbolic link pointing outward.
/// <para>
/// The root is the same directory the SharePoint download cache writes into, so a file the agent
/// downloads and a file it writes itself are in one tree it can list, and in Foundry that tree is the
/// per-session sandbox shared by a workspace's conversations.
/// </para>
/// </summary>
public sealed partial class AgentFileSystem(IOptions<LocalWorkingDirectoryOptions> options) : IAgentWorkspace
{
    /// <summary>The most entries one listing returns, so a large tree cannot fill the context window.</summary>
    public const int MaxEntries = 500;

    /// <summary>The largest text a single write may create. Binary files come from the download tools.</summary>
    public const int MaxWriteBytes = 5 * 1024 * 1024;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly string _root = options.Value.ResolvedDirectory;

    /// <summary>
    /// The largest file that can be read back out, which is the same limit downloads move under: a
    /// reader should be able to fetch anything the agent was able to fetch.
    /// </summary>
    private readonly int _maxReadBytes = options.Value.Downloads.MaxFileBytes;

    public string Root => _root;

    /// <summary>
    /// The absolute path a tool argument refers to, or a thrown <see cref="ArgumentException"/> when it
    /// is not inside the sandbox. A relative path is taken from the root; an absolute path is accepted
    /// only when it is already under it, which is what lets the download tools' returned paths be
    /// passed straight back in.
    /// </summary>
    public string Resolve(string? path, bool mustExist = false)
    {
        var trimmed = path?.Trim() ?? "";
        var combined = trimmed.Length == 0 || trimmed is "." or "./"
            ? _root
            : System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(trimmed)
                ? trimmed
                : System.IO.Path.Combine(_root, trimmed));

        if (!IsInsideRoot(combined))
        {
            throw new ArgumentException(
                $"'{trimmed}' is outside the agent's working directory. Use a path relative to it.");
        }

        // A link inside the sandbox can still point out of it, and the checks above only see the name.
        for (var entry = FirstExisting(combined); entry is not null; entry = Parent(entry))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Symbolic links are not supported inside the working directory.");
            }

            if (PathEquals(entry.FullName, _root))
            {
                break;
            }
        }

        if (mustExist && !File.Exists(combined) && !Directory.Exists(combined))
        {
            throw new ArgumentException($"'{Relative(combined)}' does not exist.");
        }

        return combined;
    }

    /// <summary>The path as the agent should see and quote it: relative to the root, with forward slashes.</summary>
    public string Relative(string fullPath) =>
        PathEquals(fullPath, _root)
            ? "."
            : System.IO.Path.GetRelativePath(_root, fullPath).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public FileSystemListing List(string? path, bool recursive)
    {
        Directory.CreateDirectory(_root);
        var directory = Resolve(path);
        if (File.Exists(directory))
        {
            var single = new FileInfo(directory);
            return new(Relative(directory), 1, false, [ToEntry(single)]);
        }

        if (!Directory.Exists(directory))
        {
            throw new ArgumentException($"'{Relative(directory)}' does not exist.");
        }

        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var found = new DirectoryInfo(directory)
            .EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = search == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Take(MaxEntries + 1)
            .ToList();
        var truncated = found.Count > MaxEntries;
        var entries = found.Take(MaxEntries)
            .Select(ToEntry)
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(Relative(directory), entries.Length, truncated, entries);
    }

    /// <summary>
    /// A file's bytes, for showing or saving it outside the agent. Directories and files past the
    /// size limit are refused rather than partially returned.
    /// </summary>
    public async Task<FileContent> ReadAsync(string path, CancellationToken cancellationToken, long? maxBytes = null)
    {
        var full = Resolve(path, mustExist: true);
        if (Directory.Exists(full))
        {
            throw new ArgumentException($"'{Relative(full)}' is a directory, not a file.");
        }

        var limit = maxBytes ?? _maxReadBytes;
        var file = new FileInfo(full);
        if (file.Length > limit)
        {
            throw new ArgumentException(
                $"'{Relative(full)}' is {file.Length / (1024 * 1024)} MB, over the {limit / (1024 * 1024)} MB read limit.");
        }

        return new(
            Relative(full),
            file.Name,
            ContentTypes.TryGetContentType(file.Name, out var contentType) ? contentType : "application/octet-stream",
            await File.ReadAllBytesAsync(full, cancellationToken));
    }

    public async Task<FileSystemEntry> WriteTextAsync(string path, string content, bool overwrite, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ArgumentException($"'{Relative(full)}' is a directory.");
        }

        if (!overwrite && File.Exists(full))
        {
            throw new ArgumentException(
                $"'{Relative(full)}' already exists. Pass overwrite to replace it, or choose another name.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxWriteBytes)
        {
            throw new ArgumentException($"The text exceeds the {MaxWriteBytes / (1024 * 1024)} MB write limit.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, cancellationToken);
        return ToEntry(new FileInfo(full));
    }

    public FileSystemEntry CreateDirectory(string path)
    {
        var full = Resolve(path);
        if (File.Exists(full))
        {
            throw new ArgumentException($"'{Relative(full)}' is a file.");
        }

        Directory.CreateDirectory(full);
        return ToEntry(new DirectoryInfo(full));
    }

    public FileSystemEntry Move(string source, string destination, bool overwrite)
    {
        var from = Resolve(source, mustExist: true);
        var to = PrepareDestination(from, destination, overwrite);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite);
        }

        return Directory.Exists(to) ? ToEntry(new DirectoryInfo(to)) : ToEntry(new FileInfo(to));
    }

    public FileSystemEntry Copy(string source, string destination, bool overwrite)
    {
        var from = Resolve(source, mustExist: true);
        if (Directory.Exists(from))
        {
            throw new ArgumentException("Only files can be copied. Copy the files inside a directory one at a time.");
        }

        var to = PrepareDestination(from, destination, overwrite);
        File.Copy(from, to, overwrite);
        return ToEntry(new FileInfo(to));
    }

    public void Delete(string path, bool recursive)
    {
        var full = Resolve(path, mustExist: true);
        if (PathEquals(full, _root))
        {
            throw new ArgumentException("The working directory itself cannot be deleted.");
        }

        if (!Directory.Exists(full))
        {
            File.Delete(full);
            return;
        }

        // Emptying a directory is the kind of thing worth asking for on purpose rather than by default.
        if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new ArgumentException(
                $"'{Relative(full)}' is not empty. Pass recursive to delete it and everything in it.");
        }

        Directory.Delete(full, recursive);
    }

    private string PrepareDestination(string source, string destination, bool overwrite)
    {
        var to = Resolve(destination);

        // A bare directory as the destination means "put it in here", as the shell commands do.
        if (Directory.Exists(to) && !PathEquals(source, to))
        {
            to = System.IO.Path.Combine(to, System.IO.Path.GetFileName(source));
        }

        if (PathEquals(source, to))
        {
            throw new ArgumentException("The source and the destination are the same file.");
        }

        if (!overwrite && (File.Exists(to) || Directory.Exists(to)))
        {
            throw new ArgumentException($"'{Relative(to)}' already exists. Pass overwrite to replace it.");
        }

        if (Directory.Exists(source) && IsInside(to, source))
        {
            throw new ArgumentException("A directory cannot be moved into itself.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
        return to;
    }

    private FileSystemEntry ToEntry(FileSystemInfo entry) => new(
        Relative(entry.FullName),
        entry is DirectoryInfo,
        entry is FileInfo file ? file.Length : null,
        entry.LastWriteTimeUtc);

    /// <summary>
    /// The nearest existing ancestor of a path, so a write to a file that does not exist yet still has
    /// its parent directories checked for links.
    /// </summary>
    private static FileSystemInfo? FirstExisting(string path)
    {
        for (var candidate = path; !string.IsNullOrEmpty(candidate); candidate = System.IO.Path.GetDirectoryName(candidate))
        {
            if (File.Exists(candidate))
            {
                return new FileInfo(candidate);
            }

            if (Directory.Exists(candidate))
            {
                return new DirectoryInfo(candidate);
            }
        }

        return null;
    }

    private static FileSystemInfo? Parent(FileSystemInfo entry) =>
        entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent;

    private bool IsInsideRoot(string path) => PathEquals(path, _root) || IsInside(path, _root);

    private static bool IsInside(string path, string directory) =>
        path.StartsWith(
            directory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
            Comparison);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            left.TrimEnd(System.IO.Path.DirectorySeparatorChar),
            right.TrimEnd(System.IO.Path.DirectorySeparatorChar),
            Comparison);

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
