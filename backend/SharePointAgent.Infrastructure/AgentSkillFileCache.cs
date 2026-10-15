using SharePointAgent.Persistence;
using SharePointAgent.Application;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace SharePointAgent.Infrastructure;

/// <summary>Shared versioned folders, pinned while agent turns use them.</summary>
public sealed class AgentSkillFileCache : IDisposable
{
    private readonly IAgentSkillFileStorage _storage;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxAge;
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private readonly AgentSkillDirectory _root = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(Guid Id, string Hash, Guid Generation), Entry> _entries = [];
    private long _size;
    private bool _disposed;

    public AgentSkillFileCache(IAgentSkillFileStorage storage, IOptions<AgentSkillCacheOptions>? options = null, TimeProvider? timeProvider = null)
    {
        _storage = storage;
        _clock = timeProvider ?? TimeProvider.System;
        var settings = options?.Value ?? new AgentSkillCacheOptions();
        if (settings.MaxAgeDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Cache age must be between 1 and 3650 days.");
        }
        _maxAge = TimeSpan.FromDays(settings.MaxAgeDays);
        File.WriteAllText(Path.Combine(_root.Path, "cache-directory.json"), JsonSerializer.Serialize(new
        {
            CreatedAtUtc = _clock.GetUtcNow(),
            ProcessId = Environment.ProcessId,
            settings.MaxAgeDays
        }));
    }

    public async Task<AgentSkillFileLease> AcquireAsync(AgentSkillDefinitionEntity skill, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _clock.GetUtcNow();
            var existing = _entries.FirstOrDefault(x => x.Key.Id == skill.Id && x.Key.Hash == skill.ContentHash &&
                !x.Value.Obsolete && now - x.Value.CreatedAtUtc < _maxAge);
            var entry = existing.Value;
            var key = existing.Key;
            if (entry is null)
            {
                key = (skill.Id, skill.ContentHash, Guid.NewGuid());
                var directory = Path.Combine(_root.Path, $"{now:yyyyMMddTHHmmssZ}-{key.Generation:N}");
                try
                {
                    var files = new Dictionary<string, byte[]>();
                    foreach (var file in skill.Files)
                    {
                        files.Add(file.Path, await _storage.ReadAsync(file.StorageKey, cancellationToken));
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var skillPath = Path.Combine(directory, skill.Name);
                    AgentSkillStore.WriteFiles(skillPath, files);
                    entry = new Entry(directory, skillPath, files.Values.Sum(x => x.LongLength), now);
                    File.WriteAllText(Path.Combine(directory, "cache-entry.json"), JsonSerializer.Serialize(new
                    {
                        SkillId = skill.Id,
                        skill.Name,
                        skill.ContentHash,
                        entry.CreatedAtUtc,
                        ExpiresAtUtc = entry.CreatedAtUtc + _maxAge
                    }));
                    _entries.Add(key, entry);
                    _size += entry.Size;
                }
                catch
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    throw;
                }
            }
            entry.LastUsed = now;
            entry.Users++;
            foreach (var obsolete in _entries.Where(x => x.Key.Id == skill.Id && x.Key != key).Select(x => x.Key).ToArray())
            {
                _entries[obsolete].Obsolete = true;
            }
            Trim();
            return new AgentSkillFileLease(entry.SkillPath, skill.Name, () => Release(key));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Release((Guid Id, string Hash, Guid Generation) key)
    {
        _gate.Wait();
        try
        {
            _entries[key].Users--;
            Trim();
            if (_disposed && _entries.Count == 0)
            {
                _root.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Trim()
    {
        foreach (var item in _entries.Where(x => x.Value.Users == 0).OrderBy(x => x.Value.LastUsed).ToArray())
        {
            if (_disposed || item.Value.Obsolete || _size > MaxCacheBytes)
            {
                Remove(item.Key);
            }
        }
    }

    private void Remove((Guid Id, string Hash, Guid Generation) key)
    {
        var entry = _entries[key];
        Directory.Delete(entry.Directory, recursive: true);
        _entries.Remove(key);
        _size -= entry.Size;
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Trim();
            if (_entries.Count == 0)
            {
                _root.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Entry(string directory, string skillPath, long size, DateTimeOffset createdAtUtc)
    {
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public string Directory { get; } = directory;

        public long Size { get; } = size;

        public string SkillPath { get; } = skillPath;

        public int Users { get; set; }

        public bool Obsolete { get; set; }

        public DateTimeOffset LastUsed { get; set; }
    }
}
