namespace SharePointAgent.Persistence;

// No cascading foreign keys: deleting a conversation must not erase billed usage.
public sealed class ImageDescriptionTokenUsageEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? UserId { get; set; }

    public Guid ConversationId { get; set; }

    public Guid QuestionId { get; set; }

    public Guid AttachmentId { get; set; }

    public string ModelId { get; set; } = "";

    public string? SystemPrompt { get; set; }

    public string? Prompt { get; set; }

    public string? Description { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? TotalTokens { get; set; }
}
