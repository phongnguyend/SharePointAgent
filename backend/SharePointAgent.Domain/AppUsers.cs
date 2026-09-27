namespace SharePointAgent.Domain;

public sealed record SystemAttachmentStorage(long FileCount, long UsedBytes, long OrphanBytes, long UnassignedBytes);

public static class AppRoles
{
    public const string GlobalAdmin = "Global Admin";
    public const string GlobalReaderAdmin = "Global Reader Admin";
    public const string User = "User";
    public static readonly string[] All = [GlobalAdmin, GlobalReaderAdmin, User];
}

public sealed record AppUserView(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, bool IsActive,
    bool HasSignedIn, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastLoginAtUtc, string ConcurrencyStamp,
    long? AttachmentStorageLimitBytes = null, long AttachmentStorageUsedBytes = 0,
    long? MonthlyTokenLimit = null, long MonthlyTokensUsed = 0, DateTimeOffset? TokenUsageResetsAtUtc = null,
    IReadOnlyList<DailyTokenUsage>? DailyTokenUsage = null,
    IReadOnlyList<DailyModelTokenUsage>? DailyModelTokenUsage = null);

public sealed record DailyTokenUsage(int Day, long InputTokens, long OutputTokens, long TotalTokens);
public sealed record DailyModelTokenUsage(int Day, string? ModelId, long InputTokens, long OutputTokens, long TotalTokens);

public sealed record AppUserInput(string Email, string DisplayName, IReadOnlyList<string> Roles, bool IsActive = true, string? ConcurrencyStamp = null);

public sealed record AppUserStorageInput(long? AttachmentStorageLimitBytes, string ConcurrencyStamp);
public sealed record AppUserTokenLimitInput(long? MonthlyTokenLimit, string ConcurrencyStamp);
