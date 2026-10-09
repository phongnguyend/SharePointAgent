using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Search.Documents.Indexes;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.GraphRag.Cosmos;

using SearchOptions = SharePointAgent.Application.SearchOptions;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Registers Graph RAG behind its feature flags. With the flags off nothing is registered beyond a no-op
/// indexing hook, no setting is validated, and the hosts behave exactly as they did before.
/// </summary>
public static class GraphRagDependencyInjection
{
    public static bool IsGraphIndexingEnabled(this IConfiguration configuration) =>
        configuration.GetValue($"{GraphRagOptions.SectionName}:{nameof(GraphRagOptions.IndexingEnabled)}", false);

    public static bool IsGraphRetrievalEnabled(this IConfiguration configuration) =>
        configuration.GetValue($"{GraphRagOptions.SectionName}:{nameof(GraphRagOptions.RetrievalEnabled)}", false)
        || configuration.GetValue($"{GraphRagOptions.SectionName}:{nameof(GraphRagOptions.ShadowRetrieval)}", false);

    /// <summary>
    /// The indexer's hook: a Service Bus publisher when graph indexing and Service Bus are both on, otherwise
    /// a no-op. Called from every registration that adds the SharePoint change processor.
    /// </summary>
    public static IServiceCollection AddGraphIndexingSignal(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.IsGraphIndexingEnabled() && configuration.IsServiceBusEnabled())
        {
            AddGraphRagOptions(services, configuration, requireArchive: false);
            services.TryAddSingleton<IGraphIndexingSignal, ServiceBusGraphIndexingSignal>();
        }
        else
        {
            services.TryAddSingleton<IGraphIndexingSignal, NullGraphIndexingSignal>();
        }
        return services;
    }

    /// <summary>The worker side: extraction, resolution, archive, projection, and reconciliation.</summary>
    public static IServiceCollection AddGraphIndexingServices(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.IsGraphIndexingEnabled())
        {
            return services;
        }

        AddGraphRagOptions(services, configuration, requireArchive: true);
        AddGraphStore(services);
        services.TryAddSingleton<IGraphChunkSource, AzureSearchGraphChunkSource>();
        services.TryAddSingleton<IGraphExtractor, AzureOpenAiGraphExtractor>();
        services.TryAddSingleton<IEntityResolver, EntityResolver>();
        services.TryAddSingleton<ICanonicalGraphArchive, BlobCanonicalGraphArchive>();
        services.TryAddSingleton<IGraphIndexingPipeline, GraphIndexingPipeline>();
        services.TryAddSingleton<GraphReconciliationService>();
        services.TryAddSingleton<GraphIndexingMessageHandler>();
        return services;
    }

    /// <summary>The query side, added to every host that runs the chat agent.</summary>
    public static IServiceCollection AddGraphRetrievalServices(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.IsGraphRetrievalEnabled())
        {
            return services;
        }

        AddGraphRagOptions(services, configuration, requireArchive: false);
        AddGraphStore(services);
        services.TryAddSingleton<IAuthorizedChunkStore, AzureSearchAuthorizedChunkStore>();
        services.TryAddSingleton<GraphQueryRouter>();
        services.TryAddSingleton<GraphEntityLinker>();
        services.TryAddSingleton<GraphEvidenceVerifier>();
        services.TryAddSingleton<IGraphRetrievalService, GraphRetrievalService>();
        return services;
    }

    private static void AddGraphStore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<GraphRagOptions>>().Value.CreateOntology());
        services.TryAddSingleton(sp => new GraphPartitionLocator(sp.GetRequiredService<IOptions<GraphRagOptions>>().Value.Cosmos.PartitionBuckets));
        services.TryAddSingleton(sp => new GraphCosmosClient(CreateCosmosClient(sp.GetRequiredService<IOptions<GraphRagOptions>>().Value.Cosmos)));
        services.TryAddSingleton<IGraphProjectionStore, CosmosGraphProjectionStore>();
        services.TryAddSingleton<IGraphWriter, GraphProjectionWriter>();
        services.TryAddSingleton<IGraphReader, BoundedGraphTraversal>();
        services.TryAddSingleton<IEntityAliasIndex>(sp => new AzureSearchEntityAliasIndex(
            CreateSearchIndexClient(sp.GetRequiredService<IOptions<SearchOptions>>().Value), sp.GetRequiredService<IOptions<GraphRagOptions>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, CosmosGraphInitializer>());
    }

    private static void AddGraphRagOptions(IServiceCollection services, IConfiguration configuration, bool requireArchive)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(GraphRagOptionsMarker)))
        {
            if (requireArchive)
            {
                services.AddOptions<GraphRagOptions>().Validate(o => o.Archive.IsConfigured,
                    "GraphRag:Archive:ServiceUri is required with managed identity; otherwise GraphRag:Archive:ConnectionString is required.");
            }
            return;
        }

        services.AddSingleton<GraphRagOptionsMarker>();
        var builder = services.AddOptions<GraphRagOptions>()
            .Bind(configuration.GetSection(GraphRagOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => ValidateNested(o.Extraction) && ValidateNested(o.Traversal) && ValidateNested(o.Reconciliation)
                && ValidateNested(o.Cosmos) && ValidateNested(o.Archive), "A GraphRag setting is out of range; see the ranges on GraphRagOptions.")
            .Validate(o => o.Cosmos.IsConfigured,
                "GraphRag:Cosmos:Endpoint is required with managed identity; otherwise GraphRag:Cosmos:ConnectionString is required.")
            .Validate(o => GraphOntology.ValidateDefinition(o.Ontology ?? GraphOntologyDefaults.CreateDefinition()).Count == 0,
                "GraphRag:Ontology is invalid; GraphOntology.ValidateDefinition lists every problem.");
        if (requireArchive)
        {
            builder.Validate(o => o.Archive.IsConfigured,
                "GraphRag:Archive:ServiceUri is required with managed identity; otherwise GraphRag:Archive:ConnectionString is required.");
        }
        builder.ValidateOnStart();
    }

    private static bool ValidateNested(object value) =>
        Validator.TryValidateObject(value, new ValidationContext(value), null, validateAllProperties: true);

    private static CosmosClient CreateCosmosClient(CosmosGraphOptions options)
    {
        var serializer = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter() }
        };
        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = "SharePointAgent.GraphRag",
            UseSystemTextJsonSerializerWithOptions = serializer,
            MaxRetryAttemptsOnRateLimitedRequests = options.MaxRetryAttemptsOnThrottling,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
            EnableContentResponseOnWrite = false
        };
        return options.UsedManagedIdentity
            ? new CosmosClient(options.Endpoint, DependencyInjection.CreateManagedIdentityCredential(), clientOptions)
            : new CosmosClient(options.ConnectionString, clientOptions);
    }

    private static SearchIndexClient CreateSearchIndexClient(SearchOptions options) => options.UsedManagedIdentity
        ? new SearchIndexClient(new Uri(options.Endpoint), DependencyInjection.CreateManagedIdentityCredential())
        : new SearchIndexClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey!));

    /// <summary>Marks that the options are already bound, so hosts that add both sides bind them once.</summary>
    private sealed class GraphRagOptionsMarker;
}
