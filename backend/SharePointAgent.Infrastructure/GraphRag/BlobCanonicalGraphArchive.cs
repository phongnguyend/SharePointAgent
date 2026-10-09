using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Versioned snapshot archive in Blob Storage, one immutable JSON blob per tenant, document, revision, and
/// extraction version. It is the source of truth the graph store is rebuilt from: a schema migration, a new
/// partition layout, or a different graph database is a replay of these blobs, not a new round of extraction.
/// Snapshots hold names, IDs, hashes, and provenance, never chunk text.
/// </summary>
public sealed class BlobCanonicalGraphArchive : ICanonicalGraphArchive
{
    public const string LayoutVersion = "v1";

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;

    public BlobCanonicalGraphArchive(IOptions<GraphRagOptions> options)
        : this(CreateContainer(options.Value.Archive))
    {
    }

    internal BlobCanonicalGraphArchive(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task<CanonicalGraphSnapshot> SaveAsync(CanonicalGraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(BlobName(snapshot.TenantId, snapshot.DocumentId, snapshot.DocumentVersion, snapshot.Extraction.ExtractionVersion));
        var content = BinaryData.FromObjectAsJson(snapshot, SerializerOptions);
        try
        {
            await blob.UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Metadata = new Dictionary<string, string>
                {
                    ["schemaversion"] = snapshot.SchemaVersion,
                    ["extractionversion"] = snapshot.Extraction.ExtractionVersion
                }
            }, cancellationToken);
            return snapshot;
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // Archived already, perhaps by a concurrent delivery of the same request. The first copy is kept so
            // that every projection of this revision and extraction version is of the same snapshot.
            return await TryLoadAsync(snapshot.TenantId, snapshot.DocumentId, snapshot.DocumentVersion, snapshot.Extraction.ExtractionVersion, cancellationToken)
                ?? throw new GraphTransientFailureException("archive.unreadable", "An archived snapshot exists but could not be read back.", exception);
        }
    }

    public async Task<CanonicalGraphSnapshot?> TryLoadAsync(
        string tenantId, string documentId, string documentVersion, string extractionVersion, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(BlobName(tenantId, documentId, documentVersion, extractionVersion));
        try
        {
            var download = await blob.DownloadContentAsync(cancellationToken);
            var snapshot = download.Value.Content.ToObjectFromJson<CanonicalGraphSnapshot>(SerializerOptions);

            // A blob that does not describe what its name says is ignored rather than projected.
            return snapshot is not null && snapshot.TenantId == tenantId && snapshot.DocumentId == documentId
                && snapshot.DocumentVersion == documentVersion && snapshot.Extraction?.ExtractionVersion == extractionVersion
                ? snapshot
                : null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>v1/{tenant}/{document hash}/{revision}/{extraction version}.json</c>. The document ID is hashed so
    /// that no drive or item identifier appears in a blob name.
    /// </summary>
    internal static string BlobName(string tenantId, string documentId, string documentVersion, string extractionVersion)
    {
        var documentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(documentId)), 0, 16);
        return $"{LayoutVersion}/{Segment(tenantId)}/{documentHash}/{Segment(documentVersion)}/{Segment(extractionVersion)}.json";
    }

    private static string Segment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-');
        }
        return builder.ToString();
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _initialization.Release();
        }
    }

    private static BlobContainerClient CreateContainer(GraphArchiveOptions options) => options.UsedManagedIdentity
        ? new BlobServiceClient(new Uri(options.ServiceUri!), DependencyInjection.CreateManagedIdentityCredential()).GetBlobContainerClient(options.ContainerName)
        : new BlobContainerClient(options.ConnectionString!, options.ContainerName);
}
