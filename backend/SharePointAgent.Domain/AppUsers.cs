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
    long? AttachmentStorageLimitBytes = null, long AttachmentStorageUsedBytes = 0);

public sealed record AppUserInput(string Email, string DisplayName, IReadOnlyList<string> Roles, bool IsActive = true, string? ConcurrencyStamp = null);

public sealed record AppUserStorageInput(long? AttachmentStorageLimitBytes, string ConcurrencyStamp);
