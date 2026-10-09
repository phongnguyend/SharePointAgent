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

    Task SaveAsync(string scope, Stream archive, CancellationToken cancellationToken);

    Task DeleteAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>Which sandbox serves a workspace scope: its exposed SandboxHost address and per-sandbox API key.</summary>
public sealed record SandboxBinding(Uri Endpoint, string ApiKey);

public interface ISandboxRegistry
{
    Task<SandboxBinding?> GetAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>
/// Workspace state in one private Blob container: <c>snapshots/{scope}.zip</c> for dynamic sessions and
/// <c>sandboxes/{scope}.json</c> for sandbox bindings. The bindings are written by whatever provisions the
/// sandboxes (the sandbox SDK or CLI), which keeps the provisioning API, still changing between versions,
/// out of this application.
/// </summary>
public sealed class BlobWorkspaceStorage : IWorkspaceSnapshotStore, ISandboxRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient _container;
    private readonly SandboxesWorkspaceOptions _sandboxes;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;

    public BlobWorkspaceStorage(IOptions<AgentWorkspaceOptions> options)
        : this(CreateContainer(options.Value.Snapshots), options.Value.Sandboxes)
    {
    }

    internal BlobWorkspaceStorage(BlobContainerClient container, SandboxesWorkspaceOptions sandboxes)
    {
        _container = container;
        _sandboxes = sandboxes;
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
            var download = await _container.GetBlobClient($"sandboxes/{scope}.json").DownloadContentAsync(cancellationToken);
            var binding = download.Value.Content.ToObjectFromJson<SandboxBindingDocument>(Json);
            if (binding is not null && Uri.TryCreate(binding.Endpoint, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(binding.ApiKey))
            {
                return new SandboxBinding(endpoint, binding.ApiKey);
            }
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
        }

        return Uri.TryCreate(_sandboxes.SharedEndpoint, UriKind.Absolute, out var shared) && !string.IsNullOrWhiteSpace(_sandboxes.SharedApiKey)
            ? new SandboxBinding(shared, _sandboxes.SharedApiKey)
            : null;
    }

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

    private sealed record SandboxBindingDocument(string? Endpoint, string? ApiKey);
}
