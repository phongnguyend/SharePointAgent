using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class ContentSafetyUsageEndpoints
{
    public static void MapContentSafetyUsage(this WebApplication app)
    {
        app.MapGet("/api/usage/content-safety", ReadAsync);
    }

    public static async Task<IResult> ReadAsync(SharePointIndexDbContext db, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string? status = null, string? operation = null,
        Guid? userId = null, int skip = 0, int top = 25)
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
        var query = db.ContentSafetyUsage.AsNoTracking().Where(x => x.CreatedAtUtc >= startUtc && x.CreatedAtUtc < endUtc);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(operation))
        {
            query = query.Where(x => x.Operation == operation);
        }
        if (userId is { } id)
        {
            query = query.Where(x => x.UserId == id);
        }
        var summary = await query.GroupBy(x => 1).Select(g => new
        {
            Requests = g.Count(),
            Allowed = g.Count(x => x.Status == "Allowed"),
            Blocked = g.Count(x => x.Status == "Blocked"),
            Failed = g.Count(x => x.Status == "Failed" || x.Status == "Cancelled"),
            Characters = g.Sum(x => (long)x.CharacterCount),
            EstimatedTextRecords = g.Sum(x => x.Status == "Allowed" || x.Status == "Blocked" ? (long)x.EstimatedTextRecords : 0)
        }).SingleOrDefaultAsync(ct);
        var daily = await query.GroupBy(x => x.CreatedAtUtc.Date).Select(g => new
        {
            Day = g.Key,
            Requests = g.Count(),
            Blocked = g.Count(x => x.Status == "Blocked"),
            Characters = g.Sum(x => (long)x.CharacterCount)
        }).OrderBy(x => x.Day).ToListAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(top).ToListAsync(ct);
        return Results.Ok(new
        {
            Summary = summary ?? new { Requests = 0, Allowed = 0, Blocked = 0, Failed = 0, Characters = 0L, EstimatedTextRecords = 0L },
            Daily = daily,
            Items = items
        });
    }
}
