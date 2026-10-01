namespace SharePointAgent.Persistence;

public sealed class SignatureRequestEntity
{
    public Guid Id { get; set; }

    public Guid AttachmentFileId { get; set; }

    public Guid CreatedById { get; set; }

    public Guid ClientRequestId { get; set; }

    public string Provider { get; set; } = "";

    public string? ExternalId { get; set; }

    public string Subject { get; set; } = "";

    public string RecipientsJson { get; set; } = "[]";

    public string Status { get; set; } = "Creating";

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
