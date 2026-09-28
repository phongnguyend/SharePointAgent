using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class TokenUsageEndpoints
{
    public static void MapTokenUsage(this WebApplication app)
    {
        app.MapGet("/api/usage/tokens", ReadAsync);
    }

    public static async Task<IResult> ReadAsync(SharePointIndexDbContext db, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string? model = null, string? user = null,
        Guid? questionId = null, bool unknownModel = false, int skip = 0, int top = 25)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start || end.DayNumber - start.DayNumber > 365 || skip < 0 || top is < 1 or > 100
            || model?.Length > 200 || user?.Length > 200)
        {
            return Results.BadRequest(new { error = "Choose a valid UTC date range of up to 366 days and a page size from 1 to 100." });
        }
        var startDay = start.Year * 10000 + start.Month * 100 + start.Day;
        var endDay = end.Year * 10000 + end.Month * 100 + end.Day;
        // Quotas assign a turn to its start day; CreatedAtUtc records when usage was saved.
        var period = db.UserTokenUsage.AsNoTracking().Where(x => x.Day >= startDay && x.Day <= endDay);
        var query = period;
        if (unknownModel)
        {
            query = query.Where(x => x.ModelId == null);
        }
        else if (!string.IsNullOrWhiteSpace(model))
        {
            query = query.Where(x => x.ModelId == model);
        }
        if (!string.IsNullOrWhiteSpace(user))
        {
            var term = user.Trim();
            var id = Guid.TryParse(term, out var parsed) ? parsed : (Guid?)null;
            query = query.Where(x => (id != null && x.UserId == id) || db.Users.Any(u => u.Id == x.UserId
                && (u.DisplayName.Contains(term) || (u.Email != null && u.Email.Contains(term)))));
        }
        if (questionId is { } question)
        {
            query = query.Where(x => x.QuestionId == question);
        }
        var summary = await query.GroupBy(x => 1).Select(g => new
        {
            Turns = g.Count(),
            InputTokens = g.Sum(x => x.InputTokens),
            OutputTokens = g.Sum(x => x.OutputTokens),
            TotalTokens = g.Sum(x => x.TotalTokens),
            Users = g.Select(x => x.UserId).Distinct().Count(),
            UnknownModelTurns = g.Count(x => x.ModelId == null)
        }).SingleOrDefaultAsync(ct);
        var daily = await query.GroupBy(x => x.Day).Select(g => new
        {
            Day = g.Key,
            Turns = g.Count(),
            InputTokens = g.Sum(x => x.InputTokens),
            OutputTokens = g.Sum(x => x.OutputTokens),
            TotalTokens = g.Sum(x => x.TotalTokens)
        }).OrderBy(x => x.Day).ToListAsync(ct);
        var models = await query.GroupBy(x => x.ModelId).Select(g => new
        {
            ModelId = g.Key,
            Turns = g.Count(),
            InputTokens = g.Sum(x => x.InputTokens),
            OutputTokens = g.Sum(x => x.OutputTokens),
            TotalTokens = g.Sum(x => x.TotalTokens)
        }).OrderByDescending(x => x.TotalTokens).ThenBy(x => x.ModelId).ToListAsync(ct);
        var users = await query.GroupBy(x => x.UserId).Select(g => new
        {
            UserId = g.Key,
            Turns = g.Count(),
            InputTokens = g.Sum(x => x.InputTokens),
            OutputTokens = g.Sum(x => x.OutputTokens),
            TotalTokens = g.Sum(x => x.TotalTokens)
        }).OrderByDescending(x => x.TotalTokens).ThenBy(x => x.UserId).Take(100).ToListAsync(ct);
        var rows = await query.OrderByDescending(x => x.Day).ThenByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.QuestionId).Skip(skip).Take(top).ToListAsync(ct);
        var ids = rows.Select(x => x.UserId).Concat(users.Select(x => x.UserId)).Distinct().ToArray();
        var names = await db.Users.Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.DisplayName })
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        return Results.Ok(new
        {
            From = start,
            To = end,
            Summary = summary ?? new { Turns = 0, InputTokens = 0L, OutputTokens = 0L, TotalTokens = 0L, Users = 0, UnknownModelTurns = 0 },
            Daily = daily,
            Models = models,
            Users = users.Select(x => new { x.UserId, Name = names.GetValueOrDefault(x.UserId, x.UserId.ToString()), x.Turns, x.InputTokens, x.OutputTokens, x.TotalTokens }),
            Items = rows.Select(x => new { Usage = x, UserName = names.GetValueOrDefault(x.UserId, x.UserId.ToString()) }),
            ModelOptions = await period.Select(x => x.ModelId).Distinct().OrderBy(x => x).ToListAsync(ct)
        });
    }
}
