using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Brings one file's graph in line with what the search index holds now. A request only names the file: the
/// pipeline reads the current revision itself, so requests can arrive late, twice, or out of order, and
/// replaying them converges on the same state.
/// <para>
/// Idempotency is keyed by tenant, document, revision, and extraction version. Work already projected is
/// skipped; a snapshot already archived is projected again without calling the model; only a revision
/// nobody has extracted with the current prompt, model, and ontology is sent to the model. The resolved
/// snapshot is archived before it is projected, so the archive is always at least as complete as the store.
/// </para>
/// </summary>
public sealed class GraphIndexingPipeline(
    IOptions<GraphRagOptions> options,
    IOptions<SharePointOptions> sharePointOptions,
    IFileMetadataRepository metadata,
    IGraphChunkSource chunkSource,
    IGraphExtractor extractor,
    IEntityResolver resolver,
    IEntityAliasIndex aliases,
    ICanonicalGraphArchive archive,
    IGraphWriter writer,
    IGraphReader reader,
    GraphOntology ontology,
    ILogger<GraphIndexingPipeline> logger) : IGraphIndexingPipeline
{
    private const int MaxAliasesPerEntity = 20;

    private readonly CanonicalGraphSnapshotValidator _validator = new(ontology);

    public string CurrentExtractionVersion { get; } =
        GraphExtractionVersion.Compute(extractor.PromptVersion, extractor.ModelId, ontology.Version);

    private string TenantId => sharePointOptions.Value.TenantId;

    public async Task<GraphIndexingOutcome> ProcessAsync(GraphIndexingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = GraphRagMetrics.Activities.StartActivity("GraphRag.Index");
        activity?.SetTag("graph_rag.request", request.Kind.ToString());
        if (!options.Value.IndexingEnabled || !options.Value.IsEnabledForTenant(TenantId))
        {
            return Record(GraphIndexingOutcome.Disabled);
        }

        var documentId = GraphDocumentIds.ForDriveItem(request.DriveId, request.ItemId);
        var record = await metadata.GetAsync(request.DriveId, request.ItemId, cancellationToken);

        // Whatever the request says, a file the indexer no longer tracks has no graph.
        if (record is null || request.Kind == GraphIndexingRequestKind.Removed)
        {
            await writer.RetractDocumentAsync(TenantId, documentId, cancellationToken);
            return Record(GraphIndexingOutcome.Removed);
        }

        if (request.Kind == GraphIndexingRequestKind.AccessChanged)
        {
            await writer.UpdateAccessScopeAsync(TenantId, documentId, GraphDocumentIds.ForAccessScope(record.PermissionsHash), cancellationToken);
            return Record(GraphIndexingOutcome.AccessUpdated);
        }

        var revision = GraphDocumentIds.ForIndexedFile(record);
        if (revision is null)
        {
            return Record(GraphIndexingOutcome.SkippedNoRevision);
        }

        var state = await reader.GetDocumentStateAsync(TenantId, documentId, cancellationToken);
        if (state is not null && state.IsServing(revision) && state.ActiveExtractionVersion == CurrentExtractionVersion && state.PendingManifest.Count == 0)
        {
            return Record(GraphIndexingOutcome.AlreadyCurrent);
        }

        var archived = await archive.TryLoadAsync(TenantId, documentId, revision, CurrentExtractionVersion, cancellationToken);
        if (archived is not null)
        {
            await writer.ApplySnapshotAsync(archived, cancellationToken);
            await aliases.UpsertAsync(AliasesOf(archived), cancellationToken);
            return Record(GraphIndexingOutcome.ReplayedFromArchive);
        }

        if (record.Sensitivity?.IsEncrypted == true && !options.Value.Extraction.IncludeEncryptedDocuments)
        {
            // Projected as an empty snapshot, so an earlier revision's facts are retracted and the document still
            // reads as current rather than being retried forever.
            await ProjectAsync(EmptySnapshot(documentId, revision), [], cancellationToken);
            return Record(GraphIndexingOutcome.SkippedProtectedContent);
        }

        var snapshot = await ExtractAsync(record, documentId, revision, cancellationToken);
        await ProjectAsync(snapshot.Snapshot, snapshot.NewAliases, cancellationToken);
        logger.LogInformation(
            "Extracted graph revision {Revision} of document {DocumentId}: {EntityCount} entities and {AssertionCount} assertions from {ChunkCount} chunks.",
            revision, documentId, snapshot.Snapshot.Entities.Count, snapshot.Snapshot.Assertions.Count, snapshot.ChunkCount);
        return Record(GraphIndexingOutcome.Extracted);
    }

    private async Task ProjectAsync(CanonicalGraphSnapshot snapshot, IReadOnlyCollection<EntityAliasEntry> newAliases, CancellationToken cancellationToken)
    {
        var validation = _validator.Validate(snapshot);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            await writer.MarkFailedAsync(TenantId, snapshot.DocumentId, snapshot.DocumentVersion, "snapshot.invalid", cancellationToken);
            throw new GraphPermanentFailureException(
                "snapshot.invalid", $"The snapshot failed validation with {validation.Errors.Count} errors; the first is {first.Code} at {first.Path}.");
        }

        var stored = await archive.SaveAsync(snapshot, cancellationToken);
        await writer.ApplySnapshotAsync(stored, cancellationToken);
        await aliases.UpsertAsync(newAliases.Concat(AliasesOf(stored)).Distinct().ToList(), cancellationToken);
    }

    private sealed record ExtractedSnapshot(CanonicalGraphSnapshot Snapshot, IReadOnlyList<EntityAliasEntry> NewAliases, int ChunkCount);

    private async Task<ExtractedSnapshot> ExtractAsync(FileIndexRecord record, string documentId, string revision, CancellationToken cancellationToken)
    {
        var chunks = (await chunkSource.GetChunksAsync(record.DriveId, record.ItemId, cancellationToken))
            .OrderBy(chunk => chunk.ChunkNumber)
            .ToList();

        // The indexer deletes a file's chunks before uploading the new ones and records the revision last, so a
        // count that disagrees, or a revision that moved while the chunks were read, means a rewrite is under way.
        if (chunks.Count != record.ChunkCount)
        {
            throw new GraphTransientFailureException("index.inconsistent", "The search index does not hold the tracked number of chunks yet.");
        }

        var reread = await metadata.GetAsync(record.DriveId, record.ItemId, cancellationToken);
        if (reread is null || GraphDocumentIds.ForIndexedFile(reread) != revision)
        {
            throw new GraphTransientFailureException("revision.changed", "The file was re-indexed while its chunks were being read.");
        }

        var limit = options.Value.Extraction;
        if (chunks.Count > limit.MaxChunksPerDocument)
        {
            GraphRagMetrics.Rejected("chunkLimit", chunks.Count - limit.MaxChunksPerDocument);
        }

        var selected = chunks.Take(limit.MaxChunksPerDocument).ToList();
        var grounded = new GroundedExtraction[selected.Count];
        using var concurrency = new SemaphoreSlim(limit.MaxConcurrentChunks);
        await Task.WhenAll(selected.Select(async (chunk, index) =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var input = new GraphExtractionInput(
                    record.Name,
                    record.ParentPath,
                    chunk.Content,
                    index > 0 ? Tail(selected[index - 1].Content, limit.NeighborContextCharacters) : null,
                    index + 1 < selected.Count ? Head(selected[index + 1].Content, limit.NeighborContextCharacters) : null);
                var response = await extractor.ExtractAsync(input, cancellationToken);
                var result = GraphExtractionGrounding.Ground(response, chunk.Content, ontology, limit.MaxEntitiesPerChunk, limit.MaxRelationsPerChunk);
                GraphRagMetrics.Rejected("ungroundedEntity", result.RejectedEntities);
                GraphRagMetrics.Rejected("ungroundedRelation", result.RejectedRelations);
                grounded[index] = result;
            }
            finally
            {
                concurrency.Release();
            }
        }));

        var candidates = new List<EntityCandidate>();
        for (var index = 0; index < selected.Count; index++)
        {
            foreach (var entity in grounded[index].Entities)
            {
                candidates.Add(new EntityCandidate(LocalRef(selected[index].ChunkNumber, entity.Ref), entity.Type, entity.Name, entity.Aliases, entity.Attributes));
            }
        }

        var resolution = await resolver.ResolveAsync(TenantId, candidates, cancellationToken);
        var decisions = resolution.Decisions.ToDictionary(decision => decision.LocalRef, StringComparer.Ordinal);
        var entities = BuildEntities(candidates, decisions);
        var types = entities.ToDictionary(entity => entity.EntityId, entity => entity.EntityType, StringComparer.Ordinal);
        var accessScope = GraphDocumentIds.ForAccessScope(record.PermissionsHash);

        var assertions = new Dictionary<string, GraphAssertion>(StringComparer.Ordinal);
        for (var index = 0; index < selected.Count; index++)
        {
            var chunk = selected[index];
            var evidence = new EvidenceRef(
                TenantId, documentId, revision, chunk.ChunkId, GraphDocumentIds.ForChunkContent(chunk.Content), null, null, accessScope);
            foreach (var relation in grounded[index].Relations)
            {
                var subject = decisions[LocalRef(chunk.ChunkNumber, relation.SubjectRef)].EntityId;
                var @object = decisions[LocalRef(chunk.ChunkNumber, relation.ObjectRef)].EntityId;
                var rule = ontology.Relations[relation.Predicate];

                // Resolution can map two mentions to one entity, which turns a valid relation into a self-reference.
                if ((subject == @object && !rule.AllowSelfReference) || !ontology.IsAllowedTriple(types[subject], relation.Predicate, types[@object]))
                {
                    GraphRagMetrics.Rejected("resolvedRelation");
                    continue;
                }

                if (rule.Symmetric && string.CompareOrdinal(subject, @object) > 0)
                {
                    (subject, @object) = (@object, subject);
                }

                var assertionId = GraphIds.ForAssertion(evidence, subject, relation.Predicate, @object, rule.Symmetric);
                assertions.TryAdd(assertionId, new GraphAssertion(
                    TenantId, assertionId, subject, relation.Predicate, @object, evidence, GraphAssertionStatus.Asserted, ontology.Version, CurrentExtractionVersion));
            }
        }

        var snapshot = new CanonicalGraphSnapshot(
            TenantId,
            documentId,
            revision,
            CanonicalGraphSnapshot.CurrentSchemaVersion,
            Provenance(),
            entities,
            MarkContradictions(assertions.Values.ToList()),
            resolution.Decisions);
        return new ExtractedSnapshot(snapshot, resolution.NewAliases, selected.Count);
    }

    /// <summary>
    /// One entity per resolved ID: the first mention's name is canonical, later names become aliases, and the
    /// first value of each attribute wins, so the result does not depend on extraction order beyond the chunks'.
    /// </summary>
    private List<GraphEntity> BuildEntities(List<EntityCandidate> candidates, Dictionary<string, EntityResolutionDecision> decisions)
    {
        var entities = new List<GraphEntity>();
        foreach (var group in candidates.GroupBy(candidate => decisions[candidate.LocalRef].EntityId, StringComparer.Ordinal))
        {
            var first = group.First();
            var canonical = GraphNormalization.NormalizeName(first.Name);
            var aliases = group.SelectMany(candidate => candidate.Aliases.Prepend(candidate.Name))
                .Where(name => GraphNormalization.NormalizeName(name) != canonical)
                .DistinctBy(GraphNormalization.NormalizeName, StringComparer.Ordinal)
                .Take(MaxAliasesPerEntity)
                .ToList();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in group.SelectMany(candidate => candidate.Attributes))
            {
                attributes.TryAdd(key, value);
            }
            entities.Add(new GraphEntity(TenantId, group.Key, first.EntityType, first.Name, aliases, attributes));
        }
        return entities;
    }

    /// <summary>
    /// An antisymmetric relation stated in both directions by one document cannot be right both ways, so both
    /// assertions are kept as disputed instead of either being trusted.
    /// </summary>
    private List<GraphAssertion> MarkContradictions(List<GraphAssertion> assertions)
    {
        var directions = assertions
            .Where(assertion => ontology.Relations[assertion.Predicate].Antisymmetric)
            .Select(assertion => (assertion.SubjectEntityId, assertion.Predicate, assertion.ObjectEntityId))
            .ToHashSet();
        return assertions
            .Select(assertion => directions.Contains((assertion.ObjectEntityId, assertion.Predicate, assertion.SubjectEntityId))
                ? assertion with { Status = GraphAssertionStatus.Disputed }
                : assertion)
            .OrderBy(assertion => assertion.AssertionId, StringComparer.Ordinal)
            .ToList();
    }

    private CanonicalGraphSnapshot EmptySnapshot(string documentId, string revision) =>
        new(TenantId, documentId, revision, CanonicalGraphSnapshot.CurrentSchemaVersion, Provenance(), [], [], []);

    private GraphExtractionProvenance Provenance() =>
        new(CurrentExtractionVersion, extractor.ModelId, extractor.PromptVersion, ontology.Version);

    private static IReadOnlyList<EntityAliasEntry> AliasesOf(CanonicalGraphSnapshot snapshot) =>
        snapshot.Entities
            .SelectMany(entity => entity.Aliases.Prepend(entity.CanonicalName)
                .Select(GraphNormalization.NormalizeName)
                .Where(alias => alias.Length > 0)
                .Select(alias => new EntityAliasEntry(entity.TenantId, entity.EntityType, alias, entity.EntityId)))
            .Distinct()
            .ToList();

    private static string LocalRef(int chunkNumber, string reference) => $"{chunkNumber}:{reference}";

    private static string Head(string text, int characters) => text.Length <= characters ? text : text[..characters];

    private static string Tail(string text, int characters) => text.Length <= characters ? text : text[^characters..];

    private static GraphIndexingOutcome Record(GraphIndexingOutcome outcome)
    {
        GraphRagMetrics.Indexed(outcome.ToString());
        return outcome;
    }
}
