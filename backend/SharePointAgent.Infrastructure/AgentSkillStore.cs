using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

public sealed record AgentSkillInfo(string Id, string Name, string Description, string Scope, bool IsDefault, int VersionCount);

public sealed record AgentSkillFileInfo(string Path, long Size);

public sealed record AgentSkillVersionInfo(int Version, string Description, DateTimeOffset CreatedAtUtc, bool IsActive);

/// <summary>Database catalog and file metadata; file contents live in blob storage.</summary>
public sealed class AgentSkillStore(IDbContextFactory<SharePointIndexDbContext> factory,
    IAgentSkillFileStorage storage, ILogger<AgentSkillStore> logger, AgentSkillFileCache fileCache)
{
    public const int MaxArchiveBytes = 10 * 1024 * 1024;
    public const int MaxExpandedBytes = 20 * 1024 * 1024;

    public static string Scope(Guid? agentId, Guid? subAgentId) => subAgentId is { } child
        ? $"sub:{child:N}" : agentId is { } parent ? $"agent:{parent:N}" : "global";

    public async Task<bool> ScopeExistsAsync(Guid? agentId, Guid? subAgentId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (subAgentId is { } child)
        {
            return agentId is { } parent && await db.SubAgentDefinitions.AnyAsync(
                x => x.Id == child && x.ParentAgentId == parent, cancellationToken);
        }
        return agentId is not { } id || await db.AgentDefinitions.AnyAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentSkillInfo>> ListAsync(string scope, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AgentSkillDefinitions.AsNoTracking().Where(x => x.Scope == "global" || x.Scope == scope)
            .OrderBy(x => x.Name).Select(x => new AgentSkillInfo(x.Id.ToString(), x.Name, x.Description, x.Scope, x.IsDefault,
                db.AgentSkillVersions.Count(version => version.AgentSkillDefinitionId == x.Id)))
            .ToArrayAsync(cancellationToken);
    }

    public async Task SynchronizeDefaultsAsync(CancellationToken cancellationToken)
    {
        var defaults = await ChatAgentSkills.GetFileSkillsAsync(ChatAgentSkills.DirectoryPath, cancellationToken);
        foreach (var skill in defaults)
        {
            var files = Directory.EnumerateFiles(skill.Path, "*", SearchOption.AllDirectories)
                .ToDictionary(x => Path.GetRelativePath(skill.Path, x).Replace('\\', '/'), File.ReadAllBytes);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await SaveAsync(skill.Frontmatter.Name, skill.Frontmatter.Description, "global", true, files, null, cancellationToken);
                    break;
                }
                catch (Exception ex) when (attempt < 2 && IsConcurrentWrite(ex))
                {
                    // API replicas can synchronize the same bundled definitions at startup.
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken);
                }
            }
        }
    }

    private static bool IsConcurrentWrite(Exception ex) => ex is Microsoft.Data.SqlClient.SqlException { Number: 1205 or 2601 or 2627 }
        || ex is DbUpdateException { InnerException: Microsoft.Data.SqlClient.SqlException { Number: 1205 or 2601 or 2627 } };

    public Task<AgentSkillInfo> UploadAsync(string scope, byte[] archive, CancellationToken cancellationToken) =>
        UploadAsync(scope, archive, null, cancellationToken);

    public async Task<AgentSkillInfo> UploadAsync(string scope, byte[] archive, Guid? replacementId, CancellationToken cancellationToken)
    {
        var files = ReadArchive(archive);
        using var bundle = new AgentSkillDirectory();
        var manifest = Encoding.UTF8.GetString(files["SKILL.md"]).TrimStart('\uFEFF').Replace("\r\n", "\n");
        var headerEnd = manifest.Length >= 4 ? manifest.IndexOf("\n---", 4, StringComparison.Ordinal) : -1;
        var match = manifest.StartsWith("---\n", StringComparison.Ordinal) && headerEnd >= 4
            ? Regex.Match(manifest[4..headerEnd], "^name:[ \\t]*['\"]?([a-z0-9]+(?:-[a-z0-9]+)*)['\"]?[ \\t]*(?:#.*)?$", RegexOptions.Multiline)
            : Match.Empty;
        if (!match.Success || match.Groups[1].Value.Length > 64 || !IsSafeSegment(match.Groups[1].Value))
        {
            throw new InvalidDataException("SKILL.md must have YAML frontmatter with a single-line name of up to 64 lowercase letters, digits, and single hyphens.");
        }
        WriteFiles(System.IO.Path.Combine(bundle.Path, match.Groups[1].Value), files);
        var catalog = await ChatAgentSkills.GetCatalogAsync(bundle.Path, cancellationToken);
        if (catalog.Count != 1 || string.IsNullOrWhiteSpace(catalog[0].Description) ||
            catalog[0].Name.Length > 100 || catalog[0].Description.Length > 4000)
        {
            throw new InvalidDataException("The ZIP must contain one valid SKILL.md with a name (up to 100 characters) and description (up to 4000 characters).");
        }
        var skill = catalog[0];
        try
        {
            return await SaveAsync(skill.Name, skill.Description, scope, false, files, replacementId, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            throw new InvalidDataException("A skill with this name already exists.", ex);
        }
    }

    private async Task<AgentSkillInfo> SaveAsync(string name, string description, string scope, bool isDefault,
        Dictionary<string, byte[]> files, Guid? replacementId, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
            hash.AppendData(SHA256.HashData(bytes));
        }
        var contentHash = Convert.ToHexString(hash.GetHashAndReset());
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var entity = await db.AgentSkillDefinitions.Include(x => x.Files).SingleOrDefaultAsync(
            x => replacementId != null ? x.Id == replacementId && x.Scope == scope : x.Name == name, cancellationToken);
        if (replacementId is not null)
        {
            if (entity is null || entity.IsDefault)
            {
                throw new InvalidDataException("Only uploaded skills in this scope can be replaced.");
            }
            if (!string.Equals(entity.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The replacement SKILL.md name must remain '{entity.Name}'. Renaming a skill is not allowed.");
            }
        }
        else if (entity is not null && (!isDefault || !entity.IsDefault))
        {
            throw new InvalidDataException("A skill with this name already exists. Use a unique name in SKILL.md.");
        }
        if (entity is not null && entity.ContentHash == contentHash)
        {
            return new(entity.Id.ToString(), entity.Name, entity.Description, entity.Scope, entity.IsDefault,
                await db.AgentSkillVersions.CountAsync(x => x.AgentSkillDefinitionId == entity.Id, cancellationToken));
        }
        var now = DateTimeOffset.UtcNow;
        if (entity is null)
        {
            entity = new AgentSkillDefinitionEntity { Name = name, Scope = scope, IsDefault = isDefault, CreatedAtUtc = now };
            db.AgentSkillDefinitions.Add(entity);
        }
        else
        {
            entity.ActiveVersion = ++entity.LastVersion;
        }
        entity.Description = description;
        entity.ContentHash = contentHash;
        entity.UpdatedAtUtc = now;
        db.AgentSkillVersions.Add(new AgentSkillVersionEntity
        {
            Definition = entity,
            Version = entity.ActiveVersion,
            Description = description,
            ContentHash = contentHash,
            CreatedAtUtc = now
        });
        foreach (var (path, bytes) in files)
        {
            entity.Files.Add(new AgentSkillFileEntity { Path = path, SizeBytes = bytes.LongLength, Version = entity.ActiveVersion });
        }
        var uploaded = new List<string>();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var file in entity.Files.Where(x => x.Version == entity.ActiveVersion))
            {
                file.StorageKey = $"agent-skills/{entity.Id:N}/{file.Id:N}";
                uploaded.Add(file.StorageKey);
                await storage.UploadAsync(file.StorageKey, files[file.Path], cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await CleanupFilesAsync(uploaded);
            throw;
        }
        return new(entity.Id.ToString(), entity.Name, entity.Description, entity.Scope, entity.IsDefault,
            await db.AgentSkillVersions.CountAsync(x => x.AgentSkillDefinitionId == entity.Id, cancellationToken));
    }

    public async Task<bool> DeleteAsync(string scope, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AgentSkillDefinitions.Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == id && x.Scope == scope && !x.IsDefault, cancellationToken);
        if (entity is null)
        {
            return false;
        }
        foreach (var agent in await db.AgentDefinitions.Where(x => x.Skills != null && x.Skills.Contains(entity.Name)).ToArrayAsync(cancellationToken))
        {
            agent.Skills = agent.Skills!.Where(x => x != entity.Name).ToArray();
        }
        foreach (var agent in await db.SubAgentDefinitions.Where(x => x.Skills.Contains(entity.Name)).ToArrayAsync(cancellationToken))
        {
            agent.Skills = agent.Skills.Where(x => x != entity.Name).ToArray();
        }
        var blobs = entity.Files.Select(x => x.StorageKey).ToArray();
        db.AgentSkillDefinitions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        await CleanupFilesAsync(blobs);
        return true;
    }

    public async Task CleanupFilesAsync(IEnumerable<string> storageKeys)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var storageKey in storageKeys)
        {
            try
            {
                await storage.DeleteAsync(storageKey, cleanup.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not clean up agent skill storage object {StorageKey}; it is no longer referenced by the catalog", storageKey);
            }
        }
    }

    public async Task<IReadOnlyList<AgentSkillVersionInfo>?> ListVersionsAsync(string scope, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var skill = await db.AgentSkillDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && (x.Scope == scope || x.Scope == "global"), cancellationToken);
        if (skill is null)
        {
            return null;
        }
        return await db.AgentSkillVersions.Where(x => x.AgentSkillDefinitionId == id).OrderByDescending(x => x.Version)
            .Select(x => new AgentSkillVersionInfo(x.Version, x.Description, x.CreatedAtUtc, x.Version == skill.ActiveVersion)).ToArrayAsync(cancellationToken);
    }

    public async Task<bool> SetActiveVersionAsync(string scope, Guid id, int version, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var skill = await db.AgentSkillDefinitions.SingleOrDefaultAsync(x => x.Id == id && x.Scope == scope && !x.IsDefault, cancellationToken);
        if (skill is null)
        {
            return false;
        }
        var target = await db.AgentSkillVersions.SingleOrDefaultAsync(x => x.AgentSkillDefinitionId == id && x.Version == version, cancellationToken);
        if (target is null)
        {
            return false;
        }
        if (skill.ActiveVersion != version)
        {
            skill.ActiveVersion = version;
            skill.Description = target.Description;
            skill.ContentHash = target.ContentHash;
            skill.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteVersionAsync(string scope, Guid id, int version, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var skill = await db.AgentSkillDefinitions.SingleOrDefaultAsync(x => x.Id == id && x.Scope == scope && !x.IsDefault, cancellationToken);
        if (skill is null)
        {
            return false;
        }
        if (version == skill.ActiveVersion)
        {
            throw new InvalidDataException("The active skill version cannot be deleted.");
        }
        var entry = await db.AgentSkillVersions.SingleOrDefaultAsync(x => x.AgentSkillDefinitionId == id && x.Version == version, cancellationToken);
        if (entry is null)
        {
            return false;
        }
        var files = await db.AgentSkillFiles.Where(x => x.AgentSkillDefinitionId == id && x.Version == version).ToArrayAsync(cancellationToken);
        db.AgentSkillFiles.RemoveRange(files);
        db.AgentSkillVersions.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await CleanupFilesAsync(files.Select(x => x.StorageKey));
        return true;
    }

    public Task<(string Name, byte[] Content)?> DownloadArchiveAsync(string scope, Guid id, CancellationToken cancellationToken) =>
        DownloadArchiveAsync(scope, id, null, cancellationToken);

    public async Task<(string Name, byte[] Content)?> DownloadArchiveAsync(string scope, Guid id, int? version, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var skill = await db.AgentSkillDefinitions.AsNoTracking().Include(x => x.Files)
            .SingleOrDefaultAsync(x => x.Id == id && (x.Scope == scope || x.Scope == "global"), cancellationToken);
        if (skill is null || !await db.AgentSkillVersions.AnyAsync(x => x.AgentSkillDefinitionId == id && x.Version == (version ?? skill.ActiveVersion), cancellationToken))
        {
            return null;
        }
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in skill.Files.Where(x => x.Version == (version ?? skill.ActiveVersion)).OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                var bytes = await storage.ReadAsync(file.StorageKey, cancellationToken);
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes, cancellationToken);
            }
        }
        return (version is null ? $"{skill.Name}.zip" : $"{skill.Name}-v{version}.zip", output.ToArray());
    }

    public Task<IReadOnlyList<AgentSkillFileInfo>?> ListFilesAsync(string scope, Guid id, CancellationToken cancellationToken) =>
        ListFilesAsync(scope, id, null, cancellationToken);

    public async Task<IReadOnlyList<AgentSkillFileInfo>?> ListFilesAsync(string scope, Guid id, int? version, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.AgentSkillDefinitions.AnyAsync(x => x.Id == id && (x.Scope == scope || x.Scope == "global"), cancellationToken))
        {
            return null;
        }
        if (version is not null && !await db.AgentSkillVersions.AnyAsync(x => x.AgentSkillDefinitionId == id && x.Version == version, cancellationToken))
        {
            return null;
        }
        return await db.AgentSkillFiles.AsNoTracking().Where(x => x.AgentSkillDefinitionId == id && x.Version == (version ?? x.Definition.ActiveVersion))
            .OrderBy(x => x.Path).Select(x => new AgentSkillFileInfo(x.Path, x.SizeBytes)).ToArrayAsync(cancellationToken);
    }

    public Task<AgentSkillFileEntity?> GetFileAsync(string scope, Guid id, string path, CancellationToken cancellationToken) =>
        GetFileAsync(scope, id, path, null, cancellationToken);

    public async Task<AgentSkillFileEntity?> GetFileAsync(string scope, Guid id, string path, int? version, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AgentSkillFiles.AsNoTracking().SingleOrDefaultAsync(x => x.AgentSkillDefinitionId == id && x.Path == path && x.Version == (version ?? x.Definition.ActiveVersion) &&
            (x.Definition.Scope == scope || x.Definition.Scope == "global"), cancellationToken);
    }

    public Task<byte[]> ReadFileAsync(AgentSkillFileEntity file, CancellationToken cancellationToken) => storage.ReadAsync(file.StorageKey, cancellationToken);

    public async Task<AgentSkillLeaseSet> PrepareAsync(string scope, IReadOnlyCollection<string>? selected, CancellationToken cancellationToken)
    {
        var bundle = new AgentSkillLeaseSet();
        try
        {
            if (selected is { Count: 0 })
            {
                return bundle;
            }
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            // Hold definition/file read locks until the cache is ready, so replacement and
            // version deletion cannot remove storage objects during materialization.
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            var query = db.AgentSkillDefinitions.AsNoTracking()
                .Include(x => x.Files.Where(file => file.Version == file.Definition.ActiveVersion))
                .Where(x => x.Scope == "global" || x.Scope == scope);
            if (selected is not null)
            {
                query = query.Where(x => selected.Contains(x.Name));
            }
            foreach (var skill in await query.ToArrayAsync(cancellationToken))
            {
                bundle.Add(await fileCache.AcquireAsync(skill, cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
            return bundle;
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public static Dictionary<string, byte[]> ReadArchive(byte[] bytes)
    {
        if (bytes.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException("The ZIP must be no larger than 10 MB.");
        }
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 200)
        {
            throw new InvalidDataException("The ZIP must contain at most 200 entries.");
        }
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            var segments = path.TrimEnd('/').Split('/');
            if (path.Length > 240 || path.StartsWith('/') || segments.Any(x => !IsSafeSegment(x)) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException("ZIP entries must be regular files with safe relative paths; links and traversal are not allowed.");
            }
            if (path.EndsWith('/'))
            {
                continue;
            }
            total += entry.Length;
            if (total > MaxExpandedBytes || entry.Length > MaxExpandedBytes)
            {
                throw new InvalidDataException("The extracted ZIP must be no larger than 20 MB.");
            }
            using var stream = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (output.Length + read > entry.Length)
                {
                    throw new InvalidDataException("The ZIP has invalid entry sizes.");
                }
                output.Write(buffer, 0, read);
            }
            if (!files.TryAdd(path, output.ToArray()))
            {
                throw new InvalidDataException("The ZIP contains duplicate file paths.");
            }
        }
        var manifests = files.Keys.Where(x => System.IO.Path.GetFileName(x) == "SKILL.md").ToArray();
        if (manifests.Length != 1)
        {
            throw new InvalidDataException("Include exactly one SKILL.md at the ZIP root or inside a single skill folder.");
        }
        var prefix = manifests[0][..^"SKILL.md".Length];
        if (files.Keys.Any(x => !x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("All files must be inside the skill folder containing SKILL.md.");
        }
        foreach (var file in files.Keys)
        {
            var parent = file.LastIndexOf('/');
            while (parent >= 0)
            {
                if (files.ContainsKey(file[..parent]))
                {
                    throw new InvalidDataException("A ZIP path cannot be both a file and a directory.");
                }
                parent = file.LastIndexOf('/', parent - 1);
            }
        }
        return files.ToDictionary(x => x.Key[prefix.Length..], x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSafeSegment(string segment) => segment is not ("" or "." or "..") &&
        !segment.EndsWith('.') && !segment.EndsWith(' ') && !segment.Any(char.IsControl) &&
        segment.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) < 0 &&
        !Regex.IsMatch(segment.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);

    internal static void WriteFiles(string root, Dictionary<string, byte[]> files)
    {
        foreach (var (relative, bytes) in files)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            if (!path.StartsWith(System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid skill file path.");
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }
}

/// <summary>Owned temporary directory for upload validation or the host's skill cache.</summary>
public sealed class AgentSkillDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharepoint-skills", Guid.NewGuid().ToString("N"));

    public AgentSkillDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
