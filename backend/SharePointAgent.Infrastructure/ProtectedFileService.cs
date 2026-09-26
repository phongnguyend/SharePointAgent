using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.InformationProtection;
using Microsoft.InformationProtection.File;
using Microsoft.InformationProtection.Exceptions;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public interface IProtectedFileService
{
    Task EnsureReadableAsync(string path, string fileName, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>Checks downloaded files with MIP and decrypts an authorized local copy, retaining the original.</summary>
public sealed class ProtectedFileService(IOptions<SharePointOptions> options, ILogger<ProtectedFileService>? logger = null) : IProtectedFileService, IDisposable
{
    public const string ProtectedOriginalSuffix = ".mip-protected";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MipContext? _context;
    private IFileProfile? _profile;
    private IFileEngine? _engine;

    public async Task EnsureReadableAsync(string path, string fileName, int maxBytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureContext();
            var original = path + ProtectedOriginalSuffix;
            var hasOriginal = System.IO.File.Exists(original);
            var source = hasOriginal ? original : path;
            using var input = System.IO.File.OpenRead(source);
            // Pass the real name: staging files and retained originals have a different extension.
            var status = FileHandler.GetFileStatus(input, fileName, _context);
            logger?.LogInformation("Protection inspection: TraceId={TraceId}, FileName={FileName}, Bytes={Bytes}, Protected={Protected}, Labeled={Labeled}.",
                System.Diagnostics.Activity.Current?.TraceId.ToString(), fileName, input.Length, status.IsProtected(), status.IsLabeled());
            if (!status.IsProtected())
            {
                if (hasOriginal)
                    throw new InvalidDataException("The retained protected original is invalid. Refresh the document.");
                return;
            }

            input.Position = 0;
            var engine = await GetEngineAsync();
            cancellationToken.ThrowIfCancellationRequested();
            using var handler = await engine.CreateFileHandlerAsync(input, fileName, false);
            cancellationToken.ThrowIfCancellationRequested();
            if (handler.Protection is not { } protection || !protection.AccessCheck("EXTRACT"))
                throw new ProtectedDocumentAccessDeniedException();

            // Cached copies still require an authorization check, but retain the user's local edits.
            if (hasOriginal)
            {
                using var cached = System.IO.File.OpenRead(path);
                // Retry any empty cache copy left by an earlier failed stream copy.
                if (cached.Length > 0 && !FileHandler.GetFileStatus(cached, fileName, _context).IsProtected())
                    return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var plaintext = await handler.GetDecryptedTemporaryStreamAsync();
            logger?.LogInformation("Decrypted stream: TraceId={TraceId}, FileName={FileName}, Seekable={Seekable}, Length={Length}, Position={Position}.",
                System.Diagnostics.Activity.Current?.TraceId.ToString(), fileName, plaintext.CanSeek,
                plaintext.CanSeek ? plaintext.Length : null, plaintext.CanSeek ? plaintext.Position : null);
            // MIP returns the seekable stream positioned at its end; copy the entire document.
            if (plaintext.CanSeek) plaintext.Position = 0;
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".decrypting";
            try
            {
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await plaintext.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        total += read;
                        if (total > maxBytes) throw new FileTooLargeException(total, maxBytes);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!hasOriginal) System.IO.File.Copy(path, original, overwrite: false);
                input.Dispose();
                System.IO.File.Move(staging, path, overwrite: true);
                logger?.LogInformation("Decrypted file ready: TraceId={TraceId}, FileName={FileName}, Bytes={Bytes}.",
                    System.Diagnostics.Activity.Current?.TraceId.ToString(), fileName, new FileInfo(path).Length);
            }
            finally
            {
                if (System.IO.File.Exists(staging)) System.IO.File.Delete(staging);
            }
        }
        catch (NoPermissionsException ex)
        {
            // MIP can reject the consumption license while creating the handler, before AccessCheck.
            throw new ProtectedDocumentAccessDeniedException(ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Microsoft Information Protection native libraries could not be loaded. Install the MIP runtime for this host before downloading protected documents.", ex);
        }
        finally { _gate.Release(); }
    }

    private void EnsureContext()
    {
        if (_context is not null) return;
        MIP.Initialize(MipComponent.File);
        var app = new ApplicationInfo
        {
            ApplicationId = options.Value.ClientId,
            ApplicationName = "SharePoint Agent",
            ApplicationVersion = "1.0.0"
        };
        _context = MIP.CreateMipContext(new MipConfiguration(app,
            Path.Combine(Path.GetTempPath(), "SharePointAgent", "mip"), Microsoft.InformationProtection.LogLevel.Warning, false, CacheStorageType.InMemory));
    }

    private async Task<IFileEngine> GetEngineAsync()
    {
        if (_engine is not null) return _engine;
        _profile ??= await MIP.LoadFileProfileAsync(new FileProfileSettings(_context!, CacheStorageType.InMemory, new ConsentDelegate())
        {
            CanCacheLicenses = false
        });
        var settings = options.Value;
        _engine = await _profile.AddEngineAsync(new FileEngineSettings(settings.ClientId, new AuthDelegate(settings), "", "en-US")
        {
            Identity = new Identity($"{settings.ClientId}@{settings.TenantId}"),
            ProtectionOnlyEngine = true
        });
        return _engine;
    }

    private sealed class AuthDelegate(SharePointOptions settings) : IAuthDelegate
    {
        private readonly ClientSecretCredential _credential = new(settings.TenantId, settings.ClientId, settings.ClientSecret);
        public string AcquireToken(Identity identity, string authority, string resource, string claims) =>
            _credential.GetToken(new TokenRequestContext([resource.TrimEnd('/') + "/.default"], claims: claims), CancellationToken.None).Token;
    }

    private sealed class ConsentDelegate : IConsentDelegate
    {
        public Consent GetUserConsent(string url) => Consent.Accept;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _profile?.Dispose();
        _context?.ShutDown();
        _context?.Dispose();
        _gate.Dispose();
    }
}
