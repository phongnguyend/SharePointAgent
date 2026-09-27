namespace SharePointAgent.Persistence;

public sealed class WebhookSubscriptionEntity
{
    public Guid Id { get; set; }
    public string? GraphSubscriptionId { get; set; }
    public string Name { get; set; } = "";
    public string NotificationUrl { get; set; } = "";
    public string? ClientState { get; set; }
    public bool AutoRenewEnabled { get; set; }
    public int LifetimeDays { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
