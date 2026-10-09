using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Projects archived snapshots into the graph store. Writes across partitions are not atomic, so every step
/// is idempotent and the document state records enough to finish or undo a projection that stopped part-way:
/// <list type="number">
/// <item>The new revision's manifest is recorded as pending before any assertion is written.</item>
/// <item>Entities and assertions are upserted; replaying them is harmless.</item>
/// <item>The active-revision pointer switches in one conditional write, the only point at which readers see
/// the new revision.</item>
/// <item>Assertions only the old revision had are retracted.</item>
/// </list>
/// A document never loses an assertion another document supports, because an assertion belongs to exactly
/// one document revision; and entities are never deleted, because other documents may refer to them.
/// </summary>
public sealed class GraphProjectionWriter(
    IGraphProjectionStore store,
    GraphOntology ontology,
    IOptions<GraphRagOptions> options,
    TimeProvider time,
    ILogger<GraphProjectionWriter> logger) : IGraphWriter
{
    /// <summary>Aliases kept on a stored entity; mentions beyond this still resolve through the alias index.</summary>
    public const int MaxStoredAliases = 50;

    private const int MaxStoredAttributes = 16;

    private const int MaxHistoryEntries = 20;

    private const int MaxConflictRetries = 5;

    private readonly CanonicalGraphSnapshotValidator _validator = new(ontology);

    private TimeSpan RetractedRetention => TimeSpan.FromDays(options.Value.Reconciliation.RetractedRetentionDays);

    public async Task ApplySnapshotAsync(CanonicalGraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var validation = _validator.Validate(snapshot);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            throw new GraphPermanentFailureException(
                "snapshot.invalid", $"The snapshot failed validation with {validation.Errors.Count} errors; the first is {first.Code} at {first.Path}.");
        }

        var tenantId = snapshot.TenantId;
        var documentId = snapshot.DocumentId;
        var revision = snapshot.DocumentVersion;
        var extractionVersion = snapshot.Extraction.ExtractionVersion;
        var state = await GetStateAsync(tenantId, documentId, cancellationToken);
        if (state is not null && state.IsServing(revision) && state.ActiveExtractionVersion == extractionVersion)
        {
            // Already projected. Finish any retraction a previous run left behind, then stop.
            await SettlePendingAsync(state, cancellationToken);
            return;
        }

        var manifest = snapshot.Assertions
            .Select(assertion => new GraphManifestEntry(
                assertion.AssertionId, assertion.SubjectEntityId, assertion.Predicate, assertion.ObjectEntityId, assertion.Evidence.ChunkId))
            .ToList();
        var current = state ?? EmptyState(tenantId, documentId);

        // Anything already pending stays listed until it is settled, so nothing written by an earlier attempt
        // is forgotten.
        var pending = current with
        {
            Status = current.Status == GraphProjectionStatus.Ready ? GraphProjectionStatus.Ready : GraphProjectionStatus.Pending,
            PendingRevision = revision,
            PendingManifest = Union(current.PendingManifest, manifest),
            UpdatedAtUtc = time.GetUtcNow()
        };
        var token = await store.SaveDocumentStateAsync(pending, null, cancellationToken);

        await UpsertEntitiesAsync(tenantId, snapshot.Entities, cancellationToken);
        await store.UpsertAssertionsAsync(snapshot.Assertions, cancellationToken);

        var kept = manifest.Select(entry => entry.AssertionId).ToHashSet(StringComparer.Ordinal);
        var superseded = Union(current.Manifest, pending.PendingManifest)
            .Where(entry => !kept.Contains(entry.AssertionId))
            .ToList();
        var ready = pending with
        {
            Status = GraphProjectionStatus.Ready,
            ActiveRevision = revision,
            ActiveExtractionVersion = extractionVersion,
            Manifest = manifest,
            PendingRevision = null,
            PendingManifest = superseded,
            AccessScopeRef = snapshot.Assertions.Select(assertion => assertion.Evidence.AccessScopeRef).FirstOrDefault(reference => reference is not null) ?? current.AccessScopeRef,
            FailureCode = null,
            UpdatedAtUtc = time.GetUtcNow(),
            ConcurrencyToken = token
        };
        token = await store.SaveDocumentStateAsync(ready, null, cancellationToken);
        await SettlePendingAsync(ready with { ConcurrencyToken = token }, cancellationToken);
        logger.LogInformation(
            "Projected graph revision {Revision} of document {DocumentId}: {EntityCount} entities, {AssertionCount} assertions, {RetractedCount} retracted.",
            revision, documentId, snapshot.Entities.Count, snapshot.Assertions.Count, superseded.Count);
    }

    public async Task RetractDocumentAsync(string tenantId, string documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var state = await GetStateAsync(tenantId, documentId, cancellationToken);
        if (state is { Status: GraphProjectionStatus.Tombstoned, Manifest.Count: 0, PendingManifest.Count: 0 })
        {
            return;
        }

        // The tombstone is written first and on its own, so traversal stops serving the document before any
        // of its assertions are touched.
        var current = state ?? EmptyState(tenantId, documentId);
        var tombstone = current with
        {
            Status = GraphProjectionStatus.Tombstoned,
            ActiveRevision = null,
            ActiveExtractionVersion = null,
            PendingRevision = null,
            Manifest = [],
            PendingManifest = Union(current.Manifest, current.PendingManifest),
            UpdatedAtUtc = time.GetUtcNow()
        };
        var token = await store.SaveDocumentStateAsync(tombstone, RetractedRetention, cancellationToken);
        await SettlePendingAsync(tombstone with { ConcurrencyToken = token }, cancellationToken);
        logger.LogInformation("Tombstoned the graph of document {DocumentId} and retracted {RetractedCount} assertions.", documentId, tombstone.PendingManifest.Count);
    }

    public async Task MarkFailedAsync(string tenantId, string documentId, string revision, string failureCode, CancellationToken cancellationToken)
    {
        await UpdateStateAsync(tenantId, documentId, state => state with
        {
            Status = state.Status == GraphProjectionStatus.Tombstoned ? GraphProjectionStatus.Tombstoned : GraphProjectionStatus.Failed,
            PendingRevision = revision,
            FailureCode = failureCode,
            UpdatedAtUtc = time.GetUtcNow()
        }, createIfMissing: true, cancellationToken);
    }

    public async Task UpdateAccessScopeAsync(string tenantId, string documentId, string? accessScopeRef, CancellationToken cancellationToken)
    {
        // Evidence eligibility is always decided by the live permission filter at query time; this only keeps
        // the recorded scope current for reconciliation and operators.
        await UpdateStateAsync(tenantId, documentId, state => state with
        {
            AccessScopeRef = accessScopeRef,
            UpdatedAtUtc = time.GetUtcNow()
        }, createIfMissing: false, cancellationToken);
    }

    public async Task MergeEntitiesAsync(string tenantId, string sourceEntityId, string targetEntityId, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!GraphIds.IsStorageSafe(sourceEntityId) || !GraphIds.IsStorageSafe(targetEntityId))
        {
            throw new ArgumentException("Both entity IDs must be valid keys.");
        }

        if (sourceEntityId == targetEntityId)
        {
            throw new ArgumentException("An entity cannot be merged into itself.");
        }

        if (TypePrefix(sourceEntityId) != TypePrefix(targetEntityId))
        {
            throw new ArgumentException("Only entities of the same type can be merged.");
        }

        for (var attempt = 0; ; attempt++)
        {
            var records = await store.GetEntitiesAsync(tenantId, [sourceEntityId, targetEntityId], cancellationToken);
            var source = records.FirstOrDefault(record => record.Entity.EntityId == sourceEntityId)
                ?? throw new KeyNotFoundException("The source entity does not exist.");
            var target = records.FirstOrDefault(record => record.Entity.EntityId == targetEntityId)
                ?? throw new KeyNotFoundException("The target entity does not exist.");
            if (source.RedirectToEntityId == targetEntityId && target.MergedEntityIds.Contains(sourceEntityId))
            {
                return;
            }

            if (target.RedirectToEntityId is not null)
            {
                throw new InvalidOperationException("The target entity has itself been merged; merge into its survivor instead.");
            }

            if (source.RedirectToEntityId is not null && source.RedirectToEntityId != targetEntityId)
            {
                throw new InvalidOperationException("The source entity has already been merged into another entity.");
            }

            var now = time.GetUtcNow();
            var mergedIds = target.MergedEntityIds
                .Append(sourceEntityId)
                .Concat(source.MergedEntityIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            try
            {
                // The survivor is written first: if the second write fails, traversal from the survivor already
                // reaches the source's assertions and the merge can simply be repeated.
                await store.SaveEntityAsync(target with
                {
                    MergedEntityIds = mergedIds,
                    History = AppendHistory(target.History, new GraphEntityHistoryEntry("mergedFrom", sourceEntityId, actor, now))
                }, cancellationToken);
                await store.SaveEntityAsync(source with
                {
                    RedirectToEntityId = targetEntityId,
                    History = AppendHistory(source.History, new GraphEntityHistoryEntry("mergedInto", targetEntityId, actor, now))
                }, cancellationToken);
                logger.LogInformation("Merged graph entity {SourceEntityId} into {TargetEntityId}.", sourceEntityId, targetEntityId);
                return;
            }
            catch (GraphConcurrencyException) when (attempt < MaxConflictRetries)
            {
                GraphRagMetrics.Conflict("entity");
            }
        }
    }

    private async Task UpsertEntitiesAsync(string tenantId, IReadOnlyList<GraphEntity> entities, CancellationToken cancellationToken)
    {
        foreach (var batch in entities.Chunk(100))
        {
            var existing = (await store.GetEntitiesAsync(tenantId, batch.Select(entity => entity.EntityId).ToList(), cancellationToken))
                .ToDictionary(record => record.Entity.EntityId, StringComparer.Ordinal);
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (entity, token) =>
            {
                existing.TryGetValue(entity.EntityId, out var record);
                for (var attempt = 0; ; attempt++)
                {
                    var merged = Merge(record, entity);
                    if (merged is null)
                    {
                        return;
                    }

                    try
                    {
                        await store.SaveEntityAsync(merged, token);
                        return;
                    }
                    catch (GraphConcurrencyException exception)
                    {
                        // Another document is writing the same entity. Re-read and merge onto its version.
                        GraphRagMetrics.Conflict("entity");
                        if (attempt >= MaxConflictRetries)
                        {
                            throw new GraphTransientFailureException("entity.conflict", "An entity kept changing while it was being merged.", exception);
                        }
                        record = (await store.GetEntitiesAsync(tenantId, [entity.EntityId], token)).FirstOrDefault();
                    }
                }
            });
        }
    }

    /// <summary>
    /// Folds a snapshot's view of an entity into the stored one, or returns null when it adds nothing. The
    /// first canonical name wins, other names become aliases, and existing attributes are never overwritten,
    /// so documents processed in any order converge on the same record.
    /// </summary>
    internal static GraphEntityRecord? Merge(GraphEntityRecord? stored, GraphEntity incoming)
    {
        if (stored is null)
        {
            return new GraphEntityRecord(
                incoming with
                {
                    Aliases = DistinctAliases(incoming.CanonicalName, incoming.Aliases).Take(MaxStoredAliases).ToList(),
                    Attributes = incoming.Attributes.Take(MaxStoredAttributes).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                },
                null, [], [], null);
        }

        var entity = stored.Entity;
        var aliases = DistinctAliases(entity.CanonicalName, entity.Aliases.Append(incoming.CanonicalName).Concat(incoming.Aliases))
            .Take(MaxStoredAliases)
            .ToList();
        var attributes = new Dictionary<string, string>(entity.Attributes, StringComparer.Ordinal);
        foreach (var (key, value) in incoming.Attributes)
        {
            if (attributes.Count >= MaxStoredAttributes)
            {
                break;
            }
            attributes.TryAdd(key, value);
        }

        if (aliases.Count == entity.Aliases.Count && attributes.Count == entity.Attributes.Count)
        {
            return null;
        }
        return stored with { Entity = entity with { Aliases = aliases, Attributes = attributes } };
    }

    private static IEnumerable<string> DistinctAliases(string canonicalName, IEnumerable<string> aliases)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { GraphNormalization.NormalizeName(canonicalName) };
        foreach (var alias in aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias) && seen.Add(GraphNormalization.NormalizeName(alias)))
            {
                yield return alias;
            }
        }
    }

    /// <summary>Retracts what the state lists as pending and clears the list.</summary>
    private async Task SettlePendingAsync(GraphDocumentState state, CancellationToken cancellationToken)
    {
        if (state.PendingManifest.Count == 0)
        {
            return;
        }

        var keys = state.PendingManifest
            .Select(entry => new GraphAssertionKey(state.TenantId, entry.AssertionId, entry.SubjectEntityId, entry.ObjectEntityId))
            .ToList();
        await store.SetAssertionStatusAsync(keys, GraphAssertionStatus.Retracted, RetractedRetention, cancellationToken);
        var ttl = state.Status == GraphProjectionStatus.Tombstoned ? RetractedRetention : (TimeSpan?)null;
        await store.SaveDocumentStateAsync(state with { PendingManifest = [], UpdatedAtUtc = time.GetUtcNow() }, ttl, cancellationToken);
    }

    private async Task UpdateStateAsync(
        string tenantId, string documentId, Func<GraphDocumentState, GraphDocumentState> change, bool createIfMissing, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = await GetStateAsync(tenantId, documentId, cancellationToken);
            if (state is null && !createIfMissing)
            {
                return;
            }

            try
            {
                await store.SaveDocumentStateAsync(change(state ?? EmptyState(tenantId, documentId)), null, cancellationToken);
                return;
            }
            catch (GraphConcurrencyException) when (attempt < MaxConflictRetries)
            {
                GraphRagMetrics.Conflict("documentState");
            }
        }
    }

    private async Task<GraphDocumentState?> GetStateAsync(string tenantId, string documentId, CancellationToken cancellationToken) =>
        (await store.GetDocumentStatesAsync(tenantId, [documentId], cancellationToken)).FirstOrDefault();

    private GraphDocumentState EmptyState(string tenantId, string documentId) =>
        new(tenantId, documentId, GraphProjectionStatus.Pending, null, null, [], null, [], null, null, time.GetUtcNow());

    private static IReadOnlyList<GraphManifestEntry> Union(IReadOnlyList<GraphManifestEntry> first, IReadOnlyList<GraphManifestEntry> second) =>
        first.Concat(second).DistinctBy(entry => entry.AssertionId, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<GraphEntityHistoryEntry> AppendHistory(IReadOnlyList<GraphEntityHistoryEntry> history, GraphEntityHistoryEntry entry) =>
        history.Append(entry).TakeLast(MaxHistoryEntries).ToList();

    private static string TypePrefix(string entityId)
    {
        var separator = entityId.IndexOf(':');
        return separator < 0 ? "" : entityId[..separator];
    }
}
