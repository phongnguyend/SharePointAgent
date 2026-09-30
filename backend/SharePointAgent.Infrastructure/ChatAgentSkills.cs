using Microsoft.Agents.AI;
using System.Diagnostics;
using System.Text.Json;

namespace SharePointAgent.Infrastructure;

public static class ChatAgentSkills
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "skills");

    public static async Task<IReadOnlyList<AgentCapability>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using var source = new AgentFileSkillsSource(DirectoryPath, RunScriptAsync);
        // Discovery needs an agent context but never invokes a model or runs a skill.
        var client = new OpenAI.Chat.ChatClient("catalog", new System.ClientModel.ApiKeyCredential("catalog-only"));
        var agent = new ChatClientAgent(Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(client));
        var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, null), cancellationToken);
        return skills.Select(skill => new AgentCapability(skill.Frontmatter.Name, skill.Frontmatter.Description))
            .OrderBy(skill => skill.Name, StringComparer.Ordinal).ToArray();
    }

    public static AgentSkillsProvider CreateProvider()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            throw new DirectoryNotFoundException($"Agent skills folder was not deployed: {DirectoryPath}");
        }
        return new AgentSkillsProvider(DirectoryPath, RunScriptAsync);
    }

    public static async Task<object?> RunScriptAsync(AgentFileSkill skill, AgentFileSkillScript script,
        JsonElement? arguments, IServiceProvider? serviceProvider, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(script.FullPath).ToLowerInvariant();
        var executable = extension switch
        {
            ".ps1" => OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            ".py" => OperatingSystem.IsWindows() ? "python.exe" : "python3",
            ".js" or ".mjs" or ".cjs" => OperatingSystem.IsWindows() ? "node.exe" : "node",
            _ => throw new ArgumentException("The skill runner supports PowerShell (.ps1), Python (.py) and Node.js (.js, .mjs, .cjs) scripts only."),
        };
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script.FullPath)!
        };
        if (extension == ".ps1")
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-File");
        }
        else if (extension == ".py")
        {
            start.ArgumentList.Add("-X");
            start.ArgumentList.Add("utf8");
            start.ArgumentList.Add("-u");
            start.StandardOutputEncoding = System.Text.Encoding.UTF8;
            start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }
        else
        {
            start.StandardOutputEncoding = System.Text.Encoding.UTF8;
            start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }
        start.ArgumentList.Add(script.FullPath);
        if (arguments is { ValueKind: JsonValueKind.Array } values)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Null)
                {
                    start.ArgumentList.Add(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString());
                }
            }
        }
        else if (arguments is { ValueKind: not JsonValueKind.Null })
        {
            throw new ArgumentException("Script arguments must be a JSON array.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Skill script failed ({process.ExitCode}): {stderr.Trim()}");
            }
            return stdout.Trim();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
    }
}
