using System.Text.Json.Serialization;

namespace SharePointAgent.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<GraphIndexingRequestKind>))]
public enum GraphIndexingRequestKind
{
    /// <summary>The file's chunks were written to the search index.</summary>
    Indexed,

    /// <summary>The file's permissions or properties changed but its content did not.</summary>
    AccessChanged,

    /// <summary>The file left the search index.</summary>
    Removed
}

/// <summary>
/// Asks the graph pipeline to bring one file's graph up to date. It carries identifiers only: the pipeline
/// reads the current state of the file from the indexed-file table and the search index, so a delayed,
/// duplicated, or reordered request still converges on the current revision and no document text travels
/// through the queue.
/// </summary>
public sealed record GraphIndexingRequest(
    GraphIndexingRequestKind Kind,
    string DriveId,
    string ItemId,
    DateTimeOffset RequestedAtUtc);

/// <summary>One chunk as the search index holds it, read by the pipeline on the server side only.</summary>
public sealed record GraphSourceChunk(string ChunkId, int ChunkNumber, string Content);

/// <summary>
/// Whether a document's graph can be served. Only <see cref="Ready"/> documents are visible to traversal; a
/// document is <see cref="Tombstoned"/> the moment its deletion is processed, before its assertions are
/// retracted, so cleanup can never serve a stale fact.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GraphProjectionStatus>))]
public enum GraphProjectionStatus
{
    Pending,
    Ready,
    Failed,
    Tombstoned
}

/// <summary>
/// One assertion a document revision contributed. The endpoints and chunk let the manifest locate the
/// assertion's partitions for retraction and let retrieval find the entities an authorized chunk mentions,
/// without querying across partitions.
/// </summary>
public sealed record GraphManifestEntry(
    string AssertionId,
    string SubjectEntityId,
    string Predicate,
    string ObjectEntityId,
    string ChunkId);

/// <summary>
/// The projection state of one document: which revision is active, what it contributed, and anything still
/// in flight. <see cref="PendingManifest"/> lists assertions that are stored but not part of the active
/// revision: a new revision's assertions are recorded there before they are written, and a superseded
/// revision's assertions are moved there before they are retracted. Either way a projection that stops
/// half-way leaves a record of exactly what has to be finished or cleaned up.
/// </summary>
public sealed record GraphDocumentState(
    string TenantId,
    string DocumentId,
    GraphProjectionStatus Status,
    string? ActiveRevision,
    string? ActiveExtractionVersion,
    IReadOnlyList<GraphManifestEntry> Manifest,
    string? PendingRevision,
    IReadOnlyList<GraphManifestEntry> PendingManifest,
    string? AccessScopeRef,
    string? FailureCode,
    DateTimeOffset UpdatedAtUtc,
    string? ConcurrencyToken = null)
{
    public bool IsServing(string revision) =>
        Status == GraphProjectionStatus.Ready && string.Equals(ActiveRevision, revision, StringComparison.Ordinal);
}

/// <summary>How one indexing request ended, for metrics and logs. Carries no document content.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GraphIndexingOutcome>))]
public enum GraphIndexingOutcome
{
    Disabled,
    AlreadyCurrent,
    Extracted,
    ReplayedFromArchive,
    SkippedProtectedContent,
    SkippedNoRevision,
    Removed,
    AccessUpdated
}

/// <summary>A failure that retrying cannot fix, such as a snapshot that breaks the ontology. Dead-lettered.</summary>
public sealed class GraphPermanentFailureException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A failure expected to clear on its own, such as an index rewrite in progress. Retried with backoff.</summary>
public sealed class GraphTransientFailureException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>Another writer changed the same record first; the operation is retried from fresh state.</summary>
public sealed class GraphConcurrencyException(string message) : Exception(message);
