namespace SharePointAgent.Persistence;

// Usage survives removal of the related conversation or user.
public sealed class SystemOneUsageEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public int Day { get; set; }

    public int Month { get; set; }

    public Guid? UserId { get; set; }

    public Guid? ConversationId { get; set; }

    public Guid? QuestionId { get; set; }

    public string Provider { get; set; } = "Jev";

    public string ModelId { get; set; } = "";

    public string? TraceId { get; set; }

    public int QuestionCount { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long TotalTokens { get; set; }
}
