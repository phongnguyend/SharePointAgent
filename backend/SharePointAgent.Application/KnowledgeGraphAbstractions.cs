using SharePointAgent.Domain;

namespace SharePointAgent.Application;

// Graph RAG contracts. Implementations live in Infrastructure; nothing here names a storage engine, so the
// graph store can move from Cosmos DB for NoSQL to a graph database by replacing one adapter.

/// <summary>
/// The minimal hook the SharePoint indexer calls after it changes the search index. Publishing is
/// best-effort: a lost request is repaired by reconciliation, so the hook can never fail indexing.
/// </summary>
public interface IGraphIndexingSignal
{
    Task PublishAsync(GraphIndexingRequest request, CancellationToken cancellationToken);
}

/// <summary>Reads one file's chunks from the existing search index, on the server side only.</summary>
public interface IGraphChunkSource
{
    Task<IReadOnlyList<GraphSourceChunk>> GetChunksAsync(string driveId, string itemId, CancellationToken cancellationToken);
}

/// <summary>What the extractor is given for one chunk. Neighbouring text is context, never evidence.</summary>
public sealed record GraphExtractionInput(
    string DocumentName,
    string? DocumentPath,
    string ChunkText,
    string? PreviousContext,
    string? NextContext);

/// <summary>Turns one chunk into candidate entities and relations with schema-constrained model output.</summary>
public interface IGraphExtractor
{
    /// <summary>The model the extractor calls, recorded in every snapshot's provenance.</summary>
    string ModelId { get; }

    string PromptVersion { get; }

    Task<GraphExtractionResponse> ExtractAsync(GraphExtractionInput input, CancellationToken cancellationToken);
}

/// <summary>Alias lookup for entity resolution and query linking, always scoped to one tenant.</summary>
public interface IEntityAliasIndex
{
    /// <summary>
    /// Returns, for each normalized alias that matched, the entities it names. A null
    /// <paramref name="entityType"/> searches every type.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FindAsync(
        string tenantId, string? entityType, IReadOnlyCollection<string> normalizedAliases, CancellationToken cancellationToken);

    Task UpsertAsync(IReadOnlyCollection<EntityAliasEntry> entries, CancellationToken cancellationToken);
}

public sealed record EntityResolutionResult(
    IReadOnlyList<EntityResolutionDecision> Decisions,
    IReadOnlyList<EntityAliasEntry> NewAliases);

public interface IEntityResolver
{
    Task<EntityResolutionResult> ResolveAsync(string tenantId, IReadOnlyList<EntityCandidate> candidates, CancellationToken cancellationToken);
}

/// <summary>Versioned store of resolved snapshots, the source of truth the graph store is rebuilt from.</summary>
public interface ICanonicalGraphArchive
{
    /// <summary>
    /// Archives a snapshot and returns the archived one. When the same document revision and extraction
    /// version is already archived, the existing snapshot wins and is returned, so a replay projects exactly
    /// what was archived first.
    /// </summary>
    Task<CanonicalGraphSnapshot> SaveAsync(CanonicalGraphSnapshot snapshot, CancellationToken cancellationToken);

    Task<CanonicalGraphSnapshot?> TryLoadAsync(
        string tenantId, string documentId, string documentVersion, string extractionVersion, CancellationToken cancellationToken);
}

