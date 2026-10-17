using SharePointAgent.Domain;

namespace SharePointAgent.Application;

/// <summary>Workspace-only invocation; never calls the model or changes conversation history.</summary>
public sealed record FoundryWorkspaceRequest(
    string Operation, string? Path = null, string? Destination = null, string? Content = null,
    bool Overwrite = false, bool Recursive = false, IReadOnlyList<string>? Paths = null,
    WorkspaceFileChange? Change = null, WorkspaceExecution? Execution = null);
