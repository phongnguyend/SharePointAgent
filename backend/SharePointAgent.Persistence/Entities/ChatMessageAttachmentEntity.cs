namespace SharePointAgent.Persistence;

public sealed class ChatMessageAttachmentEntity
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid AttachmentFileId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ChatMessageEntity? Message { get; set; }
    public ChatMessageAttachmentFileEntity? AttachmentFile { get; set; }
}