/// <summary>
/// The storage port for the graph projection. It holds no policy: the writer decides what to write and the
/// traversal decides what to read, so another store needs only this adapter.
/// </summary>
public interface IGraphProjectionStore
{
    Task<IReadOnlyList<GraphDocumentState>> GetDocumentStatesAsync(string tenantId, IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the state when its concurrency token is null, otherwise replaces it only if it is unchanged.
    /// Returns the new token. Throws <see cref="GraphConcurrencyException"/> when another writer got there first.
    /// </summary>
    Task<string> SaveDocumentStateAsync(GraphDocumentState state, TimeSpan? timeToLive, CancellationToken cancellationToken);

    IAsyncEnumerable<GraphDocumentState> ListDocumentStatesAsync(string tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphEntityRecord>> GetEntitiesAsync(string tenantId, IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken);

    /// <summary>Same concurrency contract as <see cref="SaveDocumentStateAsync"/>.</summary>
    Task<string> SaveEntityAsync(GraphEntityRecord entity, CancellationToken cancellationToken);

    /// <summary>Writes assertions and their reverse adjacency. Idempotent.</summary>
    Task UpsertAssertionsAsync(IReadOnlyList<GraphAssertion> assertions, CancellationToken cancellationToken);

    /// <summary>Changes the status of stored assertions, ignoring ones that no longer exist.</summary>
    Task SetAssertionStatusAsync(IReadOnlyList<GraphAssertionKey> keys, GraphAssertionStatus status, TimeSpan? timeToLive, CancellationToken cancellationToken);

    /// <summary>
    /// Reads non-retracted assertions touching the given entities, with partition-targeted reads only.
    /// Outgoing reads the primary assertions by subject; incoming reads the reverse projection by object.
    /// </summary>
    Task<GraphAdjacencyPage> GetAdjacentAssertionsAsync(
        string tenantId,
        IReadOnlyCollection<string> entityIds,
        IReadOnlySet<string> predicates,
        GraphTraversalDirection direction,
        int maxResults,
        CancellationToken cancellationToken);
}

public interface IGraphWriter
{
    Task ApplySnapshotAsync(CanonicalGraphSnapshot snapshot, CancellationToken cancellationToken);

    Task RetractDocumentAsync(string tenantId, string documentId, CancellationToken cancellationToken);

    /// <summary>Hides a document whose new revision could not be projected, until a later attempt succeeds.</summary>
    Task MarkFailedAsync(string tenantId, string documentId, string revision, string failureCode, CancellationToken cancellationToken);

    Task UpdateAccessScopeAsync(string tenantId, string documentId, string? accessScopeRef, CancellationToken cancellationToken);

    /// <summary>
    /// Merges one entity into another of the same type. The source keeps its ID and redirects to the target;
    /// nothing is rewritten, so the change is auditable and reversible.
    /// </summary>
    Task MergeEntitiesAsync(string tenantId, string sourceEntityId, string targetEntityId, string actor, CancellationToken cancellationToken);
}

public interface IGraphReader
{
    Task<GraphTraversalResult> TraverseAsync(GraphTraversalPlan plan, CancellationToken cancellationToken);

    Task<GraphDocumentState?> GetDocumentStateAsync(string tenantId, string documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphDocumentState>> GetDocumentStatesAsync(string tenantId, IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches specific chunks only if the user may read them, through the same permission filter the search
/// endpoints use. A missing user is an error, never an unfiltered read.
/// </summary>
public interface IAuthorizedChunkStore
{
    Task<IReadOnlyList<SearchQueryHit>> GetAuthorizedChunksAsync(string userId, IReadOnlyCollection<string> chunkIds, CancellationToken cancellationToken);
}

public interface IGraphIndexingPipeline
{
    /// <summary>The extraction version a document must have been projected with to count as current.</summary>
    string CurrentExtractionVersion { get; }

    Task<GraphIndexingOutcome> ProcessAsync(GraphIndexingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="BaselineHits"/> are authorized hits the caller already has: they seed the traversal and are not
/// returned again. <see cref="SeedHits"/> are authorized hits that only seed it, so they can still come back
/// as the evidence for a relationship. <see cref="IncludeShadowResults"/> returns the chunks shadow mode
/// computed; only the administrator evaluation endpoint sets it.
/// </summary>
public sealed record GraphRetrievalRequest(
    string? UserId,
    string Query,
    IReadOnlyList<SearchQueryHit> BaselineHits,
    bool IncludeShadowResults = false,
    IReadOnlyList<SearchQueryHit>? SeedHits = null);

/// <summary>Adds authorized, graph-derived chunks to a baseline search, or nothing when anything is uncertain.</summary>
public interface IGraphRetrievalService
{
    /// <summary>
    /// Whether results are shown to users for this host's tenant. False in shadow mode, where retrieval runs
    /// only to record its metrics.
    /// </summary>
    bool ReturnsResults { get; }

    Task<GraphRetrievalResult> AugmentAsync(GraphRetrievalRequest request, CancellationToken cancellationToken);
}

/// <summary>The structured output the extraction model must produce for one chunk.</summary>
public sealed class GraphExtractionResponse
{
    public List<ExtractedEntity> Entities { get; set; } = [];

    public List<ExtractedRelation> Relations { get; set; } = [];
}

public sealed class ExtractedEntity
{
    /// <summary>A short reference unique within this response, such as <c>e1</c>.</summary>
    public string Ref { get; set; } = "";

    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public List<string> Aliases { get; set; } = [];

    public List<ExtractedAttribute> Attributes { get; set; } = [];
}

public sealed class ExtractedAttribute
{
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed class ExtractedRelation
{
    public string SubjectRef { get; set; } = "";

    public string Predicate { get; set; } = "";

    public string ObjectRef { get; set; } = "";

    /// <summary>The exact words of the chunk that state the relation.</summary>
    public string EvidenceQuote { get; set; } = "";
}
