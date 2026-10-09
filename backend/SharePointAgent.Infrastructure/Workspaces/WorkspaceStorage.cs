using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.Workspaces;

/// <summary>Zipped copies of workspaces whose environment does not keep files, keyed by workspace scope.</summary>
public interface IWorkspaceSnapshotStore
{
    Task<Stream?> OpenAsync(string scope, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string scope, CancellationToken cancellationToken);

    Task SaveAsync(string scope, Stream archive, CancellationToken cancellationToken);

    Task DeleteAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>
/// Which sandbox serves a workspace scope: its ID in the sandbox group (null for a shared development
/// sandbox), its exposed SandboxHost address, and its per-sandbox API key.
/// </summary>
public sealed record SandboxBinding(string? SandboxId, Uri Endpoint, string ApiKey);

/// <summary>The scope-to-sandbox bindings the provisioner keeps.</summary>
public interface ISandboxRegistry
{
    Task<SandboxBinding?> GetAsync(string scope, CancellationToken cancellationToken);

    /// <summary>Records a new binding unless one already exists, so concurrent first turns settle on one sandbox.</summary>
    Task<bool> TryAddAsync(string scope, SandboxBinding binding, CancellationToken cancellationToken);

    Task SaveAsync(string scope, SandboxBinding binding, CancellationToken cancellationToken);

    Task DeleteBindingAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>
/// Workspace state in one private Blob container: <c>snapshots/{scope}.zip</c> for dynamic sessions and
/// <c>sandboxes/{scope}.json</c> for the bindings of the sandboxes the provisioner creates. The container is
/// private and reached with the application's identity; the bindings hold per-sandbox keys.
/// </summary>
public sealed class BlobWorkspaceStorage : IWorkspaceSnapshotStore, ISandboxRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;

    public BlobWorkspaceStorage(IOptions<AgentWorkspaceOptions> options)
        : this(CreateContainer(options.Value.Snapshots))
    {
    }

    internal BlobWorkspaceStorage(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task<Stream?> OpenAsync(string scope, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        try
        {
            var download = await _container.GetBlobClient(SnapshotName(scope)).DownloadStreamingAsync(cancellationToken: cancellationToken);
            return download.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string scope, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        return (await _container.GetBlobClient(SnapshotName(scope)).ExistsAsync(cancellationToken)).Value;
    }

    public async Task SaveAsync(string scope, Stream archive, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(SnapshotName(scope)).UploadAsync(archive, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" }
        }, cancellationToken);
    }

    public async Task DeleteAsync(string scope, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(SnapshotName(scope)).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<SandboxBinding?> GetAsync(string scope, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        try
        {
            var download = await _container.GetBlobClient(BindingName(scope)).DownloadContentAsync(cancellationToken);
            var binding = download.Value.Content.ToObjectFromJson<SandboxBindingDocument>(Json);
            return binding is not null && Uri.TryCreate(binding.Endpoint, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(binding.ApiKey)
                ? new SandboxBinding(binding.SandboxId, endpoint, binding.ApiKey)
                : null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> TryAddAsync(string scope, SandboxBinding binding, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        try
        {
            await _container.GetBlobClient(BindingName(scope)).UploadAsync(Serialize(binding),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            return false;
        }
    }

    public async Task SaveAsync(string scope, SandboxBinding binding, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(BindingName(scope)).UploadAsync(Serialize(binding), overwrite: true, cancellationToken);
    }

    public async Task DeleteBindingAsync(string scope, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(BindingName(scope)).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    private static BinaryData Serialize(SandboxBinding binding) =>
        BinaryData.FromObjectAsJson(new SandboxBindingDocument(binding.SandboxId, binding.Endpoint.ToString(), binding.ApiKey), Json);

    private static string BindingName(string scope) => $"sandboxes/{scope}.json";

    internal static string SnapshotName(string scope) => $"snapshots/{scope}.zip";

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

    private static BlobContainerClient CreateContainer(WorkspaceSnapshotOptions options) => options.UsedManagedIdentity
        ? new BlobServiceClient(new Uri(options.ServiceUri!), DependencyInjection.CreateManagedIdentityCredential()).GetBlobContainerClient(options.ContainerName)
        : new BlobContainerClient(options.ConnectionString!, options.ContainerName);

    private sealed record SandboxBindingDocument(string? SandboxId, string? Endpoint, string? ApiKey);
}
