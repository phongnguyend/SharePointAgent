namespace SharePointAgent.Persistence;

/// <summary>One text analysis request. Does not store submitted text or credentials.</summary>
public sealed class ContentSafetyUsageEntity
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid? AttachmentId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? QuestionId { get; set; }
    public string Operation { get; set; } = "UserMessage";
    public string ApiVersion { get; set; } = "2024-09-01";
    public string Status { get; set; } = "Started";
    public int CharacterCount { get; set; }
    public int EstimatedTextRecords { get; set; }
    public int? HttpStatusCode { get; set; }
    public long DurationMs { get; set; }
    public int? HateSeverity { get; set; }
    public int? SexualSeverity { get; set; }
    public int? ViolenceSeverity { get; set; }
    public int? SelfHarmSeverity { get; set; }
    public int HateThreshold { get; set; }
    public int SexualThreshold { get; set; }
    public int ViolenceThreshold { get; set; }
    public int SelfHarmThreshold { get; set; }
    public string? ErrorCode { get; set; }
    public string? TraceId { get; set; }
}
