namespace SharePointAgent.Domain;

public sealed record SharePointBrowseItem(
    string Id, string Name, bool IsFolder, long? Size, DateTimeOffset? LastModifiedUtc,
    string? WebUrl, string? ParentId, string? ETag);

public sealed record SharePointFolderListing(
    SharePointBrowseItem Folder, IReadOnlyList<SharePointBrowseItem> Breadcrumbs,
    IReadOnlyList<SharePointBrowseItem> Items);
