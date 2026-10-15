using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using SharePointAgent.Persistence.Repositories;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AgentCapabilityTests
{
    [Fact]
    public async Task ApiPersistsExplicitSelectionsAndRejectsUnknownCapabilities()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var factory = new Factory(new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var options = Options.Create(new OpenAiOptions { ChatDeployment = "test" });
        var repository = new AgentRepository(factory, options);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAgentRepository>(repository);
        builder.Services.AddSingleton<IDbContextFactory<SharePointIndexDbContext>>(factory);
        builder.Services.AddSingleton<IAgentSkillFileStorage, FakeAgentSkillFileStorage>();
        builder.Services.AddSingleton<AgentSkillFileCache>();
        builder.Services.AddSingleton<AgentSkillStore>();
        builder.Services.AddSingleton<IOptions<OpenAiOptions>>(options);
        builder.Services.AddOptions<ChatAgentHostingOptions>();
        await using var app = builder.Build();
        app.MapAgentsEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var body = new AgentDefinitionRequest("Limited", "test", "Answer carefully", [ChatAgentToolNames.HttpGet], []);
        using var created = await client.PostAsJsonAsync("/api/agents", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var agent = (await created.Content.ReadFromJsonAsync<AgentDefinition>())!;
        Assert.Equal([ChatAgentToolNames.HttpGet], agent.Tools!);
        Assert.Empty(agent.Skills!);
        Assert.Equal([ChatAgentToolNames.HttpGet], (await repository.GetByNameAsync("Limited", default))!.Tools!);
        using var invalid = await client.PutAsJsonAsync($"/api/agents/{agent.Id}", body with { Tools = ["invented"] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidSkill = await client.PutAsJsonAsync($"/api/agents/{agent.Id}", body with { Skills = ["invented"] });
        Assert.Equal(HttpStatusCode.BadRequest, invalidSkill.StatusCode);
        using var cleared = await client.PutAsJsonAsync($"/api/agents/{agent.Id}", body with { Tools = [], Skills = [] });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var saved = (await client.GetFromJsonAsync<AgentDefinition>($"/api/agents/{agent.Id}"))!;
        Assert.Empty(saved.Tools!);
        Assert.Empty(saved.Skills!);
        Assert.Empty(Assert.Single(await repository.ListAsync(default)).Tools!);
    }

    [Fact]
    public async Task ExistingAgentsRetainAllCapabilitiesUntilExplicitlyChanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var factory = new Factory(new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var entity = new AgentDefinitionEntity { Name = "Existing", ModelId = "test", Instructions = "Original" };
        db.AgentDefinitions.Add(entity);
        await db.SaveChangesAsync();
        var repository = new AgentRepository(factory, Options.Create(new OpenAiOptions()));
        var original = (await repository.GetAsync(entity.Id, default))!;
        Assert.Null(original.Tools);
        Assert.Null(original.Skills);
        await repository.UpdateAsync(entity.Id, original.Name, original.ModelId, original.Instructions,
            [ChatAgentToolNames.ReadText, ChatAgentToolNames.ReadText], ["dns-lookup"], default);
        var updated = (await repository.GetAsync(entity.Id, default))!;
        Assert.Equal([ChatAgentToolNames.ReadText], updated.Tools!);
        Assert.Equal(["dns-lookup"], updated.Skills!);
    }

    private sealed class Factory(DbContextOptions<SharePointIndexDbContext> options) : IDbContextFactory<SharePointIndexDbContext>
    {
        public SharePointIndexDbContext CreateDbContext() => new(options);
    }
}
