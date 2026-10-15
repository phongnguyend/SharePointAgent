using Microsoft.Extensions.Hosting;

namespace SharePointAgent.Infrastructure;

public sealed class AgentSkillDefaultsInitializer(AgentSkillStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.SynchronizeDefaultsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
