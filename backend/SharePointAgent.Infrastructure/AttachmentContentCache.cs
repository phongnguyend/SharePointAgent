using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

public sealed record CachedAttachmentDownload(string LocalPath, bool CacheHit);
public sealed record CachedAttachmentMarkdown(string Content, string LocalPath, bool DownloadCacheHit, bool MarkdownCacheHit);

public sealed class AttachmentContentCache(
    BlobServiceClient blobs,
    MarkItDownClient converter,
    IOptions<UploadOptions> options,
    IOptions<LocalWorkingDirectoryOptions> workingDirectory) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly UploadOptions settings = options.Value;

    // Attachments sit beside the SharePoint downloads in the agent's working directory, so a file it
    // fetched from a conversation and one it fetched from the library are in the same tree its file
    // tools can list and read.
    private readonly string attachments = workingDirectory.Value.ResolvedAttachmentsDirectory;

    private BlobContainerClient Container => blobs.GetBlobContainerClient(settings.ContainerName);
    private string DirectoryFor(Guid id) => Path.Combine(Path.GetFullPath(attachments), id.ToString("N"));
    private BlobClient MarkdownBlob(Guid id) => Container.GetBlobClient($"markdown-cache/{id:N}/content.md");

    public async Task<CachedAttachmentDownload> DownloadAsync(ChatMessageAttachmentFileEntity file, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try

        {
            return await DownloadCoreAsync(file, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CachedAttachmentDownload> DownloadCoreAsync(ChatMessageAttachmentFileEntity file, CancellationToken ct)
    {
        if (file.SizeBytes > settings.MaxFileBytes)
        {
            throw new UploadTooLargeException(settings.MaxFileBytes);
        }

        var directory = DirectoryFor(file.Id);
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            extension = ".bin";
        }

        var path = Path.Combine(directory, "original" + (string.IsNullOrEmpty(extension) ? ".bin" : extension));
        if (File.Exists(path) && new FileInfo(path).Length == file.SizeBytes)
        {
            return new(path, true);
        }

        var response = await Container.GetBlobClient(file.BlobName).DownloadContentAsync(ct);
        var bytes = response.Value.Content.ToArray();
        if (bytes.LongLength > settings.MaxFileBytes)
        {
            throw new UploadTooLargeException(settings.MaxFileBytes);
        }

        await WriteAtomicallyAsync(path, bytes, ct);
        return new(path, false);
    }

    public async Task<string> ConvertForIndexAsync(ChatMessageAttachmentFileEntity file, CancellationToken ct)
    {
        RejectImageMarkdown(file);
        var source = await DownloadAsync(file, ct);
        if (settings.IsTextFile(file.FileName))
        {
            // Preserve text and line breaks; detect Unicode BOMs and reject invalid UTF-8
            // rather than silently indexing replacement characters.
            using var reader = new StreamReader(source.LocalPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct);
        }
        return await converter.ConvertAsync(file.FileName, await File.ReadAllBytesAsync(source.LocalPath, ct), file.ContentType, ct);
    }

    public async Task StoreMarkdownAsync(Guid id, string markdown, CancellationToken ct)
    {
        await MarkdownBlob(id).UploadAsync(BinaryData.FromString(markdown), overwrite: true, cancellationToken: ct);
    }

    public async Task<CachedAttachmentMarkdown> GetMarkdownAsync(ChatMessageAttachmentFileEntity file, CancellationToken ct)
    {
        if (file.Status != UploadIndexStatus.Indexed)
        {
            throw new AttachmentMarkdownUnavailableException("Markdown is available after successful indexing. Reindex the attachment or wait for indexing to finish.");
        }

        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(DirectoryFor(file.Id));
            var blob = MarkdownBlob(file.Id);
            // Read only the representation used for indexing; validate local copies by blob ETag.
            {
                try
                {
                    var properties = await blob.GetPropertiesAsync(cancellationToken: ct);
                    var cachedPath = MarkdownPath(file.Id, properties.Value.ETag);
                    if (File.Exists(cachedPath))
                    {
                        return new(await File.ReadAllTextAsync(cachedPath, ct), cachedPath, false, true);
                    }

                    var cached = await blob.DownloadContentAsync(ct);
                    cachedPath = MarkdownPath(file.Id, cached.Value.Details.ETag);
                    await WriteAtomicallyAsync(cachedPath, cached.Value.Content.ToArray(), ct);
                    return new(cached.Value.Content.ToString(), cachedPath, false, false);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    throw new AttachmentMarkdownUnavailableException("Indexed Markdown is missing. Reindex the attachment to generate its Markdown blob.");
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void RejectImageMarkdown(ChatMessageAttachmentFileEntity file)
    {
        if (settings.IsImageFile(file.FileName))
        {
            throw new ArgumentException("Image attachments do not have Markdown. Use download_attachment to download the original image.");
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await MarkdownBlob(id).DeleteIfExistsAsync(cancellationToken: ct);
            var directory = DirectoryFor(id);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string MarkdownPath(Guid id, ETag etag) => Path.Combine(DirectoryFor(id),
        $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(etag.ToString())))}.md");

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void Dispose() => gate.Dispose();
}

public sealed class AttachmentMarkdownUnavailableException(string message) : InvalidOperationException(message);
