using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatAgentSkillsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PythonScriptPreservesArgumentsAndReportsErrors(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-python-test-" + Guid.NewGuid());
        var skillPath = Path.Combine(root, "python-test");
        Directory.CreateDirectory(Path.Combine(skillPath, "scripts"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(skillPath, "SKILL.md"),
                "---\nname: python-test\ndescription: Test Python execution.\n---\nRun scripts/test.py.\n");
            await File.WriteAllTextAsync(Path.Combine(skillPath, "scripts", "test.py"),
                "import json, sys\nif sys.argv[1] == 'fail':\n    sys.stderr.write('python failure')\n    sys.exit(7)\nprint(json.dumps(sys.argv[1:], ensure_ascii=False))\n");
            using var source = new AgentFileSkillsSource(root, ChatAgentSkills.RunScriptAsync);
            var agent = new ChatClientAgent(Substitute.For<IChatClient>());
            var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, null), default);
            var skill = Assert.Single(skills);
            var script = await skill.GetScriptAsync("scripts/test.py", default);
            Assert.NotNull(script);
            string[] arguments = fail ? ["fail"] : ["spaces and quotes \"", "; echo unsafe", "Tiếng Việt"];
            if (fail)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => script.RunAsync(
                    skill, JsonSerializer.SerializeToElement(arguments), null, default));
                Assert.Contains("(7): python failure", error.Message);
            }
            else
            {
                var output = await script.RunAsync(skill, JsonSerializer.SerializeToElement(arguments), null, default);
                Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(Assert.IsType<string>(output)));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoversPackagedDnsSkillAndScript()
    {
        using var source = new AgentFileSkillsSource(ChatAgentSkills.DirectoryPath, ChatAgentSkills.RunScriptAsync);
        var agent = new ChatClientAgent(Substitute.For<IChatClient>());
        var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, null), default);
        Assert.Contains(skills, skill => skill.Frontmatter.Name == "dns-lookup");
        Assert.True(File.Exists(Path.Combine(ChatAgentSkills.DirectoryPath, "dns-lookup", "scripts", "resolve-dns.ps1")));
        Assert.NotNull(ChatAgentSkills.CreateProvider());
    }

    [Fact]
    public async Task ReferenceScriptRejectsInvalidDomainWithoutExecutingShellInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var source = new AgentFileSkillsSource(ChatAgentSkills.DirectoryPath, ChatAgentSkills.RunScriptAsync);
        var agent = new ChatClientAgent(Substitute.For<IChatClient>());
        var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, null), default);
        var skill = Assert.Single(skills, item => item.Frontmatter.Name == "dns-lookup");
        var script = await skill.GetScriptAsync("scripts/resolve-dns.ps1", default);
        Assert.NotNull(script);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => script.RunAsync(
            skill, JsonSerializer.SerializeToElement(new[] { "bad;domain" }), null, default));
        Assert.Contains("Invalid domain format", error.Message);
    }
}
