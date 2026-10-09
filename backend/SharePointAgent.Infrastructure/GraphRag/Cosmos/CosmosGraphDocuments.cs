using System.Text.Json.Serialization;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag.Cosmos;

// The stored shapes. They are kept separate from the domain records so the domain stays free of storage
// concerns (partition keys, ETags, TTLs) and so the stored JSON can be versioned on its own.

internal sealed class EntityDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    public string PartitionKey { get; set; } = "";

    public string TenantId { get; set; } = "";

    public string EntityType { get; set; } = "";

    public string CanonicalName { get; set; } = "";

    public List<string> Aliases { get; set; } = [];

    public Dictionary<string, string> Attributes { get; set; } = [];

    public string? RedirectToEntityId { get; set; }

    public List<string> MergedEntityIds { get; set; } = [];

    public List<GraphEntityHistoryEntry> History { get; set; } = [];

    public DateTimeOffset UpdatedAtUtc { get; set; }

    [JsonPropertyName("_etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; set; }

    public static EntityDocument From(GraphEntityRecord record, string partitionKey, DateTimeOffset now) => new()
    {
        Id = record.Entity.EntityId,
        PartitionKey = partitionKey,
        TenantId = record.Entity.TenantId,
        EntityType = record.Entity.EntityType,
        CanonicalName = record.Entity.CanonicalName,
        Aliases = record.Entity.Aliases.ToList(),
        Attributes = record.Entity.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        RedirectToEntityId = record.RedirectToEntityId,
        MergedEntityIds = record.MergedEntityIds.ToList(),
        History = record.History.ToList(),
        UpdatedAtUtc = now
    };

    public GraphEntityRecord ToRecord() => new(
        new GraphEntity(TenantId, Id, EntityType, CanonicalName, Aliases, Attributes),
        RedirectToEntityId,
        MergedEntityIds,
        History,
        ETag);
}

internal sealed class EvidenceDocument
{
    public string TenantId { get; set; } = "";

    public string DocumentId { get; set; } = "";

    public string DocumentVersion { get; set; } = "";

    public string ChunkId { get; set; } = "";

    public string ChunkContentHash { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Section { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PageNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessScopeRef { get; set; }
}

/// <summary>
/// One directed edge. The same shape is written twice: to the primary container partitioned by subject, which
/// answers "what does X relate to", and to the reverse projection partitioned by object, which answers "what
/// relates to X". The primary copy is authoritative; the reverse copy can always be rebuilt from it.
/// </summary>
internal sealed class AssertionDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    public string PartitionKey { get; set; } = "";

    public string TenantId { get; set; } = "";

    public string SubjectEntityId { get; set; } = "";

    public string Predicate { get; set; } = "";

    public string ObjectEntityId { get; set; } = "";

    public EvidenceDocument Evidence { get; set; } = new();

    public string Status { get; set; } = "asserted";

    public string OntologyVersion { get; set; } = "";

    public string ExtractionVersion { get; set; } = "";

    public DateTimeOffset UpdatedAtUtc { get; set; }

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLive { get; set; }

    public static AssertionDocument From(GraphAssertion assertion, string partitionKey, DateTimeOffset now) => new()
    {
        Id = assertion.AssertionId,
        PartitionKey = partitionKey,
        TenantId = assertion.TenantId,
        SubjectEntityId = assertion.SubjectEntityId,
        Predicate = assertion.Predicate,
        ObjectEntityId = assertion.ObjectEntityId,
        Evidence = new EvidenceDocument
        {
            TenantId = assertion.Evidence.TenantId,
            DocumentId = assertion.Evidence.DocumentId,
            DocumentVersion = assertion.Evidence.DocumentVersion,
            ChunkId = assertion.Evidence.ChunkId,
            ChunkContentHash = assertion.Evidence.ChunkContentHash,
            Section = assertion.Evidence.Section,
            PageNumber = assertion.Evidence.PageNumber,
            AccessScopeRef = assertion.Evidence.AccessScopeRef
        },
        Status = StatusName(assertion.Status),
        OntologyVersion = assertion.OntologyVersion,
        ExtractionVersion = assertion.ExtractionVersion,
        UpdatedAtUtc = now
    };

    public GraphAssertion ToAssertion() => new(
        TenantId,
        Id,
        SubjectEntityId,
        Predicate,
        ObjectEntityId,
        new EvidenceRef(
            Evidence.TenantId, Evidence.DocumentId, Evidence.DocumentVersion, Evidence.ChunkId, Evidence.ChunkContentHash,
            Evidence.Section, Evidence.PageNumber, Evidence.AccessScopeRef),
        Status switch
        {
            "disputed" => GraphAssertionStatus.Disputed,
            "retracted" => GraphAssertionStatus.Retracted,
            _ => GraphAssertionStatus.Asserted
        },
        OntologyVersion,
        ExtractionVersion);

    public static string StatusName(GraphAssertionStatus status) => status switch
    {
        GraphAssertionStatus.Disputed => "disputed",
        GraphAssertionStatus.Retracted => "retracted",
        _ => "asserted"
    };
}

internal sealed class DocumentStateDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    public string PartitionKey { get; set; } = "";

    public string TenantId { get; set; } = "";

    public string DocumentId { get; set; } = "";

    public GraphProjectionStatus Status { get; set; }

    public string? ActiveRevision { get; set; }

    public string? ActiveExtractionVersion { get; set; }

    public List<GraphManifestEntry> Manifest { get; set; } = [];

    public string? PendingRevision { get; set; }

    public List<GraphManifestEntry> PendingManifest { get; set; } = [];

    public string? AccessScopeRef { get; set; }

    public string? FailureCode { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeToLive { get; set; }

    [JsonPropertyName("_etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; set; }

    public static DocumentStateDocument From(GraphDocumentState state, string id, string partitionKey, TimeSpan? timeToLive) => new()
    {
        Id = id,
        PartitionKey = partitionKey,
        TenantId = state.TenantId,
        DocumentId = state.DocumentId,
        Status = state.Status,
        ActiveRevision = state.ActiveRevision,
        ActiveExtractionVersion = state.ActiveExtractionVersion,
        Manifest = state.Manifest.ToList(),
        PendingRevision = state.PendingRevision,
        PendingManifest = state.PendingManifest.ToList(),
        AccessScopeRef = state.AccessScopeRef,
        FailureCode = state.FailureCode,
        UpdatedAtUtc = state.UpdatedAtUtc,
        TimeToLive = timeToLive is { } ttl ? (int)Math.Ceiling(ttl.TotalSeconds) : null
    };

    public GraphDocumentState ToState() => new(
        TenantId, DocumentId, Status, ActiveRevision, ActiveExtractionVersion, Manifest, PendingRevision, PendingManifest,
        AccessScopeRef, FailureCode, UpdatedAtUtc, ETag);
}
