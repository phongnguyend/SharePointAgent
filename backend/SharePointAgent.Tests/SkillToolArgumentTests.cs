using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Tests;

public sealed class SkillToolArgumentTests
{
    /// <summary>
    /// The usage ledger reads the skill name and script out of each call's arguments, because the skill
    /// tools are named after the operation rather than the skill. Those argument names belong to the
    /// agent framework, and a rename there would silently leave the ledger's Skill and Script columns
    /// empty rather than fail anything — so they are pinned against the schemas the tools actually
    /// publish, taken from the pipeline the agent really builds.
    /// </summary>
    [Fact]
    public async Task SkillToolArgumentNamesAreUnchanged()
    {
        var probe = new ToolCapturingChatClient();
        var agent = new ChatClientAgent(probe, new ChatClientAgentOptions
        {
            AIContextProviders = [ChatAgentSkills.CreateProvider()],
            ChatOptions = new ChatOptions { Instructions = "Answer the question." }
        })
            .AsBuilder()
            .UseToolApproval(new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule]
            })
            .Build();

        await foreach (var _ in agent.RunStreamingAsync("What can you do?"))
        {
        }

        var tools = (probe.Tools ?? []).OfType<AIFunction>().ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        Assert.Contains("skillName", Parameters(tools, AgentSkillsProvider.LoadSkillToolName));
        Assert.Contains("skillName", Parameters(tools, AgentSkillsProvider.ReadSkillResourceToolName));

        var runScript = Parameters(tools, AgentSkillsProvider.RunSkillScriptToolName);
        Assert.Contains("skillName", runScript);
        Assert.Contains("scriptName", runScript);
    }

    private static string[] Parameters(IReadOnlyDictionary<string, AIFunction> tools, string name)
    {
        var tool = Assert.Contains(name, tools);
        using var schema = JsonDocument.Parse(tool.JsonSchema.GetRawText());
        return [.. schema.RootElement.GetProperty("properties").EnumerateObject().Select(x => x.Name)];
    }

    /// <summary>Answers immediately, keeping the tools the skills provider supplied for the first call.</summary>
    private sealed class ToolCapturingChatClient : IChatClient
    {
        public IList<AITool>? Tools { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Tools ??= options?.Tools;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I can search documents.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Tools ??= options?.Tools;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "I can search documents.");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
