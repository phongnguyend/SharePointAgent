namespace SharePointAgent.Persistence;

// Independent of messages: deletion and branching must never erase or duplicate billed usage.
public sealed class UserTokenUsageEntity
{
    public Guid QuestionId { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public Guid UserId { get; set; }
    public string? ModelId { get; set; }
    public int Month { get; set; }
    public int Day { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
}
