using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class TranscriptionUsageEndpoints
{
    public static void MapTranscriptionUsage(this WebApplication app)
    {
        app.MapGet("/api/usage/transcriptions", ReadAsync);
    }

    public static async Task<IResult> ReadAsync(SharePointIndexDbContext db, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string? model = null, string? user = null, int skip = 0, int top = 25)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start || end == DateOnly.MaxValue || end.DayNumber - start.DayNumber > 365 || skip < 0 || top is < 1 or > 100)
        {
            return Results.BadRequest(new { error = "Choose a valid UTC date range of up to 366 days and page size from 1 to 100." });
        }
        // Day is the stored UTC yyyyMMdd key, which also keeps grouping provider-independent.
        static int Key(DateOnly date) => date.Year * 10000 + date.Month * 100 + date.Day;
        var startDay = Key(start);
        var endDay = Key(end);
        var query = db.TranscriptionTokenUsage.AsNoTracking().Where(x => x.Day >= startDay && x.Day <= endDay);
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

        var summary = await query.GroupBy(x => 1).Select(g => new TranscriptionUsageTotals(
            g.Count(), g.Count(x => x.TotalTokens == null),
            g.Sum(x => x.InputTokens ?? 0), g.Sum(x => x.OutputTokens ?? 0), g.Sum(x => x.TotalTokens ?? 0),
            g.Sum(x => x.AudioBytes), g.Sum(x => x.DurationSeconds ?? 0))).SingleOrDefaultAsync(ct);
        var daily = await query.GroupBy(x => x.Day).Select(g => new
        {
            Day = g.Key,
            Totals = new TranscriptionUsageTotals(
                g.Count(), g.Count(x => x.TotalTokens == null),
                g.Sum(x => x.InputTokens ?? 0), g.Sum(x => x.OutputTokens ?? 0), g.Sum(x => x.TotalTokens ?? 0),
                g.Sum(x => x.AudioBytes), g.Sum(x => x.DurationSeconds ?? 0))
        }).OrderBy(x => x.Day).ToListAsync(ct);
        var models = await query.GroupBy(x => x.ModelId).Select(g => new
        {
            ModelId = g.Key,
            Totals = new TranscriptionUsageTotals(
                g.Count(), g.Count(x => x.TotalTokens == null),
                g.Sum(x => x.InputTokens ?? 0), g.Sum(x => x.OutputTokens ?? 0), g.Sum(x => x.TotalTokens ?? 0),
                g.Sum(x => x.AudioBytes), g.Sum(x => x.DurationSeconds ?? 0))
        }).ToListAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(top)
            .Select(x => new
            {
                x.Id, x.CreatedAtUtc, x.UserId, x.ModelId, x.AudioBytes, x.DurationSeconds,
                x.InputTokens, x.OutputTokens, x.TotalTokens,
                UserName = db.Users.Where(u => u.Id == x.UserId).Select(u => u.DisplayName != "" ? u.DisplayName : u.Email).FirstOrDefault(),
                UserEmail = db.Users.Where(u => u.Id == x.UserId).Select(u => u.Email).FirstOrDefault()
            }).ToListAsync(ct);

        static string Date(int day) => $"{day / 10000:D4}-{day / 100 % 100:D2}-{day % 100:D2}";
        return Results.Ok(new
        {
            Summary = summary ?? new TranscriptionUsageTotals(0, 0, 0, 0, 0, 0, 0),
            Daily = daily.Select(x => Flatten(x.Totals, day: Date(x.Day))),
            Models = models.OrderByDescending(x => x.Totals.TotalTokens).ThenBy(x => x.ModelId).Select(x => Flatten(x.Totals, modelId: x.ModelId)),
            Items = items
        });
    }

    private static object Flatten(TranscriptionUsageTotals totals, string? day = null, string? modelId = null) => new
    {
        Day = day, ModelId = modelId, totals.Calls, totals.UnknownUsage, totals.InputTokens, totals.OutputTokens,
        totals.TotalTokens, totals.AudioBytes, totals.DurationSeconds
    };

    public sealed record TranscriptionUsageTotals(
        int Calls, int UnknownUsage, long InputTokens, long OutputTokens, long TotalTokens, long AudioBytes, double DurationSeconds);
}
