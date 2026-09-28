using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class ImageDescriptionUsageEndpoints
{
    public static void MapImageDescriptionUsage(this WebApplication app)
    {
        app.MapGet("/api/usage/image-descriptions", ReadAsync);
    }

    public static async Task<IResult> ReadAsync(SharePointIndexDbContext db, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string? model = null, Guid? userId = null,
        Guid? attachmentId = null, int skip = 0, int top = 25)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start || end == DateOnly.MaxValue || end.DayNumber - start.DayNumber > 365 || skip < 0 || top is < 1 or > 100)
        {
            return Results.BadRequest(new { error = "Choose a valid UTC date range of up to 366 days and page size from 1 to 100." });
        }
        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var query = db.ImageDescriptionTokenUsage.AsNoTracking().Where(x => x.CreatedAtUtc >= startUtc && x.CreatedAtUtc < endUtc);
        if (!string.IsNullOrWhiteSpace(model))
        {
            query = query.Where(x => x.ModelId == model.Trim());
        }
        if (userId is { } user)
        {
            query = query.Where(x => x.UserId == user);
        }
        if (attachmentId is { } attachment)
        {
            query = query.Where(x => x.AttachmentId == attachment);
        }
        var summary = await query.GroupBy(x => 1).Select(g => new
        {
            Calls = g.Count(), UnknownUsage = g.Count(x => x.TotalTokens == null),
            InputTokens = g.Sum(x => x.InputTokens ?? 0), OutputTokens = g.Sum(x => x.OutputTokens ?? 0),
            TotalTokens = g.Sum(x => x.TotalTokens ?? 0)
        }).SingleOrDefaultAsync(ct);
        var daily = await query.GroupBy(x => x.CreatedAtUtc.Date).Select(g => new
        {
            Day = g.Key, Calls = g.Count(), UnknownUsage = g.Count(x => x.TotalTokens == null),
            InputTokens = g.Sum(x => x.InputTokens ?? 0), OutputTokens = g.Sum(x => x.OutputTokens ?? 0),
            TotalTokens = g.Sum(x => x.TotalTokens ?? 0)
        }).OrderBy(x => x.Day).ToListAsync(ct);
        var models = await query.GroupBy(x => x.ModelId).Select(g => new
        {
            ModelId = g.Key, Calls = g.Count(), UnknownUsage = g.Count(x => x.TotalTokens == null),
            InputTokens = g.Sum(x => x.InputTokens ?? 0), OutputTokens = g.Sum(x => x.OutputTokens ?? 0),
            TotalTokens = g.Sum(x => x.TotalTokens ?? 0)
        }).OrderByDescending(x => x.TotalTokens).ThenBy(x => x.ModelId).ToListAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(top)
            .Select(x => new
            {
                x.Id, x.CreatedAtUtc, x.UserId, x.ConversationId, x.QuestionId, x.AttachmentId, x.ModelId,
                x.InputTokens, x.OutputTokens, x.TotalTokens,
                x.SystemPrompt, x.Prompt, x.Description,
                UserName = db.Users.Where(u => u.Id == x.UserId).Select(u => u.UserName).FirstOrDefault(),
                FileName = db.ChatMessageAttachmentFiles.Where(f => f.Id == x.AttachmentId).Select(f => f.FileName).FirstOrDefault()
            }).ToListAsync(ct);
        return Results.Ok(new
        {
            Summary = summary ?? new { Calls = 0, UnknownUsage = 0, InputTokens = 0L, OutputTokens = 0L, TotalTokens = 0L },
            Daily = daily, Models = models, Items = items
        });
    }
}
