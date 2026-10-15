using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

/// <summary>Uses the configured upload storage account/container, under a separate skill prefix.</summary>
public sealed class AgentSkillFileStorage(BlobServiceClient blobs, IOptions<UploadOptions> options) : IAgentSkillFileStorage
{
    private BlobContainerClient Container => blobs.GetBlobContainerClient(options.Value.ContainerName);

    public async Task UploadAsync(string storageKey, byte[] content, CancellationToken cancellationToken)
    {
        await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await Container.GetBlobClient(storageKey).UploadAsync(new BinaryData(content), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" }
        }, cancellationToken);
    }

    public async Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var response = await Container.GetBlobClient(storageKey).DownloadContentAsync(cancellationToken);
        return response.Value.Content.ToArray();
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        await Container.GetBlobClient(storageKey).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }
}
