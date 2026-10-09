using System.Text.Json.Serialization;

namespace SharePointAgent.Domain;

/// <summary>
/// How an extracted mention was matched to a canonical entity, in decreasing order of confidence. Similarity
/// alone never merges two mentions: an ambiguous name gets an entity of its own rather than joining one of
/// several candidates.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntityResolutionMethod>))]
public enum EntityResolutionMethod
{
    /// <summary>The mention carried an identifier the ontology marks as authoritative for its type.</summary>
    AuthoritativeIdentifier,

    /// <summary>The mention's normalized name or alias matched exactly one existing entity.</summary>
    ExactAlias,

    /// <summary>Nothing matched, so the mention's normalized name identifies a new entity.</summary>
    NewEntity,

    /// <summary>
    /// The name matched several existing entities. The mention keeps a name-keyed entity of its own and the
    /// candidates are recorded so an operator can merge them deliberately.
    /// </summary>
    Ambiguous
}

/// <summary>
/// One mention extracted from a document, before it is resolved. <see cref="LocalRef"/> is unique within one
/// document's extraction and is how relations refer to the mention.
/// </summary>
public sealed record EntityCandidate(
    string LocalRef,
    string EntityType,
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// The audit record of one resolution, archived with the snapshot so a migration or rebuild can replay it
/// without asking the model again. <see cref="CandidateEntityIds"/> lists the entities an ambiguous name
/// could have meant.
/// </summary>
public sealed record EntityResolutionDecision(
    string LocalRef,
    string EntityId,
    EntityResolutionMethod Method,
    IReadOnlyList<string> CandidateEntityIds);

/// <summary>
/// An entry in the alias lookup: a normalized alias of one entity of one type in one tenant. Lookups are
/// always scoped to a tenant, so one tenant's names never resolve to another tenant's entities.
/// </summary>
public sealed record EntityAliasEntry(string TenantId, string EntityType, string NormalizedAlias, string EntityId);

/// <summary>
/// A stored entity together with its merge history. An entity merged into another keeps its ID and points at
/// the survivor through <see cref="RedirectToEntityId"/>, so assertions that name it stay valid; the survivor
/// lists every merged ID in <see cref="MergedEntityIds"/> so traversal can reach those assertions too.
/// </summary>
public sealed record GraphEntityRecord(
    GraphEntity Entity,
    string? RedirectToEntityId,
    IReadOnlyList<string> MergedEntityIds,
    IReadOnlyList<GraphEntityHistoryEntry> History,
    string? ConcurrencyToken);

/// <summary>One change to an entity's identity, such as a merge. Carries IDs only, never names.</summary>
public sealed record GraphEntityHistoryEntry(string Action, string? RelatedEntityId, string Actor, DateTimeOffset AtUtc);
