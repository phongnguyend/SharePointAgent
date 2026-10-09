using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace SharePointAgent.Domain;

/// <summary>
/// Normalization shared by entity keys and predicates. Names are compared after Unicode compatibility
/// folding, whitespace collapsing, and lowercasing only: punctuation is kept, so "C#" and "C++" stay apart,
/// and nothing here merges entities because they are merely similar.
/// </summary>
public static class GraphNormalization
{
    public static string NormalizeName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var folded = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(folded.Length);
        var pendingSpace = false;
        foreach (var character in folded)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    /// <summary>Turns "depends on" or "depends-on" into the ontology's form, <c>DEPENDS_ON</c>.</summary>
    public static string NormalizePredicate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var words = value.Normalize(NormalizationForm.FormKC)
            .Split([' ', '\t', '\r', '\n', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join('_', words).ToUpperInvariant();
    }
}

/// <summary>
/// The key an entity is identified by within its tenant and type, before it is hashed into an ID. An
/// authoritative identifier, such as a system's configuration-item number, is preferred to a name because
/// two different entities can share a name; the scheme keeps the two kinds of key from colliding.
/// </summary>
public static class GraphEntityKey
{
    public static string FromName(string name)
    {
        var normalized = GraphNormalization.NormalizeName(name);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("An entity name must contain a non-whitespace character.", nameof(name));
        }
        return $"name:{normalized}";
    }

    /// <summary>
    /// Builds a key from an identifier issued by an authoritative system. The value keeps its case, because
    /// only the issuing system knows whether its identifiers are case-sensitive; callers fold case first when
    /// they are not.
    /// </summary>
    public static string FromIdentifier(string scheme, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!scheme.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-'))
        {
            throw new ArgumentException("An identifier scheme may contain only lowercase letters, digits, and hyphens.", nameof(scheme));
        }
        return $"id:{scheme}:{value.Normalize(NormalizationForm.FormKC).Trim()}";
    }
}

/// <summary>
/// Deterministic, opaque, tenant-scoped graph identifiers. They are hashes rather than names so that an ID
/// that reaches a log, a trace, or a partition key reveals nothing about the document it came from, and they
/// avoid every character a document store or graph database reserves in keys.
/// <para>
/// The hash input carries a scheme label and length-prefixed fields, so changing how an ID is formed is a
/// visible migration rather than a silent collision.
/// </para>
/// </summary>
public static class GraphIds
{
    /// <summary>Cosmos DB's limit on <c>id</c>, which is also a comfortable limit for other graph stores.</summary>
    public const int MaxIdLength = 255;

    private const string EntityScheme = "graph-entity/v1";

    private const string AssertionScheme = "graph-assertion/v1";

    private const string AssertionPrefix = "assertion:";

    public static string ForEntity(string tenantId, string entityType, string entityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        return $"{EntityPrefix(entityType)}{Hash(EntityScheme, tenantId, entityType, entityKey)}";
    }

    /// <summary>
    /// The ID of one document revision's claim of one relation from one chunk. A symmetric relation is
    /// identified by its unordered pair of entities, so "A integrates with B" and "B integrates with A" from
    /// the same chunk are one assertion.
    /// </summary>
    public static string ForAssertion(
        EvidenceRef evidence, string subjectEntityId, string predicate, string objectEntityId, bool symmetric)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectEntityId);
        if (symmetric && string.CompareOrdinal(subjectEntityId, objectEntityId) > 0)
        {
            (subjectEntityId, objectEntityId) = (objectEntityId, subjectEntityId);
        }
        var hash = Hash(
            AssertionScheme,
            evidence.TenantId,
            evidence.DocumentId,
            evidence.DocumentVersion,
            evidence.ChunkId,
            subjectEntityId,
            predicate,
            objectEntityId);
        return $"{AssertionPrefix}{hash}";
    }

    public static string EntityPrefix(string entityType) => $"{entityType.ToLowerInvariant()}:";

    public static bool IsAssertionId(string? value) => value is not null && value.StartsWith(AssertionPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a value can be used as a key in the graph stores: non-blank, within <see cref="MaxIdLength"/>,
    /// without surrounding whitespace, and free of control characters and of the characters Cosmos DB
    /// rejects in an <c>id</c>.
    /// </summary>
    public static bool IsStorageSafe([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength || value.Trim().Length != value.Length)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) || character is '/' or '\\' or '?' or '#')
            {
                return false;
            }
        }
        return true;
    }

    private static string Hash(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append(part.Length).Append(':').Append(part).Append(';');
        }
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest, 0, 16);
    }
}

