using Azure.AI.AgentServer.Invocations;
using SharePointAgent.Infrastructure;
using SharePointAgent.AgentHost;

InvocationsServer.Run<ChatAgentInvocation>(args: args, configure: builder =>
{
    // Keep the agent's working directory in the session filesystem, rather than the ephemeral /tmp,
    // so downloaded and edited files survive from one turn to the next.
    if (string.IsNullOrWhiteSpace(builder.Configuration["LocalWorkingDirectory:Directory"]))
    {
        var sessionHome = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        builder.Configuration["LocalWorkingDirectory:Directory"] = Path.Combine(sessionHome, "sharepoint-agent");
    }
    builder.Services.AddHostedChatAgentServices(builder.Configuration);
});

public partial class Program;
