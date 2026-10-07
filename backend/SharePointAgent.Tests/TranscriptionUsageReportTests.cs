using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SharePointAgent.Api;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class TranscriptionUsageReportTests
{
    [Fact]
    public async Task ReportSummarisesFiltersAndPagesVoiceUsage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SortableDateTimeOffsets>().Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var speaker = new ApplicationUser { UserName = "speaker@example.com", Email = "speaker@example.com", DisplayName = "Speaker Name" };
        var other = Guid.NewGuid();
        db.Users.Add(speaker);
        TranscriptionTokenUsageEntity Row(Guid user, DateTimeOffset at, string model, long? total, double? seconds = null) => new()
        {
            CreatedAtUtc = at, Day = MonthlyTokenQuota.DayKey(at), Month = MonthlyTokenQuota.MonthKey(at), UserId = user, ModelId = model,
            AudioBytes = 1000, DurationSeconds = seconds, InputTokens = total is null ? null : total - 10, OutputTokens = total is null ? null : 10, TotalTokens = total
        };
        db.TranscriptionTokenUsage.AddRange(
            Row(speaker.Id, new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), "gpt-4o-transcribe", 100),
            Row(speaker.Id, new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero), "gpt-4o-transcribe", 50),
            Row(other, new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero), "whisper", null, 4.5),
            Row(speaker.Id, new(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), "gpt-4o-transcribe", 999));
        await db.SaveChangesAsync();

        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 10, 2);
        var report = await Read(db, from, to);
        var summary = report.GetProperty("summary");
        Assert.Equal((3, 1, 150L, 3000L, 4.5), (summary.GetProperty("calls").GetInt32(), summary.GetProperty("unknownUsage").GetInt32(),
            summary.GetProperty("totalTokens").GetInt64(), summary.GetProperty("audioBytes").GetInt64(), summary.GetProperty("durationSeconds").GetDouble()));
        Assert.Equal(["2026-10-01", "2026-10-02"], report.GetProperty("daily").EnumerateArray().Select(x => x.GetProperty("day").GetString()));
        Assert.Equal(["gpt-4o-transcribe", "whisper"], report.GetProperty("models").EnumerateArray().Select(x => x.GetProperty("modelId").GetString()));
        var newest = report.GetProperty("items")[0];
        Assert.Equal("whisper", newest.GetProperty("modelId").GetString());

        var filtered = await Read(db, from, to, user: speaker.Id.ToString(), model: "gpt-4o-transcribe", top: 1);
        Assert.Equal(2, filtered.GetProperty("summary").GetProperty("calls").GetInt32());
        var item = Assert.Single(filtered.GetProperty("items").EnumerateArray());
        Assert.Equal(("Speaker Name", "speaker@example.com", 50L),
            (item.GetProperty("userName").GetString(), item.GetProperty("userEmail").GetString(), item.GetProperty("totalTokens").GetInt64()));
        Assert.Equal(2, (await Read(db, from, to, user: "Speaker")).GetProperty("summary").GetProperty("calls").GetInt32());
        Assert.Equal(2, (await Read(db, from, to, user: "speaker@exam")).GetProperty("summary").GetProperty("calls").GetInt32());
        Assert.Equal(0, (await Read(db, from, to, user: "nobody")).GetProperty("summary").GetProperty("calls").GetInt32());

        var empty = await Read(db, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2));
        Assert.Equal(0, empty.GetProperty("summary").GetProperty("calls").GetInt32());
    }

    /// <summary>SQLite cannot sort DateTimeOffset values, which the report does on SQL Server; store them as ticks here.</summary>
    private sealed class SortableDateTimeOffsets(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties())
                .Where(x => x.ClrType == typeof(DateTimeOffset) || x.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }

    private static async Task<JsonElement> Read(SharePointIndexDbContext db, DateOnly from, DateOnly to, string? user = null, string? model = null, int top = 25)
    {
        var result = await TranscriptionUsageEndpoints.ReadAsync(db, default, from, to, model, user, 0, top);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
