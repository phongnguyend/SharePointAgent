namespace SharePointAgent.Persistence;

// One row per dictation transcribed for the chat composer. No foreign keys, so billed usage survives
// user changes. The transcript itself is not stored: it only becomes a message if the user sends it.
public sealed class TranscriptionTokenUsageEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public int Day { get; set; }

    public int Month { get; set; }

    public Guid UserId { get; set; }

    public string ModelId { get; set; } = "";

    public long AudioBytes { get; set; }

    public double? DurationSeconds { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? TotalTokens { get; set; }
}
