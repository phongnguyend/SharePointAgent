using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ImageDescriptionUsagePersistenceTests
{
    [Fact]
    public async Task InsertUsesDatabaseDefaultAndReturnsGeneratedId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var databaseId = Guid.Parse("37c36940-85b4-4da5-b49b-c5e7ea0f49da");
        connection.CreateFunction("NEWSEQUENTIALID", () => databaseId.ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var row = new ImageDescriptionTokenUsageEntity
        {
            ModelId = "vision",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Prompt = "Describe this image.",
            Description = "A diagram."
        };

        Assert.Equal(Guid.Empty, row.Id);
        db.ImageDescriptionTokenUsage.Add(row);
        await db.SaveChangesAsync();

        Assert.Equal(databaseId, row.Id);
        db.ChangeTracker.Clear();
        var stored = await db.ImageDescriptionTokenUsage.SingleAsync();
        Assert.Equal(databaseId, stored.Id);
        Assert.Equal(row.Prompt, stored.Prompt);
        Assert.Equal(row.Description, stored.Description);
    }
}
