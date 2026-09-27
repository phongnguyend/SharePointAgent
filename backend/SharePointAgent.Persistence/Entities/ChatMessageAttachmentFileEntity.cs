using SharePointAgent.Domain;

namespace SharePointAgent.Persistence;

public sealed class ChatMessageAttachmentFileEntity
{
    public Guid? CreatedById { get; set; }
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string BlobName { get; set; } = "";
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public UploadIndexStatus Status { get; set; }
    public int ChunkCount { get; set; }
    public long? EmbeddingTokenCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? IndexedAtUtc { get; set; }
    public Guid? ChatMessageAttachmentId { get; set; }
    public ChatMessageAttachmentEntity? ChatMessageAttachment { get; set; }
    public ICollection<ChatMessageAttachmentEntity> MessageAttachments { get; set; } = [];
}
