namespace SharePointAgent.Persistence;

/// <summary>Append-only usage history. References deliberately have no cascading foreign keys.</summary>
public sealed class EmbeddingTokenUsageEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Operation { get; set; } = "";
    public string EmbeddingModelId { get; set; } = "";
    public string DeploymentId { get; set; } = "";
    public long? InputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? QuestionId { get; set; }
    public string? DriveId { get; set; }
    public string? FileId { get; set; }
    public Guid? AttachmentId { get; set; }
    public Guid? ScanId { get; set; }
    public int? ChunkNumber { get; set; }
    public string? TraceId { get; set; }
}
