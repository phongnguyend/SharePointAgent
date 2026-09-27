using Microsoft.AspNetCore.Identity;

namespace SharePointAgent.Persistence;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public long? AttachmentStorageLimitBytes { get; set; }
    public long? MonthlyTokenLimit { get; set; }
    public string? EntraTenantId { get; set; }
    public string? EntraObjectId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAtUtc { get; set; }
}
