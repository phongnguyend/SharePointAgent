using System.IO.Compression;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SharePointAgent.SandboxHost;

/// <summary>
/// The file operations, all confined to one workspace directory. The container is the security
/// boundary — a script can read anything the process user can — so the confinement here is about
/// predictable tool calls: every path is relative to the workspace, and no operation reaches outside it
/// by <c>..</c> or an absolute path. Recursive operations skip symbolic links so a link cycle, or a link
/// to <c>/</c>, cannot turn a copy or a search into a walk of the whole disk.
/// <para>
/// Errors use exception types the endpoints map to status codes: <see cref="ArgumentException"/> for a
/// bad request, <see cref="FileNotFoundException"/> for a missing path, and <see cref="IOException"/>
/// for a conflict with what is already on disk.
/// </para>
/// </summary>
public sealed class SandboxWorkspace
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SandboxHostOptions _options;

    public SandboxWorkspace(IOptions<SandboxHostOptions> options)
    {
        _options = options.Value;
        Root = _options.ResolvedWorkspaceRoot;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>
    /// The absolute path a request refers to. A relative path is taken from the workspace; an absolute
    /// path is accepted only when it is already inside it, so paths a script prints can be passed back.
    /// </summary>
    public string Resolve(string? path)
    {
        var trimmed = path?.Trim() ?? "";
        if (trimmed.Contains('\0'))
        {
            throw new ArgumentException("Paths cannot contain NUL characters.");
        }

        var full = trimmed.Length == 0 || trimmed is "." or "./"
            ? Root
            : Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(Root, trimmed));
        full = Path.TrimEndingDirectorySeparator(full);
        if (!IsInsideRoot(full))
        {
            throw new ArgumentException($"'{trimmed}' is outside the workspace. Use a path relative to it.");
        }

        return full;
    }

    /// <summary>The path as callers see it: relative to the workspace, with forward slashes.</summary>
    public string Relative(string fullPath)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(fullPath), Root, PathComparison))
        {
            return ".";
        }

        return Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    public FileEntry Describe(string? path)
    {
        return ToEntry(Existing(Resolve(path)));
    }

    public FileListing List(string? path, bool recursive, string? pattern)
    {
        var full = Resolve(path);
        if (File.Exists(full))
        {
            return new(Relative(full), [ToEntry(new FileInfo(full))], false);
        }

        if (!Directory.Exists(full))
        {
            throw new FileNotFoundException($"'{Relative(full)}' does not exist.");
        }

        var found = new DirectoryInfo(full)
            .EnumerateFileSystemInfos("*", recursive ? Recursive : TopLevel)
            .Where(info => string.IsNullOrWhiteSpace(pattern) || FileSystemName.MatchesSimpleExpression(pattern, info.Name, ignoreCase: true))
            .Take(_options.MaxListEntries + 1)
            .ToList();
        var entries = found.Take(_options.MaxListEntries)
            .Select(ToEntry)
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(Relative(full), entries, found.Count > _options.MaxListEntries);
    }

    /// <summary>The absolute path of an existing file, for streaming it back out.</summary>
    public string ResolveFile(string? path)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ArgumentException($"'{Relative(full)}' is a directory, not a file.");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"'{Relative(full)}' does not exist.");
        }

        return full;
    }

    /// <summary>
    /// Writes a stream to a file, creating parent directories. The bytes go to a temporary sibling
    /// first, so a failed or oversized upload never leaves half a file in place of the old one.
    /// </summary>
    public async Task<FileEntry> WriteAsync(string? path, Stream content, bool overwrite, CancellationToken cancellationToken)
    {
        var full = PrepareFileWrite(path, overwrite);
        var temporary = $"{full}.{Guid.NewGuid():N}.partial";
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > _options.MaxFileBytes)
                    {
                        throw new ArgumentException($"The upload is over the {_options.MaxFileBytes / (1024 * 1024)} MB file limit.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return ToEntry(new FileInfo(full));
    }

    /// <summary>
    /// Reads a text file, optionally a line range of it. Binary files are refused rather than returned
    /// as mojibake; download them through the content route instead.
    /// </summary>
    public async Task<TextFileContent> ReadTextAsync(string? path, int? startLine, int? lineCount, CancellationToken cancellationToken)
    {
        var full = ResolveFile(path);
        var first = startLine ?? 1;
        if (first < 1)
        {
            throw new ArgumentException("startLine is one-based and must be at least 1.");
        }

        if (lineCount is < 0)
        {
            throw new ArgumentException("lineCount cannot be negative.");
        }

        if (await LooksBinaryAsync(full, cancellationToken))
        {
            throw new ArgumentException($"'{Relative(full)}' looks like a binary file. Download it from /files/content instead.");
        }

        var builder = new StringBuilder();
        var total = 0;
        var returned = 0;
        var truncated = false;
        await foreach (var line in File.ReadLinesAsync(full, cancellationToken))
        {
            total++;
            if (total < first || truncated || (lineCount is { } limit && returned >= limit))
            {
                continue;
            }

            if (builder.Length + line.Length + 1 > _options.MaxTextReadChars)
            {
                truncated = true;
                continue;
            }

            if (returned > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
            returned++;
        }

        return new(Relative(full), builder.ToString(), first, returned, total, truncated);
    }

    public async Task<FileEntry> WriteTextAsync(WriteTextRequest request, CancellationToken cancellationToken)
    {
        var content = request.Content ?? throw new ArgumentException("content is required.");
        if (Utf8NoBom.GetByteCount(content) > _options.MaxFileBytes)
        {
            throw new ArgumentException($"The content is over the {_options.MaxFileBytes / (1024 * 1024)} MB file limit.");
        }

        var full = PrepareFileWrite(request.Path, request.Overwrite || request.Append);
        if (request.Append)
        {
            await File.AppendAllTextAsync(full, content, Utf8NoBom, cancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(full, content, Utf8NoBom, cancellationToken);
        }

        return ToEntry(new FileInfo(full));
    }

    /// <summary>
    /// Replaces exact text in a file. Requiring a unique match unless <c>ReplaceAll</c> is set means an
    /// edit never lands on the wrong one of several similar lines. A UTF-8 byte order mark is kept.
    /// </summary>
    public async Task<EditTextResult> EditTextAsync(EditTextRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.OldText))
        {
            throw new ArgumentException("oldText is required.");
        }

        var full = ResolveFile(request.Path);
        if (new FileInfo(full).Length > _options.MaxFileBytes)
        {
            throw new ArgumentException($"'{Relative(full)}' is over the {_options.MaxFileBytes / (1024 * 1024)} MB edit limit.");
        }

        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Utf8NoBom.GetString(hasBom ? bytes[Encoding.UTF8.Preamble.Length..] : bytes);
        var occurrences = CountOccurrences(text, request.OldText);
        if (occurrences == 0)
        {
            throw new ArgumentException($"oldText was not found in '{Relative(full)}'.");
        }

        if (occurrences > 1 && !request.ReplaceAll)
        {
            throw new ArgumentException($"oldText occurs {occurrences} times in '{Relative(full)}'. Include more surrounding text, or set replaceAll.");
        }

        var updated = text.Replace(request.OldText, request.NewText ?? "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(full, updated, hasBom ? new UTF8Encoding(true) : Utf8NoBom, cancellationToken);
        return new(Relative(full), occurrences);
    }

    public FileEntry CreateDirectory(string? path)
    {
        var full = Resolve(path);
        if (File.Exists(full))
        {
            throw new IOException($"'{Relative(full)}' already exists as a file.");
        }

        return ToEntry(Directory.CreateDirectory(full));
    }

    public FileEntry Move(TransferRequest request)
    {
        var (source, destination) = PrepareTransfer(request);
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
            return ToEntry(new DirectoryInfo(destination));
        }

        File.Move(source, destination, overwrite: false);
        return ToEntry(new FileInfo(destination));
    }

    public FileEntry Copy(TransferRequest request)
    {
        var (source, destination) = PrepareTransfer(request);
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
            return ToEntry(new DirectoryInfo(destination));
        }

        File.Copy(source, destination, overwrite: false);
        return ToEntry(new FileInfo(destination));
    }

    public void Delete(string? path, bool recursive)
    {
        var full = Resolve(path);
        if (string.Equals(full, Root, PathComparison))
        {
            throw new ArgumentException("The workspace root cannot be deleted. Delete its contents instead.");
        }

        if (File.Exists(full))
        {
            File.Delete(full);
            return;
        }

        if (!Directory.Exists(full))
        {
            throw new FileNotFoundException($"'{Relative(full)}' does not exist.");
        }

        if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new IOException($"'{Relative(full)}' is not empty. Set recursive to delete it with its contents.");
        }

        Directory.Delete(full, recursive);
    }

    /// <summary>Finds matching lines across text files, skipping binary files and files over the size limit.</summary>
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Query))
        {
            throw new ArgumentException("query is required.");
        }

        var limit = Math.Clamp(request.MaxResults ?? _options.MaxSearchResults, 1, _options.MaxSearchResults);
        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = null;
        if (request.IsRegex)
        {
            try
            {
                var options = RegexOptions.CultureInvariant | (request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                regex = new Regex(request.Query, options, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"query is not a valid regular expression: {ex.Message}");
            }
        }

        var start = Resolve(request.Path);
        IEnumerable<FileInfo> files;
        if (File.Exists(start))
        {
            files = [new FileInfo(start)];
        }
        else if (Directory.Exists(start))
        {
            files = new DirectoryInfo(start).EnumerateFiles("*", Recursive);
        }
        else
        {
            throw new FileNotFoundException($"'{Relative(start)}' does not exist.");
        }

        var matches = new List<SearchMatch>();
        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(request.Glob) && !FileSystemName.MatchesSimpleExpression(request.Glob, file.Name, ignoreCase: true))
            {
                continue;
            }

            if (file.Length > _options.MaxFileBytes || await LooksBinaryAsync(file.FullName, cancellationToken))
            {
                continue;
            }

            var number = 0;
            await foreach (var line in File.ReadLinesAsync(file.FullName, cancellationToken))
            {
                number++;
                var hit = regex is null ? line.Contains(request.Query, comparison) : IsMatch(regex, line);
                if (!hit)
                {
                    continue;
                }

                if (matches.Count == limit)
                {
                    return new(matches, true);
                }

                matches.Add(new(Relative(file.FullName), number, line.Length > 500 ? line[..500] : line));
            }
        }

        return new(matches, false);
    }

    /// <summary>Packs files and directories into a zip, with entry names relative to the workspace.</summary>
    public FileEntry Zip(ZipRequest request)
    {
        if (request.Paths is not { Count: > 0 })
        {
            throw new ArgumentException("paths must name at least one file or directory.");
        }

        var sources = request.Paths.Select(path => Existing(Resolve(path)).FullName).ToList();
        var destination = PrepareFileWrite(request.Destination, request.Overwrite);
        if (sources.Any(source => string.Equals(source, destination, PathComparison)))
        {
            throw new ArgumentException("The archive cannot include itself.");
        }

        // Listed before writing, so an archive created inside a directory being zipped never includes itself.
        var files = sources
            .SelectMany(source => Directory.Exists(source)
                ? new DirectoryInfo(source).EnumerateFiles("*", Recursive).Select(file => file.FullName)
                : [source])
            .Where(file => !string.Equals(file, destination, PathComparison))
            .Distinct(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        var temporary = $"{destination}.{Guid.NewGuid():N}.partial";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    archive.CreateEntryFromFile(file, Relative(file), CompressionLevel.Optimal);
                }
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return ToEntry(new FileInfo(destination));
    }

    /// <summary>
    /// Extracts a zip into a directory. The declared size is checked first so an archive cannot fill
    /// the disk; .NET itself refuses entries whose names would land outside the destination.
    /// </summary>
    public FileEntry Unzip(UnzipRequest request)
    {
        var source = ResolveFile(request.Path);
        var destination = Resolve(request.Destination);
        if (File.Exists(destination))
        {
            throw new IOException($"'{Relative(destination)}' already exists as a file.");
        }

        using (var archive = ZipFile.OpenRead(source))
        {
            var declared = archive.Entries.Sum(entry => entry.Length);
            if (declared > _options.MaxExtractBytes)
            {
                throw new ArgumentException($"The archive expands to {declared / (1024 * 1024)} MB, over the {_options.MaxExtractBytes / (1024 * 1024)} MB extract limit.");
            }

            if (!request.Overwrite)
            {
                var clash = archive.Entries
                    .Where(entry => entry.Name.Length > 0)
                    .Select(entry => Path.GetFullPath(Path.Combine(destination, entry.FullName)))
                    .FirstOrDefault(File.Exists);
                if (clash is not null)
                {
                    throw new IOException($"'{Relative(clash)}' already exists. Set overwrite to replace existing files.");
                }
            }
        }

        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(source, destination, overwriteFiles: request.Overwrite);
        return ToEntry(new DirectoryInfo(destination));
    }

    private string PrepareFileWrite(string? path, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path is required.");
        }

        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new IOException($"'{Relative(full)}' is a directory.");
        }

        if (File.Exists(full) && !overwrite)
        {
            throw new IOException($"'{Relative(full)}' already exists. Set overwrite to replace it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    /// <summary>
    /// Validates both ends of a move or copy. An existing destination is replaced only with
    /// <c>Overwrite</c>, and is deleted first, so a directory copy never merges into an older tree.
    /// </summary>
    private (string Source, string Destination) PrepareTransfer(TransferRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Destination))
        {
            throw new ArgumentException("source and destination are required.");
        }

        var source = Existing(Resolve(request.Source)).FullName;
        var destination = Resolve(request.Destination);
        if (string.Equals(source, Root, PathComparison) || string.Equals(destination, Root, PathComparison))
        {
            throw new ArgumentException("The workspace root cannot be moved, copied, or replaced.");
        }

        if (string.Equals(source, destination, PathComparison))
        {
            throw new ArgumentException("source and destination are the same path.");
        }

        if (Directory.Exists(source) && destination.StartsWith(source + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new ArgumentException("A directory cannot be moved or copied into itself.");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            if (!request.Overwrite)
            {
                throw new IOException($"'{Relative(destination)}' already exists. Set overwrite to replace it.");
            }

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            else
            {
                File.Delete(destination);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return (source, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in new DirectoryInfo(source).EnumerateDirectories("*", Recursive))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory.FullName)));
        }

        foreach (var file in new DirectoryInfo(source).EnumerateFiles("*", Recursive))
        {
            file.CopyTo(Path.Combine(destination, Path.GetRelativePath(source, file.FullName)));
        }
    }

    private FileSystemInfo Existing(string full)
    {
        if (File.Exists(full))
        {
            return new FileInfo(full);
        }

        if (Directory.Exists(full))
        {
            return new DirectoryInfo(full);
        }

        throw new FileNotFoundException($"'{Relative(full)}' does not exist.");
    }

    private FileEntry ToEntry(FileSystemInfo info)
    {
        var isDirectory = info is DirectoryInfo;
        return new(
            Relative(info.FullName),
            string.Equals(Path.TrimEndingDirectorySeparator(info.FullName), Root, PathComparison) ? "." : info.Name,
            isDirectory,
            isDirectory ? null : ((FileInfo)info).Length,
            info.LastWriteTimeUtc);
    }

    private bool IsInsideRoot(string full)
    {
        return string.Equals(full, Root, PathComparison)
            || full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>A NUL byte in the first 8 KB is the same heuristic git uses to call a file binary.</summary>
    private static async Task<bool> LooksBinaryAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[8000];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        return buffer.AsSpan(0, read).Contains((byte)0);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static bool IsMatch(Regex regex, string line)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ArgumentException("The regular expression took too long on a line. Simplify the pattern.");
        }
    }
}
