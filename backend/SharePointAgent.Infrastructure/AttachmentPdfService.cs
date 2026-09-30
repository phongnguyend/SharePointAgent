using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

public sealed class AttachmentPdfService(
    AttachmentContentCache contentCache,
    DocumentIntelligenceClient documentIntelligence,
    IDbContextFactory<SharePointIndexDbContext> contextFactory)
{
    public async Task<string?> ExtractTextAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var file = await db.ChatMessageAttachmentFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (file is null)
        {
            return null;
        }

        if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("PDF text extraction requires a PDF attachment.");
        }

        var cached = await contentCache.DownloadAsync(file, ct);
        return await documentIntelligence.ExtractAsync(await File.ReadAllBytesAsync(cached.LocalPath, ct), ct);
    }
}
