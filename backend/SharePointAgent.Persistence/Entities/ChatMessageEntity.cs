using SharePointAgent.Domain;

namespace SharePointAgent.Persistence;

public sealed class ChatMessageEntity
{
    public long EmbeddingTokenCount { get; set; }
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>
    /// Orders the turns within a conversation; timestamps alone would tie when a user message and its
    /// answer are written in the same instant.
    /// </summary>
    public int Sequence { get; set; }

    public ChatMessageRole Role { get; set; }
    public string Content { get; set; } = "";

    /// <summary>
    /// The retrieved documents behind an answer, serialized as JSON, or null when there were none. Kept
    /// as text rather than a mapped collection: citations are only ever read back whole, with the message.
    /// </summary>
    public string? CitationsJson { get; set; }

    /// <summary>Model usage for this response. User messages and historical rows have zeroes.</summary>
    public long InputTokenCount { get; set; }
    public long OutputTokenCount { get; set; }
    public long TotalTokenCount { get; set; }
    public string? ModelId { get; set; }

    public ChatFeedback? Feedback { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public ChatConversationEntity? Conversation { get; set; }
    public ICollection<ChatMessageAttachmentEntity> Attachments { get; set; } = [];
}
