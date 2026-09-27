using Microsoft.EntityFrameworkCore;

namespace SharePointAgent.Persistence;

public static class AttachmentStorageQuota
{
    // Lock the creator row until the upload commits. Concurrent uploads and limit changes for the
    // same user cannot race the usage check. Other users can upload independently.
    public static async Task StoreAsync(SharePointIndexDbContext db, ChatMessageAttachmentFileEntity file,
        Func<CancellationToken, Task> writeContent, CancellationToken ct)
    {
        if (file.CreatedById is not { } userId)
        {
            throw new UserManagementException("An attachment creator is required.");
        }

        if (file.SizeBytes <= 0)
        {
            throw new UserManagementException("The uploaded file is empty.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var query = db.Database.IsSqlServer()
            ? db.Users.FromSqlInterpolated($"SELECT * FROM [AspNetUsers] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {userId}")
            : db.Users.Where(x => x.Id == userId);
        var user = await query.AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw new UserManagementException("User not found.", 404);
        if (!user.IsActive)
        {
            throw new UserManagementException("Your application account is disabled.", 403);
        }

        var used = await db.ChatMessageAttachmentFiles.Where(x => x.CreatedById == userId)
            .SumAsync(x => (long?)x.SizeBytes, ct) ?? 0;
        if (user.AttachmentStorageLimitBytes is { } limit && (used >= limit || file.SizeBytes > limit - used))
        {
            throw new UserManagementException($"Attachment storage limit exceeded. Used {used:N0} of {limit:N0} bytes; this file requires {file.SizeBytes:N0} bytes. Delete unused attachments or ask an administrator to increase your limit.", 409);
        }

        db.ChatMessageAttachmentFiles.Add(file);
        await db.SaveChangesAsync(ct);
        await writeContent(ct);
        await transaction.CommitAsync(ct);
    }
}
