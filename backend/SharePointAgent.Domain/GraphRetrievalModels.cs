using System.Text.Json.Serialization;

namespace SharePointAgent.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<GraphTraversalDirection>))]
public enum GraphTraversalDirection
{
    /// <summary>From subject to object, as the relation reads: what X depends on.</summary>
    Outgoing,

    /// <summary>From object to subject: what depends on X. Served by the reverse adjacency projection.</summary>
    Incoming,

    Both
}

/// <summary>
/// A typed, bounded traversal request. Plans are built by code from a fixed ontology, never by a model
/// writing a query, and every dimension that drives cost has a limit.
/// </summary>
public sealed record GraphTraversalPlan(
    string TenantId,
    IReadOnlyList<string> StartEntityIds,
    IReadOnlySet<string> AllowedPredicates,
    int MaxDepth,
    int MaxEntities,
    int MaxAssertions,
    GraphTraversalDirection Direction = GraphTraversalDirection.Both,
    TimeSpan? Timeout = null,
    double? MaxRequestCharge = null);

/// <summary>
/// One assertion reached by a traversal. <see cref="FromEntityId"/> is the entity the traversal expanded and
/// <see cref="ToEntityId"/> the one it reached, so a path can be rebuilt and checked edge by edge.
/// </summary>
public sealed record GraphTraversalEdge(GraphAssertion Assertion, int Depth, string FromEntityId, string ToEntityId);

/// <summary>
/// Candidate edges, not facts a user may see. Every edge still has to pass evidence authorization before it
/// is used, and nothing here is returned to an end user as it is.
/// </summary>
public sealed record GraphTraversalResult(
    IReadOnlyList<string> StartEntityIds,
    IReadOnlyList<GraphTraversalEdge> Edges,
    int VisitedEntityCount,
    bool Truncated,
    string? TruncationReason,
    double RequestCharge);

/// <summary>A page of adjacency read from the store, with what it cost.</summary>
public sealed record GraphAdjacencyPage(IReadOnlyList<GraphAssertion> Assertions, double RequestCharge, bool Truncated);

/// <summary>Identifies a stored assertion and both partitions it lives in.</summary>
public sealed record GraphAssertionKey(string TenantId, string AssertionId, string SubjectEntityId, string ObjectEntityId);

/// <summary>
/// An authorized chunk that a verified relationship led to. <see cref="Predicate"/> names the relationship
/// that connects it to the question's entities; entity names are deliberately absent, because a canonical
/// name may have been taken from a document this reader cannot see.
/// </summary>
public sealed record GraphRelatedChunk(SearchQueryHit Hit, string Predicate, int Depth, bool Disputed);

[JsonConverter(typeof(JsonStringEnumConverter<GraphRetrievalStatus>))]
public enum GraphRetrievalStatus
{
    Disabled,
    NoUser,
    NotGraphQuestion,
    NoSeedEntities,
    Augmented,
    NoAuthorizedEvidence,
    Shadow,
    FellBack
}

public sealed record GraphRetrievalResult(GraphRetrievalStatus Status, IReadOnlyList<GraphRelatedChunk> Chunks)
{
    public static GraphRetrievalResult Empty(GraphRetrievalStatus status) => new(status, []);
}
