using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class DatabaseSeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeedingIsRegisteredAfterOptionalMigrations(bool autoMigrate)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlServer:AutoMigrate"] = autoMigrate.ToString(),
        }).Build();
        var services = new ServiceCollection();
        services.AddPersistence(configuration);
        services.AddPersistence(configuration);
        var hosted = services.Where(x => x.ServiceType == typeof(IHostedService))
            .Select(x => x.ImplementationType).ToArray();

        Assert.Equal(autoMigrate
            ? new[] { typeof(DatabaseMigrationHostedService), typeof(DatabaseSeedHostedService) }
            : new[] { typeof(DatabaseSeedHostedService) }, hosted);
    }

    [Fact]
    public async Task SeedsPipelineCreatedSchemaAndPreservesEditedDefaultsOnRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        await using var db = new SharePointIndexDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new SharePointIndexDbContext(options));
        var service = new DatabaseSeedHostedService(factory,
            Options.Create(new OpenAiOptions { ChatDeployment = "chat-model" }),
            Options.Create(new SharePointOptions { NotificationUrl = "https://example.com/webhook", SubscriptionLifetimeDays = 10 }),
            NullLogger<DatabaseSeedHostedService>.Instance);

        await service.StartAsync(default);
        var subscription = await db.WebhookSubscriptions.SingleAsync();
        Assert.Equal(WebhookSubscriptionDefaults.Name, subscription.Name);
        Assert.Equal("https://example.com/webhook", subscription.NotificationUrl);
        Assert.Equal(10, subscription.LifetimeDays);
        var agent = await db.AgentDefinitions.SingleAsync();
        Assert.Equal(AgentDefaults.Name, agent.Name);
        Assert.Equal("chat-model", agent.ModelId);
        subscription.NotificationUrl = "https://example.com/edited";
        agent.ModelId = "edited-model";
        await db.SaveChangesAsync();

        await service.StartAsync(default);
        db.ChangeTracker.Clear();
        Assert.Equal("https://example.com/edited", (await db.WebhookSubscriptions.SingleAsync()).NotificationUrl);
        Assert.Equal("edited-model", (await db.AgentDefinitions.SingleAsync()).ModelId);
    }
}
