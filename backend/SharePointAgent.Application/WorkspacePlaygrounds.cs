namespace SharePointAgent.Application;

public sealed record WorkspacePlaygroundInstance(
    string Id, string State, bool CanAccessFiles, DateTimeOffset? CreatedAt = null, DateTimeOffset? ExpiresAt = null);

public sealed record WorkspacePlaygroundListing(
    bool Configured, string? Message, IReadOnlyList<WorkspacePlaygroundInstance> Instances, bool Truncated = false);

public interface IWorkspacePlayground
{
    Task<WorkspacePlaygroundListing> ListAsync(string provider, CancellationToken cancellationToken);

    Task<WorkspacePlaygroundInstance> CreateAsync(string provider, CancellationToken cancellationToken);

    Task ChangeStateAsync(string provider, string id, string action, CancellationToken cancellationToken);

    Task<IAgentWorkspace> OpenAsync(string provider, string id, CancellationToken cancellationToken);
}
