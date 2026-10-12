using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

internal static class SystemOneUsageRecorder
{
    public static async Task RecordAsync(IDbContextFactory<SharePointIndexDbContext>? contextFactory,
        string provider, string model, int questionCount, SystemOneUsage usage, SystemOneUsageContext usageContext)
    {
        if (contextFactory is not null)
        {
            var now = DateTimeOffset.UtcNow;
            // The evaluation has completed: persist billed usage even if the caller disconnected.
            await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            db.SystemOneUsage.Add(new SystemOneUsageEntity
            {
                Provider = provider,
                CreatedAtUtc = now,
                Day = now.Year * 10000 + now.Month * 100 + now.Day,
                Month = now.Year * 100 + now.Month,
                UserId = usageContext.UserId,
                ConversationId = usageContext.ConversationId,
                QuestionId = usageContext.QuestionId,
                ModelId = model,
                TraceId = Activity.Current?.TraceId.ToString(),
                QuestionCount = questionCount,
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                TotalTokens = checked(usage.InputTokens + usage.OutputTokens)
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
