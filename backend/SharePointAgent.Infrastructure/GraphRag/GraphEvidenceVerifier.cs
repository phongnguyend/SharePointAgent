using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>The assertions whose evidence the reader may use, and the authorized chunks behind them.</summary>
public sealed record GraphEvidenceVerification(
    IReadOnlySet<string> AuthorizedAssertionIds,
    IReadOnlyDictionary<string, SearchQueryHit> Chunks,
    int RejectedCount);

/// <summary>
/// Decides, per assertion and per reader, whether the evidence behind a graph fact can be used. The graph
/// store is never trusted for this. Each assertion must pass all of these checks:
/// <list type="bullet">
/// <item>It belongs to the reader's tenant.</item>
/// <item>Its document revision is the one the indexed-file table records now, so deleted and re-indexed
/// documents stop counting as soon as the indexer has processed them.</item>
/// <item>The existing permission-trimmed search returns its chunk for this reader, so permission changes
/// apply as soon as the index carries them.</item>
/// <item>The chunk's text still hashes to what was extracted.</item>
/// </list>
/// </summary>
public sealed class GraphEvidenceVerifier(
    IAuthorizedChunkStore chunks,
    IFileMetadataRepository metadata,
    IOptions<GraphRagOptions> options)
{
    public async Task<GraphEvidenceVerification> VerifyAsync(
        string tenantId, string userId, IReadOnlyCollection<GraphAssertion> assertions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            // Never fall through to an unfiltered read: without a reader there is nothing to authorize against.
            throw new ArgumentException("Evidence can only be verified for a specific user.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var rejected = 0;
        var inTenant = new List<GraphAssertion>();
        foreach (var assertion in assertions.DistinctBy(assertion => assertion.AssertionId, StringComparer.Ordinal))
        {
            if (assertion.TenantId == tenantId && assertion.Evidence?.TenantId == tenantId)
            {
                inTenant.Add(assertion);
            }
            else
            {
                rejected++;
                GraphRagMetrics.AclRejected("tenant");
            }
        }

        // The current revision of each document, read from the table the indexer writes after the index.
        var documentIds = inTenant.Select(assertion => assertion.Evidence.DocumentId).Distinct(StringComparer.Ordinal).ToList();
        var consideredDocuments = documentIds.Take(options.Value.Traversal.MaxEvidenceDocuments).ToHashSet(StringComparer.Ordinal);
        var revisions = new Dictionary<string, string?>(StringComparer.Ordinal);
        await Task.WhenAll(consideredDocuments.Select(async documentId =>
        {
            string? revision = null;
            if (GraphDocumentIds.TryParseDriveItem(documentId, out var driveId, out var itemId)
                && await metadata.GetAsync(driveId, itemId, cancellationToken) is { } record)
            {
                revision = GraphDocumentIds.ForIndexedFile(record);
            }

            lock (revisions)
            {
                revisions[documentId] = revision;
            }
        }));

        var current = new List<GraphAssertion>();
        foreach (var assertion in inTenant)
        {
            if (!consideredDocuments.Contains(assertion.Evidence.DocumentId))
            {
                rejected++;
                GraphRagMetrics.AclRejected("budget");
            }
            else if (revisions[assertion.Evidence.DocumentId] is not { } revision || revision != assertion.Evidence.DocumentVersion)
            {
                rejected++;
                GraphRagMetrics.AclRejected("revision");
            }
            else
            {
                current.Add(assertion);
            }
        }

        var chunkIds = current.Select(assertion => assertion.Evidence.ChunkId).Distinct(StringComparer.Ordinal).ToList();
        var authorizedChunks = chunkIds.Count == 0
            ? new Dictionary<string, SearchQueryHit>(StringComparer.Ordinal)
            : (await chunks.GetAuthorizedChunksAsync(userId, chunkIds, cancellationToken))
                .DistinctBy(hit => hit.Id, StringComparer.Ordinal)
                .ToDictionary(hit => hit.Id, StringComparer.Ordinal);

        var authorized = new HashSet<string>(StringComparer.Ordinal);
        var usedChunks = new Dictionary<string, SearchQueryHit>(StringComparer.Ordinal);
        foreach (var assertion in current)
        {
            var evidence = assertion.Evidence;
            if (!authorizedChunks.TryGetValue(evidence.ChunkId, out var hit))
            {
                rejected++;
                GraphRagMetrics.AclRejected("unauthorized");
                continue;
            }

            if ($"{hit.DriveId}:{hit.ItemId}" != evidence.DocumentId
                || GraphDocumentIds.ForChunkContent(hit.Content) != evidence.ChunkContentHash)
            {
                rejected++;
                GraphRagMetrics.AclRejected("content");
                continue;
            }

            authorized.Add(assertion.AssertionId);
            usedChunks[hit.Id] = hit;
        }
        return new GraphEvidenceVerification(authorized, usedChunks, rejected);
    }
}
