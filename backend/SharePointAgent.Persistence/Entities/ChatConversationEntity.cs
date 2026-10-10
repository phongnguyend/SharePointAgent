using SharePointAgent.Application;

namespace SharePointAgent.Persistence;

public sealed class ChatConversationEntity
{
    public long EmbeddingTokenCount { get; set; }
    public Guid? CreatedById { get; set; }
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? UserId { get; set; }
    public Guid? AgentId { get; set; }

    /// <summary>
    /// The workspace whose sandbox this conversation shares, or null to keep its own. Set when the
    /// conversation is created; only deleting the workspace clears it afterwards.
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    public string? FoundryEndpoint { get; set; }
    public string? FoundrySessionId { get; set; }

    /// <summary>Where the API's own agent keeps files when the conversation is in no workspace; null uses the default.</summary>
    public AgentWorkspaceMode? WorkspaceMode { get; set; }

    /// <summary>The dynamic session the API's own agent uses when the conversation is in no workspace.</summary>
    public string? DynamicSessionId { get; set; }

    /// <summary>The sandbox the API's own agent uses when the conversation is in no workspace.</summary>
    public string? SandboxId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long InputTokenCount { get; set; }
    public long OutputTokenCount { get; set; }
    public long TotalTokenCount { get; set; }

    public ICollection<ChatMessageEntity> Messages { get; set; } = [];
    public AgentDefinitionEntity? Agent { get; set; }

    public ChatWorkspaceEntity? Workspace { get; set; }
}
