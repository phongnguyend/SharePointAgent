namespace SharePointAgent.Persistence;

public sealed class ChatConversationEntity
{
    public long EmbeddingTokenCount { get; set; }
    public Guid? CreatedById { get; set; }
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? UserId { get; set; }
    public Guid? AgentId { get; set; }
    public string? FoundryEndpoint { get; set; }
    public string? FoundrySessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long InputTokenCount { get; set; }
    public long OutputTokenCount { get; set; }
    public long TotalTokenCount { get; set; }

    public ICollection<ChatMessageEntity> Messages { get; set; } = [];
    public AgentDefinitionEntity? Agent { get; set; }
}
