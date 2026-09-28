using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class EmbeddingUsageEndpoints
{
    public static void MapEmbeddingUsage(this WebApplication app)
    {
        app.MapGet("/api/usage/embeddings", ReadAsync);
    }

    public static async Task<IResult> ReadAsync(SharePointIndexDbContext db, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string? model = null, string? operation = null,
        string? user = null, string? reference = null, bool unattributed = false, int skip = 0, int top = 25)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start || end.DayNumber - start.DayNumber > 365 || end == DateOnly.MaxValue
            || skip < 0 || top is < 1 or > 100
            || new[] { model, operation, user, reference }.Any(x => x?.Length > 200))
        {
            return Results.BadRequest(new { error = "Choose a valid UTC date range of up to 366 days and a page size from 1 to 100." });
        }

        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var period = db.EmbeddingTokenUsage.AsNoTracking().Where(x => x.CreatedAtUtc >= startUtc && x.CreatedAtUtc < endUtc);
        var query = period;
        if (!string.IsNullOrWhiteSpace(model))
        {
            query = query.Where(x => x.EmbeddingModelId == model);
        }
        if (!string.IsNullOrWhiteSpace(operation))
        {
            query = query.Where(x => x.Operation == operation);
        }
        if (unattributed)
        {
            query = query.Where(x => x.UserId == null);
        }
        if (!string.IsNullOrWhiteSpace(user))
        {
            var term = user.Trim();
            var userId = Guid.TryParse(term, out var parsed) ? parsed : (Guid?)null;
            query = query.Where(x => (userId != null && x.UserId == userId) || db.Users.Any(u =>
                u.Id == x.UserId && (u.DisplayName.Contains(term) || (u.Email != null && u.Email.Contains(term)))));
        }
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var term = reference.Trim();
            var id = Guid.TryParse(term, out var parsed) ? parsed : (Guid?)null;
            query = query.Where(x => x.FileId == term || x.DriveId == term || x.TraceId == term ||
                (id != null && (x.Id == id || x.QuestionId == id || x.ConversationId == id || x.AttachmentId == id || x.ScanId == id)));
        }

        var summary = await query.GroupBy(x => 1).Select(g => new
        {
            Calls = g.Count(),
            Tokens = g.Sum(x => x.TotalTokens ?? 0),
            InputTokens = g.Sum(x => x.InputTokens ?? 0),
            UnknownCalls = g.Count(x => x.TotalTokens == null),
            Models = g.Select(x => x.EmbeddingModelId).Distinct().Count(),
            Users = g.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count()
        }).SingleOrDefaultAsync(ct);
        var daily = await query.GroupBy(x => x.CreatedAtUtc.Date).Select(g => new
        {
            Day = g.Key,
            Calls = g.Count(),
            Tokens = g.Sum(x => x.TotalTokens ?? 0),
            UnknownCalls = g.Count(x => x.TotalTokens == null)
        }).OrderBy(x => x.Day).ToListAsync(ct);
        var models = await query.GroupBy(x => x.EmbeddingModelId).Select(g => new
        {
            Name = g.Key,
            Calls = g.Count(),
            Tokens = g.Sum(x => x.TotalTokens ?? 0),
            UnknownCalls = g.Count(x => x.TotalTokens == null)
        }).OrderByDescending(x => x.Tokens).ThenBy(x => x.Name).ToListAsync(ct);
        var operations = await query.GroupBy(x => x.Operation).Select(g => new
        {
            Name = g.Key,
            Calls = g.Count(),
            Tokens = g.Sum(x => x.TotalTokens ?? 0),
            UnknownCalls = g.Count(x => x.TotalTokens == null)
        }).OrderByDescending(x => x.Tokens).ThenBy(x => x.Name).ToListAsync(ct);
        var users = await query.GroupBy(x => x.UserId).Select(g => new
        {
            UserId = g.Key,
            Calls = g.Count(),
            Tokens = g.Sum(x => x.TotalTokens ?? 0),
            UnknownCalls = g.Count(x => x.TotalTokens == null)
        }).OrderByDescending(x => x.Tokens).ThenBy(x => x.UserId).Take(100).ToListAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip(skip).Take(top).ToListAsync(ct);
        var userIds = rows.Select(x => x.UserId).Concat(users.Select(x => x.UserId)).Where(x => x != null).Select(x => x!.Value).Distinct().ToArray();
        var names = await db.Users.Where(x => userIds.Contains(x.Id))
            .Select(x => new { x.Id, x.DisplayName, x.Email }).ToDictionaryAsync(x => x.Id, ct);
        return Results.Ok(new
        {
            From = start,
            To = end,
            Summary = summary ?? new { Calls = 0, Tokens = 0L, InputTokens = 0L, UnknownCalls = 0, Models = 0, Users = 0 },
            Daily = daily,
            Models = models,
            Operations = operations,
            Users = users.Select(x => new { x.UserId, Name = x.UserId is { } id && names.TryGetValue(id, out var u) ? u.DisplayName : x.UserId?.ToString() ?? "Unattributed / background", x.Calls, x.Tokens, x.UnknownCalls }),
            Items = rows.Select(x => new { Usage = x, UserName = x.UserId is { } id && names.TryGetValue(id, out var u) ? u.DisplayName : null }),
            ModelOptions = await period.Select(x => x.EmbeddingModelId).Distinct().OrderBy(x => x).ToListAsync(ct),
            OperationOptions = await period.Select(x => x.Operation).Distinct().OrderBy(x => x).ToListAsync(ct)
        });
    }
}
