using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure.DocumentSigning;

public sealed class SignatureRequestService(
    IEnumerable<ISignatureProvider> providers,
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    AttachmentContentCache contentCache,
    IOptions<DocumentSigningOptions> options)
{
    private bool InAppEnabled => options.Value.InApp.Enabled;

    public string[] EnabledProviders => providers.Where(x => x.Enabled).Select(x => x.Name)
        .Concat(InAppEnabled ? [InAppSigning.Provider] : []).ToArray();

    private ISignatureProvider Provider(string name) => providers.FirstOrDefault(x => x.Name == name && x.Enabled)
        ?? throw new InvalidOperationException("This signing provider is not enabled. Ask an administrator to configure the shared organization connection.");

    public static void Validate(SignatureInput input)
    {
        if (input.ClientRequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.Subject) || input.Subject.Length > 100 || input.Message?.Length > 2000)
        {
            throw new ArgumentException("Provide a request ID, a subject of 1–100 characters, and a message of at most 2,000 characters.");
        }
        // In-app signers are recorded for tracking only; the requesting user signs, so they are optional.
        var inApp = input.Provider == InAppSigning.Provider;
        var minimum = inApp ? 0 : 1;
        if (input.Recipients is null || input.Recipients.Length < minimum || input.Recipients.Length > 20 || input.Recipients.Any(x => x is null ||
            string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 100 || string.IsNullOrWhiteSpace(x.Email) || x.Email.Length > 254 || !new EmailAddressAttribute().IsValid(x.Email)))
        {
            throw new ArgumentException($"Provide {minimum}–20 recipients, each with a name and valid email address.");
        }
        if (input.Recipients.Select(x => x.Email.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Recipients.Length)
        {
            throw new ArgumentException("Recipient email addresses must be unique.");
        }
    }

    public async Task<SignatureRequestEntity> CreateAsync(Guid attachmentId, Guid userId, SignatureInput input, CancellationToken ct)
    {
        Validate(input);
        var inApp = input.Provider == InAppSigning.Provider;
        if (inApp && !InAppEnabled)
        {
            throw new InvalidOperationException("In-app signing is not enabled. Ask an administrator to enable it.");
        }
        var provider = inApp ? null : Provider(input.Provider);
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
            Provider = input.Provider, Subject = input.Subject.Trim(),
            Message = string.IsNullOrWhiteSpace(input.Message) ? null : input.Message.Trim(),
            RecipientsJson = JsonSerializer.Serialize(input.Recipients.Select(x => new SignatureRecipient(x.Name.Trim(), x.Email.Trim()))),
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        if (provider is null)
        {
            row.Status = InAppSigning.Draft;
            row.FieldsJson = "[]";
            row.OriginalSha256 = Convert.ToHexStringLower(SHA256.HashData(pdf));
            db.SignatureRequests.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }
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

    public async Task DeleteNeedsReviewAsync(Guid attachmentId, Guid requestId, Guid userId, bool admin, CancellationToken ct)
    {
        await FindAsync(attachmentId, requestId, userId, admin, ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var deleted = await db.SignatureRequests
            .Where(x => x.Id == requestId && x.AttachmentFileId == attachmentId &&
                (admin || x.CreatedById == userId) &&
                (x.Status == "NeedsReview" || (x.Provider == InAppSigning.Provider && x.Status == InAppSigning.Draft)))
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            throw new InvalidOperationException("Only NeedsReview records and in-app drafts can be deleted. Refresh the list and try again.");
        }
    }

    public async Task<SignatureRequestEntity> RefreshAsync(SignatureRequestEntity row, CancellationToken ct)
    {
        if (row.Provider == InAppSigning.Provider)
        {
            await using var local = await contextFactory.CreateDbContextAsync(ct);
            return await local.SignatureRequests.AsNoTracking().SingleAsync(x => x.Id == row.Id, ct);
        }
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
        if (row.Provider == InAppSigning.Provider)
        {
            throw new InvalidOperationException("Open in-app signing requests in the signing editor.");
        }
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
        if (row.Provider == InAppSigning.Provider)
        {
            return audit ? await AuditAsync(row, ct) : await contentCache.DownloadSignedDocumentAsync(row.SignedDocumentBlobName!, ct);
        }
        return await Provider(row.Provider).DownloadAsync(row.ExternalId!, audit, ct);
    }

    public async Task<SigningFieldsView> GetFieldsAsync(SignatureRequestEntity row, CancellationToken ct)
    {
        row = await RefreshAsync(RequireInApp(row), ct);
        return new(row.Status, Fields(row));
    }

    public async Task<SigningFieldsView> SaveFieldsAsync(SignatureRequestEntity row, Guid userId, SigningFieldsInput input, CancellationToken ct)
    {
        RequireSigner(row, userId);
        InAppSigning.Validate(input?.Fields, requireValues: false);
        var json = JsonSerializer.Serialize(input!.Fields);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var updated = await db.SignatureRequests
            .Where(x => x.Id == row.Id && x.Status == InAppSigning.Draft)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.FieldsJson, json).SetProperty(r => r.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
        if (updated == 0)
        {
            throw new InvalidOperationException("This signing request has already been finished.");
        }
        return new(InAppSigning.Draft, input.Fields);
    }

    public async Task<SignatureRequestEntity> CompleteAsync(SignatureRequestEntity row, Guid userId, byte[] pdf, CancellationToken ct)
    {
        RequireSigner(row, userId);
        row = await RefreshAsync(row, ct);
        if (row.Status != InAppSigning.Draft)
        {
            throw new InvalidOperationException("This signing request has already been finished.");
        }
        // The browser flattens the saved fields into the PDF; the server requires the saved layout to be complete.
        InAppSigning.Validate(Fields(row), requireValues: true);
        if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new ArgumentException("The signed document is not a valid PDF.");
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
        var blobName = await contentCache.StoreSignedDocumentAsync(row.Id, sha, pdf, ct);
        var now = DateTimeOffset.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var updated = await db.SignatureRequests
            .Where(x => x.Id == row.Id && x.Status == InAppSigning.Draft)
            .ExecuteUpdateAsync(x => x
                .SetProperty(r => r.Status, InAppSigning.Completed)
                .SetProperty(r => r.SignedDocumentBlobName, blobName)
                .SetProperty(r => r.SignedSha256, sha)
                .SetProperty(r => r.CompletedAtUtc, now)
                .SetProperty(r => r.UpdatedAtUtc, now), ct);
        if (updated == 0)
        {
            throw new InvalidOperationException("This signing request has already been finished.");
        }
        return await db.SignatureRequests.AsNoTracking().SingleAsync(x => x.Id == row.Id, ct);
    }

    private static SignatureRequestEntity RequireInApp(SignatureRequestEntity row) => row.Provider == InAppSigning.Provider
        ? row
        : throw new InvalidOperationException("Only in-app signing requests have editable fields.");

    private static void RequireSigner(SignatureRequestEntity row, Guid userId)
    {
        RequireInApp(row);
        if (row.CreatedById != userId)
        {
            throw new InvalidOperationException("Only the user who created this in-app request can sign it.");
        }
    }

    private static SigningField[] Fields(SignatureRequestEntity row) =>
        JsonSerializer.Deserialize<SigningField[]>(row.FieldsJson ?? "[]") ?? [];

    private async Task<byte[]> AuditAsync(SignatureRequestEntity row, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fileName = await db.ChatMessageAttachmentFiles.AsNoTracking()
            .Where(x => x.Id == row.AttachmentFileId).Select(x => x.FileName).SingleOrDefaultAsync(ct);
        var signer = await db.Users.AsNoTracking().Where(x => x.Id == row.CreatedById)
            .Select(x => new { x.DisplayName, x.UserName, x.Email }).SingleOrDefaultAsync(ct);
        var recipients = JsonSerializer.Deserialize<SignatureRecipient[]>(row.RecipientsJson) ?? [];
        var fields = Fields(row);
        static string Time(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "";
        return SigningAuditPdf.Create("In-app signing audit record",
        [
            ("Request", row.Id.ToString()),
            ("Subject", row.Subject),
            ("Document", fileName ?? ""),
            ("Signed by", signer is null ? "" : $"{(string.IsNullOrWhiteSpace(signer.DisplayName) ? signer.UserName : signer.DisplayName)} <{signer.Email}>"),
            ("Signer account ID", row.CreatedById.ToString()),
            ("Message", row.Message ?? "(none)"),
            ("Tracked signers (not notified)", recipients.Length == 0 ? "(none)"
                : string.Join("; ", recipients.Select((x, i) => $"{i + 1}. {x.Name} <{x.Email}>"))),
            ("Created", Time(row.CreatedAtUtc)),
            ("Completed", Time(row.CompletedAtUtc)),
            ("Original document SHA-256", row.OriginalSha256 ?? ""),
            ("Signed document SHA-256", row.SignedSha256 ?? ""),
            ("Fields", string.Join(", ", fields.GroupBy(x => x.Type).Select(x => $"{x.Count()} {x.Key}"))),
            .. fields.Select((x, i) => ($"Field {i + 1}",
                $"{x.Type} on page {x.Page}" + (InAppSigning.IsImageField(x.Type) ? " (drawn)" : $": {x.Value}")))
        ]);
    }
}
