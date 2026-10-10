namespace SharePointAgent.Persistence;

/// <summary>
/// A named group of conversations that share one Foundry sandbox. The session binding lives here
/// instead of on each conversation, so a file downloaded or edited in one conversation is still on
/// disk in the next one opened from the same workspace.
/// </summary>
public sealed class ChatWorkspaceEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>
    /// Rules every conversation in this workspace works under, added to its agent's instructions.
    /// Null or empty leaves the agent's own instructions untouched.
    /// </summary>
    public string? Instructions { get; set; }

    public Guid? CreatedById { get; set; }

    public string? FoundryEndpoint { get; set; }

    public string? FoundrySessionId { get; set; }

    /// <summary>
    /// The dynamic session the API's own agent uses for this workspace (AgentWorkspace:Mode
    /// DynamicSessions). Independent of the Foundry session; cleared to start a fresh environment.
    /// </summary>
    public string? DynamicSessionId { get; set; }

    /// <summary>
    /// The sandbox the API's own agent uses for this workspace (AgentWorkspace:Mode Sandboxes). Its
    /// address and key stay in the private workspace storage; this records which sandbox it is.
    /// </summary>
    public string? SandboxId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<ChatConversationEntity> Conversations { get; set; } = [];
}
