using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using SharePointAgent.Persistence.Repositories;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SubAgentTests
{
    [Fact]
    public async Task CrudIsParentScopedAndChildrenAreNotChatAgents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var parent = new AgentDefinitionEntity { Name = "Parent", ModelId = "test", Instructions = "Parent instructions" };
        var other = new AgentDefinitionEntity { Name = "Other", ModelId = "test", Instructions = "Other instructions" };
        db.AgentDefinitions.AddRange(parent, other);
        await db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDbContextFactory<SharePointIndexDbContext>>(factory);
        await using var app = builder.Build();
        app.MapSubAgentEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var path = $"/api/agents/{parent.Id}/sub-agents";
        var body = new SubAgentEndpoints.SubAgentRequest("Reviewer", "Review contracts", "specialist", "Find risks", [], []);
        var created = await client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var child = (await created.Content.ReadFromJsonAsync<SubAgentDefinitionEntity>())!;
        Assert.NotEqual(Guid.Empty, child.Id);
        Assert.Equal(parent.Id, child.ParentAgentId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/agents/{other.Id}/sub-agents/{child.Id}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/agents/{other.Id}/sub-agents/{child.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{path}/{child.Id}", body with { Tools = ["unknown_tool"] })).StatusCode);
        var updated = await client.PutAsJsonAsync($"{path}/{child.Id}", body with { Instructions = "Updated", Tools = [ChatAgentToolNames.ReadText] });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var list = (await client.GetFromJsonAsync<SubAgentDefinitionEntity[]>(path))!;
        Assert.Equal("Updated", Assert.Single(list).Instructions);
        Assert.Equal([ChatAgentToolNames.ReadText], list[0].Tools);
        var repository = new AgentRepository(factory, Options.Create(new OpenAiOptions()));
        Assert.Null(await repository.GetAsync(child.Id, default));
        Assert.Equal(2, (await repository.ListAsync(default)).Count);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{path}/{child.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<SubAgentDefinitionEntity[]>(path))!);
    }

    [Theory]
    [InlineData(AppRoles.User, "GET", false)]
    [InlineData(AppRoles.User, "POST", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "GET", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "PUT", false)]
    [InlineData(AppRoles.GlobalAdmin, "DELETE", true)]
    public void ConfigurationUsesAdministrationPermissions(string role, string method, bool allowed)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], method, $"/api/agents/{Guid.NewGuid()}/sub-agents/{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task DelegationUsesOwnInstructionsAndSelectedToolsAndRecordsUsage()
    {
        var probe = new ProbeClient();
        var reports = new List<ChatRequestUsage>();
        using var runner = new SubAgentTools(model =>
        {
            Assert.Equal("specialist", model);
            return probe;
        }, [AIFunctionFactory.Create(() => "read", "read_text"), AIFunctionFactory.Create(() => "write", "write_text")],
            Substitute.For<IAgentWorkspace>(), 60, (model, usage, token) =>
            {
                reports.Add(usage);
                return ValueTask.CompletedTask;
            }, (_, _) => ValueTask.CompletedTask, NullLogger.Instance);
        var definition = new SubAgentDefinitionEntity
        {
            Name = "Reviewer", ModelId = "specialist", Instructions = "Find contract risks", Tools = ["read_text"], Skills = []
        };
        var tool = runner.CreateTool(definition);
        Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("query", out _));
        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("task", out _));
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "Review contract.txt" });
        Assert.Contains("Reviewed", result!.ToString());
        Assert.Equal(["read_text"], probe.Tools.Select(x => x.Name));
        Assert.Contains("Find contract risks", probe.Instructions);
        Assert.Contains("Review contract.txt", probe.Text);
        Assert.Equal(13, runner.InputTokens);
        Assert.Equal(3, runner.OutputTokens);
        Assert.Equal(16, runner.TotalTokens);
        Assert.Single(reports);
        for (var i = 0; i < 7; i++)
        {
            await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "Review" });
            Assert.DoesNotContain("Review contract.txt", probe.Text);
        }
        var limited = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "Review" });
        Assert.Contains("limit", limited!.ToString());
        Assert.Equal(8, reports.Count);
    }

    [Fact]
    public async Task OnlyAssignedSkillsAreAdvertisedToChild()
    {
        var probe = new ProbeClient();
        using var runner = new SubAgentTools(_ => probe, [], Substitute.For<IAgentWorkspace>(), 60,
            (_, _, _) => ValueTask.CompletedTask, (_, _) => ValueTask.CompletedTask, NullLogger.Instance);
        var tool = runner.CreateTool(new SubAgentDefinitionEntity
        {
            Name = "DNS", ModelId = "test", Instructions = "Use the assigned skill", Skills = ["dns-lookup"]
        });
        await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "Describe your skills" });
        Assert.Contains("dns-lookup", probe.Instructions + probe.Text);
        var catalog = await ChatAgentSkills.GetCatalogAsync(default);
        foreach (var skill in catalog.Where(x => x.Name != "dns-lookup"))
        {
            Assert.DoesNotContain(skill.Name, probe.Instructions + probe.Text);
        }
    }

    private sealed class Factory(DbContextOptions<SharePointIndexDbContext> options) : IDbContextFactory<SharePointIndexDbContext>
    {
        public SharePointIndexDbContext CreateDbContext() => new(options);
    }

    private sealed class ProbeClient(string answer = "Reviewed") : IChatClient
    {
        public IList<AITool> Tools { get; private set; } = [];

        public string Instructions { get; private set; } = "";

        public string Text { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tools = options?.Tools ?? [];
            Instructions = options?.Instructions ?? "";
            Text = string.Join("\n", messages.Select(x => x.Text));
            return Task.FromResult(new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, answer))
            {
                Usage = new UsageDetails { InputTokenCount = 13, OutputTokenCount = 3, TotalTokenCount = 16 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tools = options?.Tools ?? [];
            Instructions = options?.Instructions ?? "";
            Text = string.Join("\n", messages.Select(x => x.Text));
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Reviewed")
            {
                Contents = [new TextContent("Reviewed"), new UsageContent(new UsageDetails { InputTokenCount = 13, OutputTokenCount = 3, TotalTokenCount = 16 })]
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type type, object? key = null) => null;

        public void Dispose()
        {
        }
    }
}
