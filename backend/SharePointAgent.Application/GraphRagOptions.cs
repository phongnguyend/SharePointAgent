using System.ComponentModel.DataAnnotations;
using SharePointAgent.Domain;

namespace SharePointAgent.Application;

/// <summary>
/// Graph RAG settings. Everything is off by default: with no <c>GraphRag</c> section the existing indexing
/// and retrieval run exactly as they did, and no graph service, Cosmos account, or archive is required.
/// <para>
/// Rollout order is indexing, then shadow retrieval (graph results computed and measured but never shown),
/// then retrieval for the tenants listed in <see cref="EnabledTenantIds"/>.
/// </para>
/// </summary>
public sealed class GraphRagOptions
{
    public const string SectionName = "GraphRag";

    /// <summary>Extract graph facts from indexed files and project them to the graph store.</summary>
    public bool IndexingEnabled { get; set; }

    /// <summary>Add authorized, graph-derived chunks to the chat agent's search results.</summary>
    public bool RetrievalEnabled { get; set; }

    /// <summary>
    /// Run graph retrieval and record its metrics without passing anything to the agent. Used to measure
    /// latency, cost, and authorization rejections before users see graph results.
    /// </summary>
    public bool ShadowRetrieval { get; set; }

    /// <summary>The tenants graph features run for. Empty means every tenant.</summary>
    public List<string> EnabledTenantIds { get; set; } = [];

    /// <summary>
    /// The ontology, replacing the built-in default when set. Leave unset to use
    /// <see cref="GraphOntologyDefaults"/>; a configured ontology must change its version.
    /// </summary>
    public GraphOntologyDefinition? Ontology { get; set; }

    public GraphExtractionOptions Extraction { get; set; } = new();

    public GraphTraversalOptions Traversal { get; set; } = new();

    public GraphReconciliationOptions Reconciliation { get; set; } = new();

    public CosmosGraphOptions Cosmos { get; set; } = new();

    public GraphArchiveOptions Archive { get; set; } = new();

    /// <summary>The Service Bus queue indexing requests travel on.</summary>
    [Required] public string IndexingQueueName { get; set; } = "graph-indexing";

    /// <summary>The Azure AI Search index holding entity aliases for resolution and query linking.</summary>
    [Required] public string AliasIndexName { get; set; } = "graph-entity-aliases";

    public bool IsEnabledForTenant(string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId)
        && (EnabledTenantIds.Count == 0 || EnabledTenantIds.Contains(tenantId, StringComparer.OrdinalIgnoreCase));

    public GraphOntology CreateOntology() => GraphOntology.Create(Ontology ?? GraphOntologyDefaults.CreateDefinition());
}

public sealed class GraphExtractionOptions
{
    /// <summary>The chat deployment used for extraction. Empty uses <c>AzureOpenAI:ChatDeployment</c>.</summary>
    public string? Deployment { get; set; }

    /// <summary>
    /// Whether files protected by a sensitivity label with encryption are extracted. Off by default: their
    /// decrypted text is in the search index under its ACL, but copying facts out of it is a separate decision.
    /// </summary>
    public bool IncludeEncryptedDocuments { get; set; }

    [Range(1, 32)] public int MaxConcurrentDocuments { get; set; } = 2;

    [Range(1, 32)] public int MaxConcurrentChunks { get; set; } = 4;

    [Range(1, 5000)] public int MaxChunksPerDocument { get; set; } = 200;

    [Range(256, 16000)] public int MaxOutputTokens { get; set; } = 2000;

    [Range(0, 4000)] public int NeighborContextCharacters { get; set; } = 600;

    [Range(0, 10)] public int MaxRetries { get; set; } = 4;

    [Range(1, 50)] public int MaxDeliveryAttempts { get; set; } = 8;

    [Range(1, 100)] public int MaxEntitiesPerChunk { get; set; } = 30;

    [Range(1, 100)] public int MaxRelationsPerChunk { get; set; } = 30;
}

public sealed class GraphTraversalOptions
{
    [Range(1, 3)] public int DefaultMaxDepth { get; set; } = 2;

    [Range(1, 500)] public int MaxEntities { get; set; } = 50;

    [Range(1, 2000)] public int MaxAssertions { get; set; } = 200;

    [Range(1, 50)] public int MaxSeedEntities { get; set; } = 10;

    [Range(50, 30000)] public int TimeoutMilliseconds { get; set; } = 1500;

    [Range(1, 100000)] public double MaxRequestCharge { get; set; } = 500;

    /// <summary>How many graph-derived chunks one search may add to the baseline results.</summary>
    [Range(1, 20)] public int MaxAdditionalChunks { get; set; } = 5;

    /// <summary>A character budget for the added chunks, so graph results cannot crowd out the baseline.</summary>
    [Range(500, 100000)] public int MaxAdditionalCharacters { get; set; } = 12000;

    /// <summary>The most distinct documents whose evidence one search verifies.</summary>
    [Range(1, 200)] public int MaxEvidenceDocuments { get; set; } = 40;
}

public sealed class GraphReconciliationOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 10080)] public int IntervalMinutes { get; set; } = 60;

    [Range(10, 5000)] public int PageSize { get; set; } = 200;

    /// <summary>The most documents one cycle reprocesses, so a large backlog is worked through gradually.</summary>
    [Range(1, 100000)] public int MaxRepairsPerCycle { get; set; } = 500;

    /// <summary>How long retracted assertions are kept for audit before the store expires them.</summary>
    [Range(1, 3650)] public int RetractedRetentionDays { get; set; } = 30;
}

public sealed class CosmosGraphOptions
{
    /// <summary>The account endpoint, used with managed identity.</summary>
    public string? Endpoint { get; set; }

    /// <summary>A connection string, for local development against the emulator only.</summary>
    public string? ConnectionString { get; set; }

    public bool UsedManagedIdentity { get; set; } = true;

    [Required] public string DatabaseName { get; set; } = "graphrag";

    [Required] public string EntitiesContainer { get; set; } = "graphEntities";

    [Required] public string AssertionsContainer { get; set; } = "graphAssertions";

    [Required] public string ReverseAssertionsContainer { get; set; } = "graphAssertionsByObject";

    [Required] public string DocumentStateContainer { get; set; } = "graphDocumentState";

    /// <summary>
    /// Synthetic partitions per tenant. Must be benchmarked for the corpus; changing it requires a rebuild
    /// from the snapshot archive.
    /// </summary>
    [Range(1, 4096)] public int PartitionBuckets { get; set; } = 16;

    /// <summary>
    /// Create the database and containers on start. Needs control-plane rights, which managed-identity data
    /// roles do not grant, so it is for the emulator; deployed accounts get their containers from Bicep.
    /// </summary>
    public bool CreateIfNotExists { get; set; }

    [Range(0, 20)] public int MaxRetryAttemptsOnThrottling { get; set; } = 6;

    public bool IsConfigured => UsedManagedIdentity
        ? Uri.TryCreate(Endpoint, UriKind.Absolute, out _)
        : !string.IsNullOrWhiteSpace(ConnectionString);
}

public sealed class GraphArchiveOptions
{
    public bool UsedManagedIdentity { get; set; } = true;

    public string? ServiceUri { get; set; }

    public string? ConnectionString { get; set; }

    [Required] public string ContainerName { get; set; } = "graph-snapshots";

    public bool IsConfigured => UsedManagedIdentity
        ? Uri.TryCreate(ServiceUri, UriKind.Absolute, out _)
        : !string.IsNullOrWhiteSpace(ConnectionString);
}
