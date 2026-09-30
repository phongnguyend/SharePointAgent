namespace SharePointAgent.Domain;

public sealed record SharePointRecycleBinItem(
    string Id, string Name, long? Size, DateTimeOffset? DeletedDateTime, string? DeletedFromLocation);

public sealed record SharePointRecycleBinListing(
    string SiteName, string? RecycleBinUrl, IReadOnlyList<SharePointRecycleBinItem> Items);
