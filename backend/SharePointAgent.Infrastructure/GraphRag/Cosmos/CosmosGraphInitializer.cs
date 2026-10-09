using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.GraphRag.Cosmos;

/// <summary>
/// The container layout, shared by this initializer and documented for the Bicep template. Indexing covers
/// only the fields queries filter on; everything else is excluded, which keeps write RU low because the large
/// fields (aliases, manifests, evidence) are never indexed.
/// </summary>
public static class CosmosGraphLayout
{
    public const string PartitionKeyPath = "/partitionKey";

    public static ContainerProperties Entities(string name) => Container(name, "/tenantId/?");

    public static ContainerProperties Assertions(string name) =>
        Container(name, "/tenantId/?", "/subjectEntityId/?", "/objectEntityId/?", "/predicate/?", "/status/?");

    public static ContainerProperties DocumentStates(string name) => Container(name, "/tenantId/?", "/status/?");

    private static ContainerProperties Container(string name, params string[] includedPaths)
    {
        var properties = new ContainerProperties(name, PartitionKeyPath)
        {
            // On, with no default expiry: only items that carry their own ttl (retracted assertions and
            // tombstones) ever expire.
            DefaultTimeToLive = -1,
            IndexingPolicy = new IndexingPolicy { Automatic = true, IndexingMode = IndexingMode.Consistent }
        };
        foreach (var path in includedPaths)
        {
            properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
        }
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        return properties;
    }
}

/// <summary>
/// Creates the database and containers when <see cref="CosmosGraphOptions.CreateIfNotExists"/> is set. That
/// needs key-based control-plane access, so it is meant for the emulator and local development; deployed
/// accounts are provisioned by Bicep and reached with data-plane roles only.
/// </summary>
public sealed class CosmosGraphInitializer(
    GraphCosmosClient client,
    IOptions<GraphRagOptions> options,
    ILogger<CosmosGraphInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cosmos = options.Value.Cosmos;
        if (!cosmos.CreateIfNotExists)
        {
            return;
        }

        var database = (await client.Client.CreateDatabaseIfNotExistsAsync(cosmos.DatabaseName, cancellationToken: cancellationToken)).Database;
        await database.CreateContainerIfNotExistsAsync(CosmosGraphLayout.Entities(cosmos.EntitiesContainer), cancellationToken: cancellationToken);
        await database.CreateContainerIfNotExistsAsync(CosmosGraphLayout.Assertions(cosmos.AssertionsContainer), cancellationToken: cancellationToken);
        await database.CreateContainerIfNotExistsAsync(CosmosGraphLayout.Assertions(cosmos.ReverseAssertionsContainer), cancellationToken: cancellationToken);
        await database.CreateContainerIfNotExistsAsync(CosmosGraphLayout.DocumentStates(cosmos.DocumentStateContainer), cancellationToken: cancellationToken);
        logger.LogInformation("Ensured the Graph RAG Cosmos database {Database} and its containers.", cosmos.DatabaseName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
