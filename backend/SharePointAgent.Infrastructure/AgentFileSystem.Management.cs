using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public sealed partial class AgentFileSystem
{
    public const int MaxUploadBytes = 5 * 1024 * 1024;

    private const int MaxManagedEntries = 5000;

    private const long MaxCopyBytes = 100 * 1024 * 1024;

    public async Task<SandboxFileChangeResult> ManageAsync(SandboxFileChange change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (change.Operation is not ("mkdir" or "upload" or "rename" or "move" or "copy" or "delete"))
        {
            throw new ArgumentException("Unknown file operation.");
        }
        var source = ManagedPath(change.Path);
        if (change.Operation is "mkdir" or "upload")
        {
            EnsureNewDestination(source);
            if (change.Operation == "mkdir")
            {
                Directory.CreateDirectory(source);
            }
            else
            {
                if (change.Content is null || change.Content.Length > MaxUploadBytes)
                {
                    throw new ArgumentException("Upload a file of 5 MB or smaller.");
                }
                var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, System.IO.Path.GetRandomFileName());
                var created = false;
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        created = true;
                        await output.WriteAsync(change.Content, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, ManagedPath(change.Path), overwrite: false);
                }
                finally
                {
                    if (created)
                    {
                        File.Delete(temporary);
                    }
                }
            }
            return new(Relative(source));
        }

        Resolve(source, mustExist: true);
        // Inspect descendants without following links before recursive operations.
        var tree = ManagedTree(source, ct);
        if (change.Operation == "delete")
        {
            Delete(source, recursive: true);
            return new(Relative(source));
        }

        var destination = ManagedPath(change.Destination);
        if (PathEquals(source, destination) || (Directory.Exists(source) && IsInside(destination, source)))
        {
            throw new ArgumentException("Choose a different destination outside the source folder.");
        }
        if (change.Operation == "rename" && !PathEquals(System.IO.Path.GetDirectoryName(source)!, System.IO.Path.GetDirectoryName(destination)!))
        {
            throw new ArgumentException("Rename must keep the item in the same folder. Use Move to change folders.");
        }
        EnsureNewDestination(destination);
        if (change.Operation is "move" or "rename")
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(source))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination, overwrite: false);
            }
            return new(Relative(destination));
        }

        if (tree.Where(entry => !entry.Directory).Sum(entry => new FileInfo(entry.Path).Length) > MaxCopyBytes)
        {
            throw new ArgumentException("A copy is limited to 100 MB. Copy smaller groups of files.");
        }
        var staging = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, System.IO.Path.GetRandomFileName());
        try
        {
            long copied = 0;
            foreach (var entry in tree)
            {
                ct.ThrowIfCancellationRequested();
                var target = PathEquals(entry.Path, source) ? staging : System.IO.Path.Combine(staging, System.IO.Path.GetRelativePath(source, entry.Path));
                Resolve(entry.Path, mustExist: true);
                Resolve(target);
                if (entry.Directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                await using var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    copied += count;
                    if (copied > MaxCopyBytes)
                    {
                        throw new ArgumentException("A copy is limited to 100 MB.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            Resolve(destination);
            if (Directory.Exists(staging))
            {
                Directory.Move(staging, destination);
            }
            else
            {
                File.Move(staging, destination, overwrite: false);
            }
        }
        finally
        {
            Resolve(staging);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            else
            {
                File.Delete(staging);
            }
        }
        return new(Relative(destination));
    }

    private string ManagedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path)
            || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
                || part != part.Trim() || part.EndsWith('.') || part.Any(c => char.IsControl(c) || "\\:*?\"<>|".Contains(c))))
        {
            throw new ArgumentException("Use a nonempty path relative to the sandbox, without '..' or reserved filename characters.");
        }
        var full = Resolve(path);
        if (PathEquals(full, _root))
        {
            throw new ArgumentException("The sandbox root cannot be changed.");
        }
        return full;
    }

    private void EnsureNewDestination(string path)
    {
        Resolve(path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new ArgumentException("The destination already exists. Choose another name.");
        }
        Directory.CreateDirectory(_root);
        if (!Directory.Exists(System.IO.Path.GetDirectoryName(path)))
        {
            throw new ArgumentException("The destination folder does not exist. Create it first.");
        }
    }

    private List<(string Path, bool Directory)> ManagedTree(string source, CancellationToken ct)
    {
        var entries = new List<(string Path, bool Directory)>();
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var path))
        {
            ct.ThrowIfCancellationRequested();
            Resolve(path, mustExist: true);
            var directory = Directory.Exists(path);
            entries.Add((path, directory));
            if (entries.Count > MaxManagedEntries)
            {
                throw new ArgumentException("This operation is limited to 5,000 entries. Work on smaller folders.");
            }
            if (directory)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if (pending.Count + entries.Count >= MaxManagedEntries)
                    {
                        throw new ArgumentException("This operation is limited to 5,000 entries. Work on smaller folders.");
                    }
                    pending.Push(child);
                }
            }
        }
        return entries;
    }
}
