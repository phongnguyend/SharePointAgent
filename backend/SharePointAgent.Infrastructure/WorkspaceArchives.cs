using System.IO.Compression;
using SharePointAgent.Infrastructure.Workspaces;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// The rules every working directory applies to a zip before extracting it, wherever the files are: a
/// bounded number of entries and expanded size, and entry names that stay inside the destination and
/// never name the isolated workspace's bookkeeping files. One bad entry refuses the whole archive.
/// </summary>
public static class WorkspaceArchives
{
    /// <summary>The most entries one archive may hold, so a listing of the result stays manageable.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>The most an archive may expand to. SandboxHost applies the same limit itself.</summary>
    public const long MaxExtractBytes = 1024L * 1024 * 1024;

    /// <summary>Checks an archive's entries and returns their normalized relative paths.</summary>
    public static IReadOnlyList<string> Check(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries)
        {
            throw new ArgumentException($"The archive has {archive.Entries.Count} entries, over the {MaxEntries} entry limit.");
        }

        var declared = archive.Entries.Sum(entry => entry.Length);
        if (declared > MaxExtractBytes)
        {
            throw new ArgumentException($"The archive expands to {declared / (1024 * 1024)} MB, over the {MaxExtractBytes / (1024 * 1024)} MB extract limit.");
        }

        return archive.Entries.Select(entry => EntryPath(entry.FullName)).ToList();
    }

    /// <summary>
    /// An entry name as a path relative to the destination, with forward slashes. Rooted names, drive
    /// letters, '..', and reserved bookkeeping names are refused rather than cleaned up, because an
    /// archive that contains them was built to escape.
    /// </summary>
    public static string EntryPath(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        if (name.StartsWith('/') || (name.Length > 1 && name[1] == ':'))
        {
            throw new ArgumentException($"The archive entry '{fullName}' is an absolute path, so the archive was not extracted.");
        }

        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToList();
        if (parts.Contains(".."))
        {
            throw new ArgumentException($"The archive entry '{fullName}' points outside the destination, so the archive was not extracted.");
        }

        if (parts.Any(part => part.StartsWith(SandboxHostWorkspace.ReservedPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The archive entry '{fullName}' uses a reserved name, so the archive was not extracted.");
        }

        return string.Join('/', parts);
    }
}
