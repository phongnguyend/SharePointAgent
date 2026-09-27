using SharePointAgent.Domain;

namespace SharePointAgent.Persistence;

/// <summary>
/// One row of the indexed-file table: what was last written to the search index for a SharePoint file.
/// Maps <see cref="FileIndexRecord"/>.
/// </summary>
public sealed class IndexedFileEntity
{
    public string DriveId { get; set; } = "";
    public string ItemId { get; set; } = "";

    /// <summary>Kept as <c>FileName</c> in the database, because <c>Name</c> reads poorly in ad-hoc queries.</summary>
    public string FileName { get; set; } = "";

    public string? ParentPath { get; set; }
    public string? WebUrl { get; set; }
    public string? MimeType { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public string? ETag { get; set; }
    public string? CTag { get; set; }
    public string PermissionsHash { get; set; } = "";
    public string IndexFingerprint { get; set; } = "";
    public string? SensitivityLabelId { get; set; }
    public string? SensitivityLabelName { get; set; }
    public bool? IsLabeled { get; set; }
    public bool? IsEncrypted { get; set; }
    public DateTimeOffset? SensitivityCheckedAtUtc { get; set; }
    public int ChunkCount { get; set; }
    public long? EmbeddingTokenCount { get; set; }
    public Guid ScanId { get; set; }
    public DateTimeOffset IndexedAtUtc { get; set; }
}
