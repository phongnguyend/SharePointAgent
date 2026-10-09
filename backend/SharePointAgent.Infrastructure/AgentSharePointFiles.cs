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

    /// <summary>
    /// Downloads into the given working directory and returns the path as the agent sees it. A local
    /// workspace is written in place as before. An isolated one gets the file streamed in from a staging
    /// copy on this host, with its protected original beside it, so SharePoint credentials and decryption
    /// never run where agent code does.
    /// </summary>
    public async Task<DownloadedFile> DownloadAsync(
        IAgentWorkspace workspace, string itemId, string fileName, CancellationToken cancellationToken, string? destinationPath = null, bool overwrite = false)
    {
        if (!workspace.IsIsolated)
        {
            var local = await DownloadAsync(itemId, fileName, cancellationToken, destinationPath, overwrite);
            return local with { LocalPath = workspace.Normalize(local.LocalPath) };
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("A drive item ID is required.", nameof(itemId));
        }

        var path = workspace.Normalize(string.IsNullOrWhiteSpace(destinationPath)
            ? $"Downloads/SharePoint/{Guid.NewGuid():N}/{Sanitize(Path.GetFileName(fileName))}"
            : destinationPath);
        if (path == "." || await workspace.FindAsync(path, cancellationToken) is { } existing && (existing.IsDirectory || !overwrite))
        {
            throw new ArgumentException("The destination already exists. Choose another file path or set overwrite to true for an existing file.");
        }

        var staging = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sharepoint-agent-staging", Guid.NewGuid().ToString("N")));
        try
        {
            var stagedFile = Path.Combine(staging.FullName, Sanitize(Path.GetFileName(fileName)));
            var size = await sharePointClient.DownloadReadableToFileAsync(itemId, fileName, stagedFile, _options.Downloads.MaxFileBytes, cancellationToken);
            var stagedOriginal = stagedFile + ProtectedFileService.ProtectedOriginalSuffix;
            var sidecar = path + ProtectedFileService.ProtectedOriginalSuffix;

            // The protection marker goes in first, so there is never a moment when a decrypted copy sits in
            // the workspace without the marker that blocks uploading it.
            if (File.Exists(stagedOriginal))
            {
                await using var original = File.OpenRead(stagedOriginal);
                await workspace.WriteAsync(sidecar, original, overwrite: true, cancellationToken);
            }

            await using (var content = File.OpenRead(stagedFile))
            {
                await workspace.WriteAsync(path, content, overwrite, cancellationToken);
            }

            if (!File.Exists(stagedOriginal) && await workspace.FindAsync(sidecar, cancellationToken) is not null)
            {
                await workspace.DeleteAsync(sidecar, recursive: false, cancellationToken);
            }

            logger.LogInformation("Downloaded {FileName} ({Bytes} bytes) from SharePoint into the isolated working directory.", Path.GetFileName(path), size);
            return new DownloadedFile(path, Path.GetFileName(path), size, AlreadyOnDisk: false);
        }
        finally
        {
            TryDeleteDirectory(staging.FullName);
        }
    }

    /// <summary>Uploads a file from the given working directory. Isolated workspaces are copied out to this host first.</summary>
    public async Task<UploadedFileVersion> UploadAsync(
        IAgentWorkspace workspace, string itemId, string fileName, string sourcePath, CancellationToken cancellationToken)
    {
        if (!workspace.IsIsolated)
        {
            return await UploadAsync(itemId, fileName, sourcePath, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("A sourcePath is required for upload.", nameof(sourcePath));
        }

        var path = workspace.Normalize(sourcePath);
        var entry = await workspace.FindAsync(path, cancellationToken)
            ?? throw new FileNotFoundException("The upload source file does not exist.", path);
        if (entry.IsDirectory)
        {
            throw new ArgumentException("The upload source must be a file, not a directory.");
        }

        if (await workspace.FindAsync(path + ProtectedFileService.ProtectedOriginalSuffix, cancellationToken) is not null)
        {
            throw new InvalidOperationException("This local copy was decrypted from a protected document. Upload is blocked to preserve the SharePoint document's protection. Save changes through a protection-aware Office application.");
        }

        var file = await workspace.ReadAsync(path, cancellationToken, _options.Downloads.MaxFileBytes);
        var staging = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sharepoint-agent-staging", Guid.NewGuid().ToString("N")));
        try
        {
            var stagedFile = Path.Combine(staging.FullName, Sanitize(file.Name));
            await File.WriteAllBytesAsync(stagedFile, file.Content, cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var version = await sharePointClient.UploadFileAsync(itemId, stagedFile, _options.Downloads.MaxFileBytes, cancellationToken);
                logger.LogInformation("Uploaded {Bytes} bytes from the isolated working directory as a new version of {FileName}.", file.Content.Length, version.Name);
                return version;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            TryDeleteDirectory(staging.FullName);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
