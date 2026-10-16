using System.Net;
using System.Text.Json;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.Workspaces;

public sealed partial class AcaSandboxProvisioner
{
    internal async Task<SandboxResource> InspectAsync(string id, CancellationToken cancellationToken) =>
        await GetSandboxAsync(id, cancellationToken) ?? throw new KeyNotFoundException("The sandbox no longer exists.");

    internal async Task<SandboxBinding?> FindBindingAsync(SandboxResource sandbox, CancellationToken cancellationToken)
    {
        if (sandbox.Labels?.TryGetValue(ScopeLabel, out var scope) != true || string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var binding = await registry.GetAsync(scope, cancellationToken);
        return binding?.SandboxId == sandbox.Id ? binding with { Endpoint = PortUrl(sandbox) ?? binding.Endpoint } : null;
    }

    internal async Task ResumeAsync(string id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Settings.ProvisionTimeoutSeconds));
        var sandbox = await InspectAsync(id, timeout.Token);
        if (Resumable.Contains(sandbox.State ?? ""))
        {
            await SendAsync<JsonElement?>(HttpMethod.Post, $"{GroupPath}/sandboxes/{Uri.EscapeDataString(id)}/resume", null, timeout.Token);
        }

        // Lifecycle management also supports sandboxes without a SandboxHost port or stored binding.
        while (!string.Equals(sandbox.State, "Running", StringComparison.OrdinalIgnoreCase))
        {
            if (Terminal.Contains(sandbox.State ?? "") || string.Equals(sandbox.StateDetails?.StoppedReason, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentWorkspaceUnavailableException("This sandbox cannot be resumed in its current state.");
            }
            await Task.Delay(PollInterval, timeout.Token);
            sandbox = await InspectAsync(id, timeout.Token);
        }
    }

    internal async Task StopFromPlaygroundAsync(string id, CancellationToken cancellationToken)
    {
        await InspectAsync(id, cancellationToken);
        await SendAsync<JsonElement?>(HttpMethod.Post, $"{GroupPath}/sandboxes/{Uri.EscapeDataString(id)}/stop", null, cancellationToken);
    }

    // Unlike best-effort conversation cleanup, an operator delete must report failures and retain the
    // binding until Azure accepts deletion, so an unsuccessful request remains retryable.
    internal async Task DeleteFromPlaygroundAsync(string id, CancellationToken cancellationToken)
    {
        var sandbox = await GetSandboxAsync(id, cancellationToken);
        using var request = await CreateRequestAsync(HttpMethod.Delete,
            $"{GroupPath}/sandboxes/{Uri.EscapeDataString(id)}", null, cancellationToken);
        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            EnsureSuccess(response, "delete");
        }

        if (sandbox?.Labels?.TryGetValue(ScopeLabel, out var scope) == true)
        {
            var binding = await registry.GetAsync(scope, cancellationToken);
            if (binding?.SandboxId == id)
            {
                await registry.DeleteBindingAsync(scope, cancellationToken);
            }
        }
    }
}
