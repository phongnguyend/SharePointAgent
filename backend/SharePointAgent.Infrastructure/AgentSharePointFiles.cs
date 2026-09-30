using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

/// <summary>Fresh SharePoint transfers to and from explicitly selected sandbox files.</summary>
public sealed class AgentSharePointFiles(
    SharePointClient sharePointClient,
    IOptions<LocalWorkingDirectoryOptions> options,
    ILogger<AgentSharePointFiles> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    private readonly LocalWorkingDirectoryOptions _options = options.Value;

    public async Task<DownloadedFile> DownloadAsync(string itemId, string fileName, CancellationToken cancellationToken, string? destinationPath = null, bool overwrite = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                throw new ArgumentException("A drive item ID is required.", nameof(itemId));
            }

            var path = string.IsNullOrWhiteSpace(destinationPath)
                ? Path.Combine("Downloads", "SharePoint", Guid.NewGuid().ToString("N"), Sanitize(Path.GetFileName(fileName)))
                : destinationPath;
            var destination = new AgentFileSystem(options).Resolve(path);
            if (Directory.Exists(destination) || (!overwrite && File.Exists(destination)))
            {
                throw new ArgumentException("The destination already exists. Choose another file path or set overwrite to true for an existing file.");
            }

            return await FetchAsync(itemId, fileName, cancellationToken, destination, overwrite);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Fetches the item's content and puts it at its local path, over whatever was there.
    /// </summary>
    private async Task<DownloadedFile> FetchAsync(string itemId, string fileName, CancellationToken cancellationToken, string destinationPath, bool overwrite)
    {
        var localPath = new AgentFileSystem(options).Resolve(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        // Streamed to a name beside the target and moved into place, so the file never sits in memory,
        // a cancelled or failed download leaves nothing a later existence check would mistake for a
        // complete copy, and a refresh keeps the copy it is replacing until the new one is whole.
        var stagingPath = $"{localPath}.{Guid.NewGuid():N}.part";
        long size;
        try
        {
            size = await sharePointClient.DownloadReadableToFileAsync(itemId, fileName, stagingPath, _options.Downloads.MaxFileBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PublishDownload(stagingPath, localPath, overwrite);
        }
        catch
        {
            TryDelete(stagingPath);
            TryDelete(stagingPath + ProtectedFileService.ProtectedOriginalSuffix);
            throw;
        }

        logger.LogInformation(
            "Downloaded {FileName} ({Bytes} bytes) from SharePoint to {LocalPath}.",
            Path.GetFileName(localPath),
            size,
            localPath);

        return new DownloadedFile(localPath, Path.GetFileName(localPath), size, AlreadyOnDisk: false);
    }

    /// <summary>
    /// Sends the explicitly specified sandbox file to a drive item as a new version.
    /// The source and other downloaded files are left unchanged.
    /// </summary>
    /// <exception cref="FileNotFoundException">The specified source file does not exist.</exception>
    public async Task<UploadedFileVersion> UploadAsync(string itemId, string fileName, string sourcePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try

        {
            return await UploadCoreAsync(itemId, fileName, cancellationToken, sourcePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UploadedFileVersion> UploadCoreAsync(string itemId, string fileName, CancellationToken cancellationToken, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("A sourcePath is required for upload.", nameof(sourcePath));
        }

        var localPath = new AgentFileSystem(options).Resolve(sourcePath);
        if (Directory.Exists(localPath))
        {
            throw new ArgumentException("The upload source must be a file, not a directory.");
        }

        var local = new FileInfo(localPath);
        if (!local.Exists)
        {
            throw new FileNotFoundException("The upload source file does not exist.", localPath);
        }

        if (File.Exists(localPath + ProtectedFileService.ProtectedOriginalSuffix))
        {
            throw new InvalidOperationException("This local copy was decrypted from a protected document. Upload is blocked to preserve the SharePoint document's protection. Save changes through a protection-aware Office application.");
        }

        var version = await sharePointClient.UploadFileAsync(itemId, localPath, _options.Downloads.MaxFileBytes, cancellationToken);

        logger.LogInformation(
            "Uploaded {LocalPath} ({Bytes} bytes) back to SharePoint as a new version of {FileName}.",
            localPath,
            local.Length,
            version.Name);

        return version;
    }

    private static string Sanitize(string value)
    {
        var sanitized = new string((string.IsNullOrWhiteSpace(value) ? "file" : value)
            .Select(c => InvalidNameChars.Contains(c) ? '_' : c)
            .ToArray())
            .Trim('.', ' ');

        return sanitized.Length == 0 ? "file" : sanitized;
    }

    private static void PublishDownload(string stagingPath, string localPath, bool overwrite)
    {
        var original = localPath + ProtectedFileService.ProtectedOriginalSuffix;
        var stagedOriginal = stagingPath + ProtectedFileService.ProtectedOriginalSuffix;
        var backup = original + "." + Guid.NewGuid().ToString("N") + ".backup";
        var hadOriginal = File.Exists(original);
        if (hadOriginal)
        {
            File.Move(original, backup);
        }

        try
        {
            if (File.Exists(stagedOriginal))
            {
                File.Move(stagedOriginal, original);
            }

            File.Move(stagingPath, localPath, overwrite);
        }
        catch
        {
            File.Delete(original);
            if (hadOriginal)
            {
                File.Move(backup, original);
            }

            throw;
        }
        TryDelete(backup);
    }

    public void Dispose() => _gate.Dispose();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
