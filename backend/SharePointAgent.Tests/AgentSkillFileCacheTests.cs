using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AgentSkillFileCacheTests
{
    [Fact]
    public async Task RefreshesAtConfiguredAgeAndRecordsDirectoryCreationDates()
    {
        var storage = new FakeAgentSkillFileStorage();
        storage.Files.Add("manifest", Encoding.UTF8.GetBytes("original"));
        storage.Files.Add("script", Encoding.UTF8.GetBytes("script"));
        var start = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var clock = new CacheClock(start);
        using var cache = new AgentSkillFileCache(storage, Options.Create(new AgentSkillCacheOptions { MaxAgeDays = 2 }), clock);
        using var first = await cache.AcquireAsync(Skill(), default);
        var directory = Directory.GetParent(first.Path)!.FullName;
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "cache-entry.json")));
        Assert.Equal(start, metadata.RootElement.GetProperty("CreatedAtUtc").GetDateTimeOffset());
        Assert.Equal(start.AddDays(2), metadata.RootElement.GetProperty("ExpiresAtUtc").GetDateTimeOffset());
        using var rootMetadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Directory.GetParent(directory)!.FullName, "cache-directory.json")));
        Assert.Equal(start, rootMetadata.RootElement.GetProperty("CreatedAtUtc").GetDateTimeOffset());
        clock.Now = start.AddDays(2).AddTicks(-1);
        using var fresh = await cache.AcquireAsync(Skill(), default);
        Assert.Equal(first.Path, fresh.Path);
        Assert.Equal(2, storage.ReadCount);
        clock.Now = start.AddDays(2);
        using var renewed = await cache.AcquireAsync(Skill(), default);
        Assert.NotEqual(first.Path, renewed.Path);
        Assert.Equal(4, storage.ReadCount);
        Assert.True(Directory.Exists(first.Path));
        first.Dispose();
        Assert.True(Directory.Exists(fresh.Path));
        fresh.Dispose();
        Assert.False(Directory.Exists(first.Path));
        using var reused = await cache.AcquireAsync(Skill(), default);
        Assert.Equal(renewed.Path, reused.Path);
        Assert.Equal(4, storage.ReadCount);
    }

    private sealed class CacheClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ReusesFilesAcrossConcurrentTurnsAndRefreshesOnlyChangedVersions()
    {
        var storage = new FakeAgentSkillFileStorage();
        storage.Files.Add("manifest", Encoding.UTF8.GetBytes("original"));
        storage.Files.Add("script", Encoding.UTF8.GetBytes("script"));
        using var cache = new AgentSkillFileCache(storage);
        var skill = Skill();
        var leases = await Task.WhenAll(cache.AcquireAsync(skill, default), cache.AcquireAsync(skill, default));
        using var first = leases[0];
        using var second = leases[1];
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(2, storage.ReadCount);
        storage.Files["manifest"] = Encoding.UTF8.GetBytes("updated");
        skill.ContentHash = "version-two";
        using var updated = await cache.AcquireAsync(skill, default);
        Assert.NotEqual(first.Path, updated.Path);
        Assert.Equal(4, storage.ReadCount);
        Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(updated.Path, "SKILL.md")));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(second.Path, "SKILL.md")));
        first.Dispose();
        Assert.True(Directory.Exists(second.Path));
        second.Dispose();
        Assert.False(Directory.Exists(second.Path));
        using var repeated = await cache.AcquireAsync(skill, default);
        Assert.Equal(updated.Path, repeated.Path);
        Assert.Equal(4, storage.ReadCount);
        cache.Dispose();
        Assert.True(Directory.Exists(updated.Path));
        updated.Dispose();
        Assert.True(Directory.Exists(repeated.Path));
        repeated.Dispose();
        Assert.False(Directory.Exists(repeated.Path));
    }

    [Fact]
    public async Task FailedDownloadsAreNotPublishedAndCanBeRetried()
    {
        var storage = new FakeAgentSkillFileStorage { FailOnRead = 2 };
        storage.Files.Add("manifest", Encoding.UTF8.GetBytes("original"));
        storage.Files.Add("script", Encoding.UTF8.GetBytes("script"));
        using var cache = new AgentSkillFileCache(storage);
        await Assert.ThrowsAsync<IOException>(() => cache.AcquireAsync(Skill(), default));
        using var target = await cache.AcquireAsync(Skill(), default);
        Assert.Equal(4, storage.ReadCount);
        Assert.Equal(2, Directory.EnumerateFiles(target.Path, "*", SearchOption.AllDirectories).Count());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquireAsync(Skill(), cancelled.Token));
        Assert.Equal(4, storage.ReadCount);
    }

    private static AgentSkillDefinitionEntity Skill() => new()
    {
        Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Name = "sample",
        ContentHash = "version-one",
        Files =
        [
            new AgentSkillFileEntity { Path = "SKILL.md", StorageKey = "manifest" },
            new AgentSkillFileEntity { Path = "scripts/test.py", StorageKey = "script" }
        ]
    };
}
