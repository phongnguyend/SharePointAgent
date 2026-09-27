using SharePointAgent.Domain;

namespace SharePointAgent.Persistence;

/// <summary>
/// One row of the delta-checkpoint table: where the next delta pass for a drive resumes from. Maps
/// <see cref="DeltaCheckpoint"/> plus the timestamp the worker last wrote it.
/// </summary>
public sealed class DeltaStateEntity
{
    public string DriveId { get; set; } = "";
    public string DeltaLink { get; set; } = "";
    public Guid ScanId { get; set; }
    public Guid? SweptScanId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
