using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharePointAgent.Api;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AgentSkillTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/root.txt")]
    [InlineData("C:/file.txt")]
    [InlineData("scripts/../../outside.txt")]
    [InlineData("scripts/file.txt:stream")]
    [InlineData("scripts/CON.py")]
    public void RejectsUnsafeZipPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => AgentSkillStore.ReadArchive(Zip(("SKILL.md", Manifest("safe")), (path, "bad"))));
    }

    [Fact]
    public void NormalizesSingleFolderAndRejectsDuplicateOrMultipleSkills()
    {
        var files = AgentSkillStore.ReadArchive(Zip(("sample/SKILL.md", Manifest("sample")), ("sample/scripts/test.py", "print('ok')")));
        Assert.Equal(2, files.Count);
        Assert.Contains("scripts/test.py", files.Keys);
        Assert.Throws<InvalidDataException>(() => AgentSkillStore.ReadArchive(Zip(("SKILL.md", "a"), ("SKILL.md", "b"))));
        Assert.Throws<InvalidDataException>(() => AgentSkillStore.ReadArchive(Zip(("a/SKILL.md", "a"), ("b/SKILL.md", "b"))));
        Assert.Throws<InvalidDataException>(() => AgentSkillStore.ReadArchive(Zip(("a/SKILL.md", "a"), ("elsewhere.txt", "b"))));
        Assert.Throws<InvalidDataException>(() => AgentSkillStore.ReadArchive(Zip(("SKILL.md", Manifest("big")), ("big.txt", new string('x', AgentSkillStore.MaxExpandedBytes)))));
    }

    [Fact]
    public async Task UploadInspectionRuntimeIsolationAndDeletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var factory = new Factory(new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var parent = new AgentDefinitionEntity { Name = "Parent", ModelId = "model", Instructions = "test" };
        var other = new AgentDefinitionEntity { Name = "Other", ModelId = "model", Instructions = "test" };
        db.AgentDefinitions.AddRange(parent, other);
        await db.SaveChangesAsync();
        var child = new SubAgentDefinitionEntity { ParentAgentId = parent.Id, Name = "Child", ModelId = "model", Description = "test", Instructions = "test" };
        db.SubAgentDefinitions.Add(child);
        await db.SaveChangesAsync();
        var storage = new FakeAgentSkillFileStorage();
        using var cache = new AgentSkillFileCache(storage);
        var store = new AgentSkillStore(factory, storage, NullLogger<AgentSkillStore>.Instance, cache);
        await store.SynchronizeDefaultsAsync(default);
        var originalDefault = await db.AgentSkillDefinitions.FirstAsync(x => x.IsDefault);
        var createdAt = originalDefault.CreatedAtUtc;
        Assert.NotEqual(default, createdAt);
        Assert.Equal(createdAt, originalDefault.UpdatedAtUtc);
        // Force a refresh without changing bundled files to exercise the update path.
        originalDefault.ContentHash = "outdated";
        await db.SaveChangesAsync();
        await store.SynchronizeDefaultsAsync(default);
        await db.Entry(originalDefault).ReloadAsync();
        Assert.Equal(createdAt, originalDefault.CreatedAtUtc);
        Assert.True(originalDefault.UpdatedAtUtc >= createdAt);
        var originalUploads = storage.UploadCount;
        await store.SynchronizeDefaultsAsync(default);
        Assert.Equal(originalUploads, storage.UploadCount);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDbContextFactory<SharePointIndexDbContext>>(factory);
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.MapAgentSkillEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        const string path = "/api/agents/skills";
        async Task<AgentSkillInfo> Upload(string query, string name)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Zip(("SKILL.md", Manifest(name)), ("scripts/test.py", "print('hello')"))), "file", "skill.zip");
            using var response = await client.PostAsync(path + query, form);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<AgentSkillInfo>())!;
        }
        var global = await Upload("", "uploaded-global");
        Assert.Equal(1, global.VersionCount);
        var uploadedDefinition = await db.AgentSkillDefinitions.SingleAsync(x => x.Id == Guid.Parse(global.Id));
        Assert.NotEqual(default, uploadedDefinition.CreatedAtUtc);
        Assert.Equal(uploadedDefinition.CreatedAtUtc, uploadedDefinition.UpdatedAtUtc);
        var local = await Upload($"?agentId={parent.Id}", "parent-only");
        var nested = await Upload($"?agentId={parent.Id}&subAgentId={child.Id}", "child-only");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync("global", Zip(("SKILL.md", "x")), default));
        var parentScope = AgentSkillStore.Scope(parent.Id, null);
        var readsBeforeBrowse = storage.ReadCount;
        Assert.Contains(await store.ListAsync(parentScope, default), x => x.Name == local.Name);
        var fileList = await store.ListFilesAsync(parentScope, Guid.Parse(local.Id), default);
        Assert.Equal(2, fileList!.Count);
        Assert.Equal(readsBeforeBrowse, storage.ReadCount);
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(AgentSkillDefinitionEntity))!.GetProperties(), x => x.Name == "Archive");
        Assert.DoesNotContain(await store.ListAsync(AgentSkillStore.Scope(other.Id, null), default), x => x.Name == local.Name);
        Assert.DoesNotContain(await store.ListAsync(AgentSkillStore.Scope(parent.Id, child.Id), default), x => x.Name == local.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{local.Id}/files?agentId={other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}?agentId={other.Id}&subAgentId={child.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/{local.Id}/file?agentId={parent.Id}&path=scripts/test.py")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{local.Id}/file?agentId={parent.Id}&path=../outside")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{local.Id}/archive?agentId={other.Id}")).StatusCode);
        using (var download = await client.GetAsync($"{path}/{local.Id}/archive?agentId={parent.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
            var extracted = AgentSkillStore.ReadArchive(await download.Content.ReadAsByteArrayAsync());
            Assert.Equal(2, extracted.Count);
            Assert.Equal(Manifest("parent-only"), Encoding.UTF8.GetString(extracted["SKILL.md"]));
            Assert.Equal("print('hello')", Encoding.UTF8.GetString(extracted["scripts/test.py"]));
        }
        using (var snapshot = await store.PrepareAsync(parentScope, [local.Name, global.Name, nested.Name], default))
        {
            var discovered = (await Task.WhenAll(snapshot.Paths.Select(path => ChatAgentSkills.GetCatalogAsync(path, default)))).SelectMany(x => x);
            Assert.Equal(new[] { local.Name, global.Name }.Order(), discovered.Select(x => x.Name).Order());
        }
        var defaults = (await store.ListAsync("global", default)).Where(x => x.IsDefault).ToArray();
        Assert.NotEmpty(defaults);
        Assert.NotNull(await store.ListFilesAsync("global", Guid.Parse(defaults[0].Id), default));
        using (var snapshot = await store.PrepareAsync("global", [defaults[0].Name], default))
        {
            Assert.Equal(defaults[0].Name, Assert.Single(await ChatAgentSkills.GetCatalogAsync(Assert.Single(snapshot.Paths), default)).Name);
        }
        Assert.NotEqual(HttpStatusCode.NoContent, (await client.DeleteAsync($"{path}/{Uri.EscapeDataString(defaults[0].Id)}")).StatusCode);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync("global", Zip(("SKILL.md", Manifest(defaults[0].Name))), default));
        parent.Skills = [local.Name, global.Name];
        child.Skills = [global.Name];
        await db.SaveChangesAsync();
        var uploadedCreatedAt = uploadedDefinition.CreatedAtUtc;
        var replacement = Zip(("SKILL.md", Manifest(global.Name)), ("scripts/new.py", "print('new')"));
        using (var oldFiles = await store.PrepareAsync("global", [global.Name], default))
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(replacement), "file", "replacement.zip");
            using var response = await client.PutAsync($"{path}/{global.Id}", form);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(global.Id, (await response.Content.ReadFromJsonAsync<AgentSkillInfo>())!.Id);
            using var newFiles = await store.PrepareAsync("global", [global.Name], default);
            Assert.NotEqual(oldFiles.Paths[0], newFiles.Paths[0]);
            Assert.True(File.Exists(Path.Combine(oldFiles.Paths[0], "scripts", "test.py")));
            Assert.Equal("print('new')", await File.ReadAllTextAsync(Path.Combine(newFiles.Paths[0], "scripts", "new.py")));
        }
        await db.Entry(uploadedDefinition).ReloadAsync();
        Assert.Equal(uploadedCreatedAt, uploadedDefinition.CreatedAtUtc);
        await db.Entry(parent).ReloadAsync();
        await db.Entry(child).ReloadAsync();
        Assert.Contains(global.Name, parent.Skills!);
        Assert.Contains(global.Name, child.Skills);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync("global", Zip(("SKILL.md", Manifest("renamed"))), Guid.Parse(global.Id), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync(parentScope, replacement, Guid.Parse(global.Id), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync("global", Zip(("SKILL.md", Manifest(defaults[0].Name))), Guid.Parse(defaults[0].Id), default));
        storage.FailOnUpload = storage.UploadCount + 2;
        await Assert.ThrowsAsync<IOException>(() => store.UploadAsync("global", Zip(("SKILL.md", Manifest(global.Name)), ("fail.py", "pass")), Guid.Parse(global.Id), default));
        storage.FailOnUpload = null;
        var retained = await store.GetFileAsync("global", Guid.Parse(global.Id), "scripts/new.py", default);
        Assert.NotNull(retained);
        Assert.Equal("print('new')", Encoding.UTF8.GetString(await store.ReadFileAsync(retained!, default)));
        var history = await store.ListVersionsAsync("global", Guid.Parse(global.Id), default);
        Assert.NotNull(history);
        Assert.Equal(new[] { 2, 1 }, history.Select(x => x.Version));
        Assert.True(history[0].IsActive);
        Assert.Equal(2, (await store.ListAsync("global", default)).Single(x => Guid.Parse(x.Id) == Guid.Parse(global.Id)).VersionCount);
        Assert.False(history[1].IsActive);
        await store.UploadAsync("global", replacement, Guid.Parse(global.Id), default);
        Assert.Equal(2, (await store.ListVersionsAsync("global", Guid.Parse(global.Id), default))!.Count);
        using (var archived = await client.GetAsync($"{path}/{global.Id}/archive?version=1"))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
            var oldContent = AgentSkillStore.ReadArchive(await archived.Content.ReadAsByteArrayAsync());
            Assert.Equal("print('hello')", Encoding.UTF8.GetString(oldContent["scripts/test.py"]));
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/{global.Id}/file?version=1&path=scripts/test.py")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{global.Id}/file?path=scripts/test.py")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{local.Id}/versions?agentId={other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{path}/{global.Id}/versions/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{path}/{global.Id}/versions/1?agentId={parent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{path}/{defaults[0].Id}/versions/1")).StatusCode);
        var oldFile = await store.GetFileAsync("global", Guid.Parse(global.Id), "scripts/test.py", 1, default);
        Assert.NotNull(oldFile);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"{path}/{global.Id}/versions/1/active?agentId={parent.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"{path}/{defaults[0].Id}/versions/1/active", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"{path}/{global.Id}/versions/999/active", null)).StatusCode);
        using (var running = await store.PrepareAsync("global", [global.Name], default))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{path}/{global.Id}/versions/1/active", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{path}/{global.Id}/versions/1/active", null)).StatusCode);
            Assert.Equal(2, (await store.ListVersionsAsync("global", Guid.Parse(global.Id), default))!.Count);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{path}/{global.Id}/versions/1")).StatusCode);
            using var rolledBack = await store.PrepareAsync("global", [global.Name], default);
            Assert.Equal("print('hello')", await File.ReadAllTextAsync(Path.Combine(rolledBack.Paths[0], "scripts", "test.py")));
            Assert.Equal("print('new')", await File.ReadAllTextAsync(Path.Combine(running.Paths[0], "scripts", "new.py")));
        }
        for (var expectedVersion = 3; expectedVersion <= 4; expectedVersion++)
        {
            await store.UploadAsync("global", Zip(("SKILL.md", Manifest(global.Name)), ("next.py", "new revision")), Guid.Parse(global.Id), default);
            var latest = (await store.ListVersionsAsync("global", Guid.Parse(global.Id), default))!.Single(x => x.IsActive);
            Assert.Equal(expectedVersion, latest.Version);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{path}/{global.Id}/versions/2/active", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{path}/{global.Id}/versions/{expectedVersion}")).StatusCode);
        }
        await db.Entry(parent).ReloadAsync();
        await db.Entry(child).ReloadAsync();
        Assert.Contains(global.Name, parent.Skills!);
        Assert.Contains(global.Name, child.Skills);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{path}/{global.Id}/versions/1")).StatusCode);
        Assert.False(storage.Files.ContainsKey(oldFile!.StorageKey));
        Assert.Single((await store.ListVersionsAsync("global", Guid.Parse(global.Id), default))!);
        Assert.Equal(1, (await store.ListAsync("global", default)).Single(x => Guid.Parse(x.Id) == Guid.Parse(global.Id)).VersionCount);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{global.Id}/archive?version=1")).StatusCode);
        Assert.NotNull(await store.GetFileAsync("global", Guid.Parse(global.Id), "scripts/new.py", default));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{path}/{global.Id}?agentId={parent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{path}/{global.Id}")).StatusCode);
        await db.Entry(parent).ReloadAsync();
        await db.Entry(child).ReloadAsync();
        Assert.Equal([local.Name], parent.Skills!);
        Assert.Empty(child.Skills);
        Assert.False(await db.AgentSkillFiles.AnyAsync(x => x.AgentSkillDefinitionId == Guid.Parse(global.Id)));
        Assert.DoesNotContain(storage.Files.Keys, x => x.StartsWith($"agent-skills/{Guid.Parse(global.Id):N}/"));
        var blobCount = storage.Files.Count;
        storage.FailOnUpload = storage.UploadCount + 2;
        await Assert.ThrowsAsync<IOException>(() => store.UploadAsync("global", Zip(("SKILL.md", Manifest("failed-upload")), ("script.py", "pass")), default));
        Assert.Equal(blobCount, storage.Files.Count);
        Assert.False(await db.AgentSkillDefinitions.AnyAsync(x => x.Name == "failed-upload"));
    }

    [Theory]
    [InlineData(AppRoles.User, "GET", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "GET", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "PUT", false)]
    [InlineData(AppRoles.GlobalAdmin, "PUT", true)]
    [InlineData(AppRoles.GlobalAdmin, "POST", true)]
    public void RequiresAdministrativeAccess(string role, string method, bool allowed)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], method, "/api/agents/skills"));
    }

    private static string Manifest(string name) => $"---\nname: {name}\ndescription: Test skill\n---\nInstructions for testing.";

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private sealed class Factory(DbContextOptions<SharePointIndexDbContext> options) : IDbContextFactory<SharePointIndexDbContext>
    {
        public SharePointIndexDbContext CreateDbContext() => new(options);
    }
}
