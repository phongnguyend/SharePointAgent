using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class JevUsageEndpoints
{
    public static void MapJevUsage(this WebApplication app)
    {
        app.MapGet("/api/usage/jev", ReadAsync);
    }

    public static async Task<IResult> ReadAsync(SharePointIndexDbContext db,
        DateOnly? from = null, DateOnly? to = null, string? model = null, string? user = null,
        string? traceId = null, int skip = 0, int top = 25, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start || end == DateOnly.MaxValue || end.DayNumber - start.DayNumber > 365 || skip < 0 || top is < 1 or > 100)
        {
            return Results.BadRequest(new { error = "Choose a valid UTC date range of up to 366 days and page size from 1 to 100." });
        }
        var startDay = start.Year * 10000 + start.Month * 100 + start.Day;
        var endDay = end.Year * 10000 + end.Month * 100 + end.Day;
        var query = db.JevTokenUsage.AsNoTracking().Where(x => x.Day >= startDay && x.Day <= endDay);
        if (!string.IsNullOrWhiteSpace(model))
        {
            query = query.Where(x => x.ModelId == model.Trim());
        }
        if (!string.IsNullOrWhiteSpace(user))
        {
            // Matches the Chat and Embedding reports: a user ID, or part of a name or email.
            var term = user.Trim();
            var id = Guid.TryParse(term, out var parsed) ? parsed : (Guid?)null;
            query = query.Where(x => (id != null && x.UserId == id) || db.Users.Any(u => u.Id == x.UserId
                && (u.DisplayName.Contains(term) || (u.Email != null && u.Email.Contains(term)))));
        }
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            query = query.Where(x => x.TraceId == traceId.Trim());
        }
        var summary = await query.GroupBy(x => 1).Select(g => new
        {
            Calls = g.Count(), UnknownUsage = 0,
            InputTokens = g.Sum(x => x.InputTokens), OutputTokens = g.Sum(x => x.OutputTokens),
            TotalTokens = g.Sum(x => x.TotalTokens)
        }).SingleOrDefaultAsync(ct);
        var daily = await query.GroupBy(x => x.Day).Select(g => new
        {
            Day = g.Key, Calls = g.Count(), UnknownUsage = 0,
            InputTokens = g.Sum(x => x.InputTokens), OutputTokens = g.Sum(x => x.OutputTokens),
            TotalTokens = g.Sum(x => x.TotalTokens)
        }).OrderBy(x => x.Day).ToListAsync(ct);
        var models = await query.GroupBy(x => x.ModelId).Select(g => new
        {
            ModelId = g.Key, Calls = g.Count(), UnknownUsage = 0,
            InputTokens = g.Sum(x => x.InputTokens), OutputTokens = g.Sum(x => x.OutputTokens),
            TotalTokens = g.Sum(x => x.TotalTokens)
        }).OrderByDescending(x => x.TotalTokens).ThenBy(x => x.ModelId).ToListAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(top)
            .Select(x => new
            {
                x.Id, x.CreatedAtUtc, x.UserId, x.ConversationId, x.QuestionId, x.QuestionCount, x.TraceId, x.ModelId,
                x.InputTokens, x.OutputTokens, x.TotalTokens,
                UserName = db.Users.Where(u => u.Id == x.UserId).Select(u => u.DisplayName != "" ? u.DisplayName : u.Email).FirstOrDefault(),
                UserEmail = db.Users.Where(u => u.Id == x.UserId).Select(u => u.Email).FirstOrDefault()
            }).ToListAsync(ct);
        return Results.Ok(new
        {
            Summary = summary ?? new { Calls = 0, UnknownUsage = 0, InputTokens = 0L, OutputTokens = 0L, TotalTokens = 0L },
            Daily = daily.Select(x => new
            {
                Day = $"{x.Day / 10000:D4}-{x.Day / 100 % 100:D2}-{x.Day % 100:D2}",
                x.Calls, x.UnknownUsage, x.InputTokens, x.OutputTokens, x.TotalTokens
            }), Models = models, Items = items
        });
    }
}
