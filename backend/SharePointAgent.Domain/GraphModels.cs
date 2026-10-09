using System.Text.Json.Serialization;

namespace SharePointAgent.Domain;

/// <summary>
/// Where one graph assertion was read from. <see cref="ChunkId"/> is the Azure AI Search chunk key, which is
/// derived from the drive, item, and chunk number and is therefore reused when a file is re-indexed, so it
/// identifies text only together with <see cref="DocumentVersion"/>.
/// <para>
/// <see cref="ChunkContentHash"/> is a hash of the exact chunk text the assertion was extracted from (never
/// the text itself). The index replaces a file's chunks before the indexed-file table records the new
/// revision, so for a moment a chunk key can hold new text while the table still names the old revision;
/// comparing the hash of the text the reader is authorized to see closes that gap.
/// </para>
/// <para>
/// <see cref="AccessScopeRef"/> is an opaque reference to the permission snapshot the chunk carried when it
/// was extracted. It exists for reconciliation only and never authorizes a reader: every use of the evidence
/// is checked again through the user-authorized search path.
/// </para>
/// </summary>
public sealed record EvidenceRef(
    string TenantId,
    string DocumentId,
    string DocumentVersion,
    string ChunkId,
    string ChunkContentHash,
    string? Section,
    int? PageNumber,
    string? AccessScopeRef);

/// <summary>
/// A canonical entity in one tenant's graph. <see cref="EntityId"/> is opaque and tenant-scoped (see
/// <see cref="GraphIds.ForEntity"/>), so it can be logged without revealing the entity's name.
/// <see cref="Attributes"/> is restricted to the keys the ontology allows for <see cref="EntityType"/>.
/// </summary>
public sealed record GraphEntity(
    string TenantId,
    string EntityId,
    string EntityType,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// The lifecycle of one source-backed assertion. A fact is never globally accepted: each assertion is one
/// document's claim, and several assertions from different documents may support or contradict the same
/// relation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GraphAssertionStatus>))]
public enum GraphAssertionStatus
{
    Asserted,
    Disputed,
    Retracted
}

/// <summary>
/// One directed relation claimed by one chunk of one document revision. <see cref="AssertionId"/> is
/// deterministic for the evidence and the normalized relation (see <see cref="GraphIds.ForAssertion"/>), so
/// replaying an extraction upserts the same assertion instead of adding a duplicate.
/// </summary>
public sealed record GraphAssertion(
    string TenantId,
    string AssertionId,
    string SubjectEntityId,
    string Predicate,
    string ObjectEntityId,
    EvidenceRef Evidence,
    GraphAssertionStatus Status,
    string OntologyVersion,
    string ExtractionVersion);

/// <summary>
/// What produced a snapshot. <see cref="ExtractionVersion"/> is the label stamped on every assertion; the
/// model and prompt versions behind it are kept so a snapshot can be audited or replayed without guessing
/// which extraction produced it.
/// </summary>
public sealed record GraphExtractionProvenance(
    string ExtractionVersion,
    string ModelId,
    string PromptVersion,
    string OntologyVersion);

/// <summary>
/// The resolved graph extracted from one document revision. A snapshot is self-contained: every entity an
/// assertion refers to is included, together with how each extracted mention was resolved to it, so the
/// archived snapshot alone is enough to rebuild the graph store without calling the extraction model again.
/// </summary>
public sealed record CanonicalGraphSnapshot(
    string TenantId,
    string DocumentId,
    string DocumentVersion,
    string SchemaVersion,
    GraphExtractionProvenance Extraction,
    IReadOnlyList<GraphEntity> Entities,
    IReadOnlyList<GraphAssertion> Assertions,
    IReadOnlyList<EntityResolutionDecision> Resolutions)
{
    public const string CurrentSchemaVersion = "1";
}
