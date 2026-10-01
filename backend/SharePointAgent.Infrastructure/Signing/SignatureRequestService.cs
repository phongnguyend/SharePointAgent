using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

public sealed class SignatureRequestService(
    IEnumerable<ISignatureProvider> providers,
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    AttachmentContentCache contentCache)
{
    public string[] EnabledProviders => providers.Where(x => x.Enabled).Select(x => x.Name).ToArray();

    private ISignatureProvider Provider(string name) => providers.FirstOrDefault(x => x.Name == name && x.Enabled)
        ?? throw new InvalidOperationException("This signing provider is not enabled. Ask an administrator to configure the shared organization connection.");

    public static void Validate(SignatureInput input)
    {
        if (input.ClientRequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.Subject) || input.Subject.Length > 100 || input.Message?.Length > 2000)
        {
            throw new ArgumentException("Provide a request ID, a subject of 1–100 characters, and a message of at most 2,000 characters.");
        }
        if (input.Recipients is null || input.Recipients.Length is < 1 or > 20 || input.Recipients.Any(x => x is null ||
            string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 100 || string.IsNullOrWhiteSpace(x.Email) || x.Email.Length > 254 || !new EmailAddressAttribute().IsValid(x.Email)))
        {
            throw new ArgumentException("Provide 1–20 recipients, each with a name and valid email address.");
        }
        if (input.Recipients.Select(x => x.Email.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Recipients.Length)
        {
            throw new ArgumentException("Recipient email addresses must be unique.");
        }
    }

    public async Task<SignatureRequestEntity> CreateAsync(Guid attachmentId, Guid userId, SignatureInput input, CancellationToken ct)
    {
        Validate(input);
        var provider = Provider(input.Provider);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var previous = await db.SignatureRequests.SingleOrDefaultAsync(x => x.CreatedById == userId && x.ClientRequestId == input.ClientRequestId, ct);
        if (previous is not null)
        {
            if (previous.AttachmentFileId != attachmentId || previous.Provider != input.Provider)
            {
                throw new ArgumentException("This request ID has already been used for another signing request.");
            }
            return previous;
        }
        var file = await db.ChatMessageAttachmentFiles.SingleOrDefaultAsync(x => x.Id == attachmentId, ct)
            ?? throw new KeyNotFoundException("Attachment file not found.");
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only PDF attachments can be sent for signature.");
        }
        var cached = await contentCache.DownloadAsync(file, ct);
        var pdf = await File.ReadAllBytesAsync(cached.LocalPath, ct);
        if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new ArgumentException("The attachment is not a valid PDF document.");
        }
        var row = new SignatureRequestEntity
        {
            AttachmentFileId = attachmentId, CreatedById = userId, ClientRequestId = input.ClientRequestId,
            Provider = input.Provider, Subject = input.Subject.Trim(), RecipientsJson = JsonSerializer.Serialize(input.Recipients),
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        db.SignatureRequests.Add(row);
        // Persist before contacting the provider. The unique client request ID prevents concurrent duplicate creates.
        await db.SaveChangesAsync(ct);
        try
        {
            row.ExternalId = await provider.CreateDraftAsync(file.FileName, pdf, input, ct);
            row.Status = "Draft";
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return row;
        }
        catch
        {
            // The provider may have received the request even if its response was lost. Never auto-resubmit.
            row.Status = "NeedsReview";
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<List<SignatureRequestEntity>> ListAsync(Guid attachmentId, Guid userId, bool admin, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.SignatureRequests.AsNoTracking().Where(x => x.AttachmentFileId == attachmentId && (admin || x.CreatedById == userId))
            .OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync(ct);
    }

    public async Task<SignatureRequestEntity> FindAsync(Guid attachmentId, Guid requestId, Guid userId, bool admin, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.SignatureRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId && x.AttachmentFileId == attachmentId && (admin || x.CreatedById == userId), ct)
            ?? throw new KeyNotFoundException("Signing request not found.");
    }

    public async Task<SignatureRequestEntity> RefreshAsync(SignatureRequestEntity row, CancellationToken ct)
    {
        if (row.ExternalId is null)
        {
            throw new InvalidOperationException("This request needs administrator review in the provider account before creating a replacement.");
        }
        var status = await Provider(row.Provider).GetStatusAsync(row.ExternalId, ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var current = await db.SignatureRequests.SingleAsync(x => x.Id == row.Id, ct);
        current.Status = status;
        current.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return current;
    }

    public async Task<string> PrepareAsync(SignatureRequestEntity row, CancellationToken ct)
    {
        row = await RefreshAsync(row, ct);
        if (row.Status is not ("created" or "AUTHORING" or "DRAFT"))
        {
            throw new InvalidOperationException("Only draft requests can be opened for preparation.");
        }
        return await Provider(row.Provider).GetPreparationUrlAsync(row.ExternalId!, ct);
    }

    public async Task<byte[]> DownloadAsync(SignatureRequestEntity row, bool audit, CancellationToken ct)
    {
        row = await RefreshAsync(row, ct);
        if (row.Status is not ("completed" or "SIGNED"))
        {
            throw new InvalidOperationException("The completed document is available after every recipient has signed.");
        }
        return await Provider(row.Provider).DownloadAsync(row.ExternalId!, audit, ct);
    }
}
