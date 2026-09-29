using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Persistence;

/// <summary>Seeds required application data without changing the database schema.</summary>
public sealed class DatabaseSeedHostedService(
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    IOptions<OpenAiOptions> openAiOptions,
    IOptions<SharePointOptions> sharePointOptions,
    ILogger<DatabaseSeedHostedService> logger) : IHostedService
{
    private readonly string _defaultModelId = openAiOptions.Value.ChatDeployment;
    private readonly SharePointOptions _sharePointOptions = sharePointOptions.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await BackfillAgentModelIdsAsync(context, cancellationToken);
        await SeedDefaultAgentAsync(context, cancellationToken);
        await SeedDefaultWebhookSubscriptionAsync(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task BackfillAgentModelIdsAsync(
        SharePointIndexDbContext context,
        CancellationToken cancellationToken)
    {
        var updated = await context.AgentDefinitions
            .Where(agent => agent.ModelId == null || agent.ModelId == "")
            .ExecuteUpdateAsync(
                update => update.SetProperty(agent => agent.ModelId, _defaultModelId),
                cancellationToken);
        if (updated > 0)
        {
            logger.LogInformation(
                "Assigned the configured default model to {Count} existing agent(s).",
                updated);
        }
    }

    private async Task SeedDefaultAgentAsync(
        SharePointIndexDbContext context,
        CancellationToken cancellationToken)
    {
        if (await context.AgentDefinitions.AnyAsync(a => a.Name == AgentDefaults.Name, cancellationToken))
        {
            logger.LogInformation("The default agent already exists.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        context.AgentDefinitions.Add(new AgentDefinitionEntity
        {
            Name = AgentDefaults.Name,
            ModelId = _defaultModelId,
            Instructions = AgentDefaults.Instructions,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created the default agent.");
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // The unique index closes the race when multiple application instances start together.
            context.ChangeTracker.Clear();
            logger.LogInformation("The default agent was created by another application instance.");
        }
    }

    private async Task SeedDefaultWebhookSubscriptionAsync(
        SharePointIndexDbContext context,
        CancellationToken cancellationToken)
    {
        if (await context.WebhookSubscriptions.AnyAsync(
                item => item.Name == WebhookSubscriptionDefaults.Name,
                cancellationToken))
        {
            logger.LogInformation("The default webhook subscription already exists.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        context.WebhookSubscriptions.Add(new WebhookSubscriptionEntity
        {
            Name = WebhookSubscriptionDefaults.Name,
            NotificationUrl = _sharePointOptions.NotificationUrl,
            AutoRenewEnabled = true,
            LifetimeDays = _sharePointOptions.SubscriptionLifetimeDays,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created the default webhook subscription from configuration.");
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            context.ChangeTracker.Clear();
            logger.LogInformation("The default webhook subscription was created by another application instance.");
        }
    }
}