/// <summary>
/// Maps the identifiers the SharePoint indexer already uses onto graph evidence, so graph facts point at the
/// same file and search chunk that the existing permission-trimmed search returns.
/// </summary>
public static class GraphDocumentIds
{
    /// <summary>A document is one drive item, the same pair the indexed-file table is keyed by.</summary>
    public static string ForDriveItem(string driveId, string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (driveId.Contains(':') || itemId.Contains(':'))
        {
            throw new ArgumentException("Drive and item IDs must not contain ':'.");
        }
        return $"{driveId}:{itemId}";
    }

    public static bool TryParseDriveItem(string? documentId, out string driveId, out string itemId)
    {
        driveId = "";
        itemId = "";
        var parts = documentId?.Split(':');
        if (parts is not { Length: 2 } || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        driveId = parts[0];
        itemId = parts[1];
        return true;
    }

    /// <summary>
    /// The revision that chunk text belongs to. Chunk keys are reused across re-indexing, and chunk
    /// boundaries depend on the chunking settings as well as the content, so a revision is the file's
    /// content tag together with the index fingerprint. Both are kept in the indexed-file table, so the active
    /// revision can be recomputed at query time.
    /// </summary>
    public static string ForRevision(string cTag, string indexFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexFingerprint);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{cTag.Length}:{cTag};{indexFingerprint.Length}:{indexFingerprint};"));
        return $"rev:{Convert.ToHexStringLower(digest, 0, 16)}";
    }

    /// <summary>
    /// The revision of an indexed file, or null when the table holds neither tag. Files always carry a content
    /// tag; the entity tag is the same fallback the indexer uses when it compares content.
    /// </summary>
    public static string? ForIndexedFile(string? cTag, string? eTag, string? indexFingerprint)
    {
        var tag = string.IsNullOrWhiteSpace(cTag) ? eTag : cTag;
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(indexFingerprint))
        {
            return null;
        }
        return ForRevision(tag, indexFingerprint);
    }

    public static string? ForIndexedFile(FileIndexRecord record) => ForIndexedFile(record.CTag, record.ETag, record.IndexFingerprint);

    public static string ForChunk(string driveId, string itemId, int chunkNumber) => SearchChunkKey.For(driveId, itemId, chunkNumber);

    public const string ChunkContentHashPrefix = "sha256:";

    /// <summary>A hash of a chunk's exact text, so evidence can be checked against what a reader is shown.</summary>
    public static string ForChunkContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ChunkContentHashPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public static bool IsChunkContentHash(string? value) =>
        value is { Length: 71 } && value.StartsWith(ChunkContentHashPrefix, StringComparison.Ordinal)
        && !value.AsSpan(ChunkContentHashPrefix.Length).ContainsAnyExcept("0123456789abcdef");

    /// <summary>
    /// The opaque access-scope reference for a permission hash from the indexed-file table. The hash is
    /// Base64, whose '/' is not allowed in store keys, so it is carried in its URL-safe form.
    /// </summary>
    public static string? ForAccessScope(string? permissionsHash) =>
        string.IsNullOrWhiteSpace(permissionsHash)
            ? null
            : "perm:" + permissionsHash.TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Labels an extraction so that changing the prompt, the model, or the ontology produces a new label, and with
/// it a re-extraction, while repeating the same extraction is recognized as already done.
/// </summary>
public static class GraphExtractionVersion
{
    public static string Compute(string promptVersion, string modelId, string ontologyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ontologyVersion);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{promptVersion.Length}:{promptVersion};{modelId.Length}:{modelId};{ontologyVersion.Length}:{ontologyVersion};"));
        return $"{promptVersion}.{ontologyVersion}.{Convert.ToHexStringLower(digest, 0, 4)}";
    }
}

/// <summary>
/// Spreads one tenant's graph over a fixed number of synthetic partitions, <c>tenantId|bucket</c>, so a large
/// tenant does not become one hot or oversized partition. The bucket is a hash of the record's anchor ID, so
/// the partition of any entity, assertion, or document is computed rather than looked up, and every read can
/// target its partition. Changing the bucket count moves every record, which is a rebuild from the archive.
/// </summary>
public sealed class GraphPartitionLocator
{
    public GraphPartitionLocator(int bucketCount)
    {
        if (bucketCount is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount), "The bucket count must be between 1 and 4096.");
        }
        BucketCount = bucketCount;
    }

    public int BucketCount { get; }

    public string For(string tenantId, string anchorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(anchorId));
        // Read with a fixed byte order so every platform computes the same partition.
        var bucket = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(digest) % (uint)BucketCount;
        return $"{tenantId}|{bucket:D4}";
    }
}
