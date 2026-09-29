using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Domain;

namespace SharePointAgent.Persistence;

public sealed class MonthlyTokenQuota(IDbContextFactory<SharePointIndexDbContext> factory, TimeProvider clock)
{
    public static int MonthKey(DateTimeOffset time) => time.UtcDateTime.Year * 100 + time.UtcDateTime.Month;
    public static int DayKey(DateTimeOffset time) => MonthKey(time) * 100 + time.UtcDateTime.Day;
    public static DateTimeOffset NextMonth(DateTimeOffset time) => new DateTimeOffset(time.UtcDateTime.Year, time.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

    public async Task<TurnLease> BeginAsync(Guid userId, CancellationToken ct)
    {
        var db = await factory.CreateDbContextAsync(ct);
        var resource = $"chat-token-quota:{userId:D}";
        TurnLease? lease = null;
        try
        {
            // A session application lock serializes turns for this user across API instances,
            // without holding a database transaction or blocking usage/profile reads during generation.
            await db.Database.OpenConnectionAsync(ct);
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @r;";
            AddResource(command, resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            if (result < 0)
            {
                throw new UserManagementException("A chat response is already running for your account. Wait for it to finish before sending another message.", 429);
            }

            lease = new TurnLease(db, resource, userId, clock.GetUtcNow());
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, ct);
            var used = await UsedAsync(db, userId, lease.Month, ct);
            EnsureAvailable(user.MonthlyTokenLimit, used);
            return lease;
        }
        catch
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
            else
            {
                await db.DisposeAsync();
            }

            throw;
        }
    }

    public static void EnsureAvailable(long? limit, long used)
    {
        if (limit.HasValue && used >= limit.Value)
        {
            throw new UserManagementException("Your monthly token limit has been reached. Contact an administrator or wait until the next month (UTC).", 429);
        }
    }

    public static async Task<long> UsedAsync(SharePointIndexDbContext db, Guid userId, int month, CancellationToken ct = default)
    {
        // Several rows per turn, one per model request. They carry the turn's start day and month, not
        // each request's, so a turn that runs across midnight UTC stays in the month its quota was
        // checked against.
        var chat = await ChatUsage(db, userId, month).SumAsync(x => x.TotalTokens, ct) ?? 0;
        var images = await ImageUsage(db, userId, month).SumAsync(x => x.TotalTokens, ct) ?? 0;
        return chat + images;
    }

    private static IQueryable<ChatTokenUsageEntity> ChatUsage(SharePointIndexDbContext db, Guid userId, int month) =>
        db.ChatTokenUsage.Where(x => x.UserId == userId && x.Month == month);

    private static IQueryable<ImageDescriptionTokenUsageEntity> ImageUsage(SharePointIndexDbContext db, Guid userId, int month) =>
        db.ImageDescriptionTokenUsage.Where(x => x.UserId == userId && x.Month == month);

    public static async Task<IReadOnlyList<DailyModelTokenUsage>> DailyUsageAsync(
        SharePointIndexDbContext db, Guid userId, int month, CancellationToken ct = default)
    {
        var chat = await ChatUsage(db, userId, month)
            .GroupBy(x => new { x.Day, x.ModelId })
            .Select(x => new DailyModelTokenUsage(x.Key.Day, x.Key.ModelId, x.Sum(t => t.InputTokens ?? 0), x.Sum(t => t.OutputTokens ?? 0), x.Sum(t => t.TotalTokens ?? 0)))
            .ToListAsync(ct);
        var images = await ImageUsage(db, userId, month).GroupBy(x => new { x.Day, x.ModelId })
            .Select(x => new DailyModelTokenUsage(x.Key.Day, x.Key.ModelId, x.Sum(t => t.InputTokens ?? 0), x.Sum(t => t.OutputTokens ?? 0), x.Sum(t => t.TotalTokens ?? 0)))
            .ToListAsync(ct);
        return chat.Concat(images).GroupBy(x => new { x.Day, x.ModelId })
            .OrderBy(x => x.Key.Day).ThenBy(x => x.Key.ModelId)
            .Select(x => new DailyModelTokenUsage(x.Key.Day, x.Key.ModelId, x.Sum(t => t.InputTokens), x.Sum(t => t.OutputTokens), x.Sum(t => t.TotalTokens))).ToArray();
    }

    private static void AddResource(DbCommand command, string resource)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = resource;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Records the turn's reported total as a single fallback row, but only when the turn left no
    /// per-request row of its own. The agent writes one row per model request as that response
    /// completes; this covers the case where none of those writes reached the database — a Foundry
    /// sandbox that could not reach SQL, or a failed write — so a turn is never billed as free.
    /// <para>
    /// Doing nothing when rows already exist is also what keeps repeat accounting for the same question
    /// from double-counting. A turn where only <em>some</em> per-request writes failed keeps its partial
    /// rows and is under-billed by the difference; that loss is logged where it happens.
    /// </para>
    /// </summary>
    public static async Task RecordUsageAsync(SharePointIndexDbContext db, Guid userId, Guid conversationId,
        Guid questionId, DateTimeOffset startedAt, ChatTokenUsage usage, string? modelId, CancellationToken ct)
    {
        if (await db.ChatTokenUsage.AnyAsync(x => x.QuestionId == questionId, ct))
        {
            return;
        }

        db.ChatTokenUsage.Add(new ChatTokenUsageEntity
        {
            ConversationId = conversationId,
            QuestionId = questionId,
            Sequence = ChatTokenUsageEntity.TurnTotalSequence,
            UserId = userId,
            Month = MonthKey(startedAt),
            Day = DayKey(startedAt),
            ModelId = modelId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            InputTokens = Math.Max(0, usage.InputTokens),
            OutputTokens = Math.Max(0, usage.OutputTokens),
            TotalTokens = Math.Max(0, usage.TotalTokens)
        });
        await db.SaveChangesAsync(ct);
    }

    public sealed class TurnLease(SharePointIndexDbContext db, string resource, Guid userId, DateTimeOffset startedAt) : IAsyncDisposable
    {
        public int Month { get; } = MonthKey(startedAt);

        /// <summary>
        /// When the turn started. The agent stamps every row of the turn with this, so all of a turn's
        /// requests land in the day and month whose allowance was checked at the start.
        /// </summary>
        public DateTimeOffset StartedAt { get; } = startedAt;

        public Task RecordAsync(Guid conversationId, Guid questionId, ChatTokenUsage usage, string? modelId, CancellationToken ct) =>
            RecordUsageAsync(db, userId, conversationId, questionId, StartedAt, usage, modelId, ct);

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource=@resource, @LockOwner='Session';";
                AddResource(command, resource);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                await db.DisposeAsync();
            }
        }
    }
}
