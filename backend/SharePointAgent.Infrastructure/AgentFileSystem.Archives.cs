using System.IO.Compression;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// Zip and unzip on the local working directory. Every path, including each extracted entry's, goes
/// through <see cref="Resolve"/>, so an archive can neither read nor write outside the directory, and
/// extraction counts the bytes it actually writes rather than trusting the sizes an archive declares.
/// </summary>
public sealed partial class AgentFileSystem
{
    public Task<FileSystemEntry> ZipAsync(IReadOnlyList<string> paths, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (paths is not { Count: > 0 })
        {
            throw new ArgumentException("Name at least one file or directory to zip.");
        }

        var sources = paths.Select(path => Resolve(path, mustExist: true)).ToList();
        var target = Resolve(destination);
        if (Directory.Exists(target))
        {
            throw new ArgumentException($"'{Relative(target)}' is a directory. Give the archive a file name.");
        }

        if (!overwrite && File.Exists(target))
        {
            throw new ArgumentException($"'{Relative(target)}' already exists. Pass overwrite to replace it.");
        }

        if (sources.Any(source => PathEquals(source, target)))
        {
            throw new ArgumentException("The archive cannot include itself.");
        }

        // Listed before writing, so an archive created inside a directory being zipped never includes itself.
        var files = sources
            .SelectMany(source => Directory.Exists(source)
                ? new DirectoryInfo(source).EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).Select(file => file.FullName)
                : [source])
            .Where(file => !PathEquals(file, target))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        if (files.Count > WorkspaceArchives.MaxEntries)
        {
            throw new ArgumentException($"That is {files.Count} files, over the {WorkspaceArchives.MaxEntries} entry limit for one archive.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var staging = $"{target}.{Guid.NewGuid():N}.part";
        try
        {
            using (var archive = ZipFile.Open(staging, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(file, Relative(file), CompressionLevel.Optimal);
                }
            }

            File.Move(staging, target, overwrite);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }

        return Task.FromResult(ToEntry(new FileInfo(target)));
    }

    public async Task<FileSystemEntry> UnzipAsync(string path, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var source = Resolve(path, mustExist: true);
        if (Directory.Exists(source))
        {
            throw new ArgumentException($"'{Relative(source)}' is a directory, not a zip file.");
        }

        var target = Resolve(destination);
        if (File.Exists(target))
        {
            throw new ArgumentException($"'{Relative(target)}' already exists as a file.");
        }

        using var archive = OpenArchive(source);
        var entries = WorkspaceArchives.Check(archive);

        // Every entry is placed and checked before anything is written, so a refused archive leaves nothing behind.
        var plan = new List<(ZipArchiveEntry Entry, string Full)>();
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            if (entries[index].Length == 0)
            {
                continue;
            }

            var full = Resolve(System.IO.Path.Combine(target, entries[index]));
            if (!PathEquals(full, target) && !IsInside(full, target))
            {
                throw new ArgumentException($"The archive entry '{archive.Entries[index].FullName}' points outside the destination, so the archive was not extracted.");
            }

            var isDirectory = archive.Entries[index].FullName.EndsWith('/') || archive.Entries[index].FullName.EndsWith('\\');
            if (!isDirectory && Directory.Exists(full))
            {
                throw new ArgumentException($"'{Relative(full)}' is a directory, so the archive was not extracted.");
            }

            if (!isDirectory && !overwrite && File.Exists(full))
            {
                throw new ArgumentException($"'{Relative(full)}' already exists. Pass overwrite to replace existing files.");
            }

            plan.Add((archive.Entries[index], full));
        }

        Directory.CreateDirectory(target);
        long written = 0;
        foreach (var (entry, full) in plan)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(full);
                continue;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            var staging = $"{full}.{Guid.NewGuid():N}.part";
            try
            {
                await using (var input = entry.Open())
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        // Declared sizes can lie; what is actually written is what fills the disk.
                        written += read;
                        if (written > WorkspaceArchives.MaxExtractBytes)
                        {
                            throw new ArgumentException($"The archive expands past the {WorkspaceArchives.MaxExtractBytes / (1024 * 1024)} MB extract limit; extraction stopped.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
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
        }

        return ToEntry(new DirectoryInfo(target));
    }

    private ZipArchive OpenArchive(string source)
    {
        try
        {
            return ZipFile.OpenRead(source);
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException($"'{Relative(source)}' is not a valid zip file.");
        }
    }
}
