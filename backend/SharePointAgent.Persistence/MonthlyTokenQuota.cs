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
            if (result < 0) throw new UserManagementException("A chat response is already running for your account. Wait for it to finish before sending another message.", 429);
            lease = new TurnLease(db, resource, userId, clock.GetUtcNow());
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId, ct);
            var used = await UsedAsync(db, userId, lease.Month, ct);
            EnsureAvailable(user.MonthlyTokenLimit, used);
            return lease;
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            else await db.DisposeAsync();
            throw;
        }
    }

    public static void EnsureAvailable(long? limit, long used)
    {
        if (limit.HasValue && used >= limit.Value)
            throw new UserManagementException("Your monthly token limit has been reached. Contact an administrator or wait until the next month (UTC).", 429);
    }

    public static async Task<long> UsedAsync(SharePointIndexDbContext db, Guid userId, int month, CancellationToken ct = default) =>
        await db.UserTokenUsage.Where(x => x.UserId == userId && x.Month == month).SumAsync(x => (long?)x.TotalTokens, ct) ?? 0;

    private static void AddResource(DbCommand command, string resource)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = resource;
        command.Parameters.Add(parameter);
    }

    public static async Task RecordUsageAsync(SharePointIndexDbContext db, Guid userId, Guid questionId,
        DateTimeOffset startedAt, ChatTokenUsage usage, string? modelId, CancellationToken ct)
    {
        if (await db.UserTokenUsage.AnyAsync(x => x.QuestionId == questionId, ct)) return;
        db.UserTokenUsage.Add(new UserTokenUsageEntity
        {
            QuestionId = questionId, UserId = userId, Month = MonthKey(startedAt), Day = DayKey(startedAt),
            ModelId = modelId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            InputTokens = Math.Max(0, usage.InputTokens), OutputTokens = Math.Max(0, usage.OutputTokens),
            TotalTokens = Math.Max(0, usage.TotalTokens)
        });
        await db.SaveChangesAsync(ct);
    }

    public sealed class TurnLease(SharePointIndexDbContext db, string resource, Guid userId, DateTimeOffset startedAt) : IAsyncDisposable
    {
        public int Month { get; } = MonthKey(startedAt);
        public Task RecordAsync(Guid questionId, ChatTokenUsage usage, string? modelId, CancellationToken ct) =>
            RecordUsageAsync(db, userId, questionId, startedAt, usage, modelId, ct);

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource=@resource, @LockOwner='Session';";
                AddResource(command, resource);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally { await db.DisposeAsync(); }
        }
    }
}
