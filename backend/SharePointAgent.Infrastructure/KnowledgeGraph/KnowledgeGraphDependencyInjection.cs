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
using SharePointAgent.Infrastructure.KnowledgeGraph.Cosmos;

using SearchOptions = SharePointAgent.Application.SearchOptions;

namespace SharePointAgent.Infrastructure.KnowledgeGraph;

/// <summary>
/// Registers knowledge graph retrieval for agent tool assignment and optional indexing services.
/// </summary>
public static class KnowledgeGraphDependencyInjection
{
    public static bool IsGraphIndexingEnabled(this IConfiguration configuration) =>
        configuration.GetValue($"{KnowledgeGraphOptions.SectionName}:{nameof(KnowledgeGraphOptions.IndexingEnabled)}", false);

    public static bool IsGraphIndexingSynchronous(this IConfiguration configuration) =>
        configuration.GetValue($"{KnowledgeGraphOptions.SectionName}:{nameof(KnowledgeGraphOptions.SynchronousIndexing)}", false);

    /// <summary>
    /// The indexer's hook: inline execution when requested, otherwise Service Bus publishing when enabled.
    /// Called from every registration that adds the SharePoint change processor.
    /// </summary>
    public static IServiceCollection AddGraphIndexingSignal(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.IsGraphIndexingEnabled() && configuration.IsGraphIndexingSynchronous())
        {
            services.AddGraphIndexingServices(configuration);
            services.TryAddSingleton<IGraphIndexingSignal, SynchronousGraphIndexingSignal>();
        }
        else if (configuration.IsGraphIndexingEnabled())
        {
            AddKnowledgeGraphOptions(services, configuration, requireArchive: false);
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

        AddKnowledgeGraphOptions(services, configuration, requireArchive: true);
        services.AddOptions<KnowledgeGraphOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.Extraction.Deployment),
                "KnowledgeGraph:Extraction:Deployment is required when graph indexing is enabled.")
            .ValidateOnStart();
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
        AddKnowledgeGraphOptions(services, configuration, requireArchive: false);
        AddGraphStore(services);
        services.TryAddSingleton<IAuthorizedChunkStore, AzureSearchAuthorizedChunkStore>();
        services.TryAddSingleton<GraphQueryPlanValidator>();
        services.TryAddSingleton<GraphEntityLinker>();
        services.TryAddSingleton<GraphEvidenceVerifier>();
        services.TryAddSingleton<IGraphRetrievalService, GraphRetrievalService>();
        services.TryAddSingleton<IGraphQueryPlanner, AzureOpenAiGraphQueryPlanner>();
        return services;
    }

    private static void AddGraphStore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<KnowledgeGraphOptions>>().Value.CreateOntology());
        services.TryAddSingleton(sp => new GraphPartitionLocator(sp.GetRequiredService<IOptions<KnowledgeGraphOptions>>().Value.Cosmos.PartitionBuckets));
        services.TryAddSingleton(sp => new GraphCosmosClient(CreateCosmosClient(sp.GetRequiredService<IOptions<KnowledgeGraphOptions>>().Value.Cosmos)));
        services.TryAddSingleton<IGraphProjectionStore, CosmosGraphProjectionStore>();
        services.TryAddSingleton<IGraphWriter, GraphProjectionWriter>();
        services.TryAddSingleton<IGraphReader, BoundedGraphTraversal>();
        services.TryAddSingleton<IEntityAliasIndex>(sp => new AzureSearchEntityAliasIndex(
            CreateSearchIndexClient(sp.GetRequiredService<IOptions<SearchOptions>>().Value), sp.GetRequiredService<IOptions<KnowledgeGraphOptions>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, CosmosGraphInitializer>());
    }

    private static void AddKnowledgeGraphOptions(IServiceCollection services, IConfiguration configuration, bool requireArchive)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(KnowledgeGraphOptionsMarker)))
        {
            if (requireArchive)
            {
                services.AddOptions<KnowledgeGraphOptions>().Validate(o => o.Archive.IsConfigured,
                    "KnowledgeGraph:Archive:ServiceUri is required with managed identity; otherwise KnowledgeGraph:Archive:ConnectionString is required.");
            }
            return;
        }

        services.AddSingleton<KnowledgeGraphOptionsMarker>();
        var builder = services.AddOptions<KnowledgeGraphOptions>()
            .Bind(configuration.GetSection(KnowledgeGraphOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => ValidateNested(o.Extraction) && ValidateNested(o.Traversal) && ValidateNested(o.Reconciliation)
                && ValidateNested(o.Cosmos) && ValidateNested(o.Archive), "A KnowledgeGraph setting is out of range; see the ranges on KnowledgeGraphOptions.")
            .Validate(o => o.Cosmos.IsConfigured,
                "KnowledgeGraph:Cosmos:Endpoint is required with managed identity; otherwise KnowledgeGraph:Cosmos:ConnectionString is required.")
            .Validate(o => GraphOntology.ValidateDefinition(o.Ontology ?? GraphOntologyDefaults.CreateDefinition()).Count == 0,
                "KnowledgeGraph:Ontology is invalid; GraphOntology.ValidateDefinition lists every problem.");
        if (requireArchive)
        {
            builder.Validate(o => o.Archive.IsConfigured,
                "KnowledgeGraph:Archive:ServiceUri is required with managed identity; otherwise KnowledgeGraph:Archive:ConnectionString is required.");
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
            ApplicationName = "SharePointAgent.KnowledgeGraph",
            UseSystemTextJsonSerializerWithOptions = serializer,
            MaxRetryAttemptsOnRateLimitedRequests = options.MaxRetryAttemptsOnThrottling,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
            EnableContentResponseOnWrite = false
        };
        return options.UseManagedIdentity
            ? new CosmosClient(options.Endpoint, DependencyInjection.CreateDefaultAzureCredential(), clientOptions)
            : new CosmosClient(options.ConnectionString, clientOptions);
    }

    private static SearchIndexClient CreateSearchIndexClient(SearchOptions options) => options.UseManagedIdentity
        ? new SearchIndexClient(new Uri(options.Endpoint), DependencyInjection.CreateDefaultAzureCredential())
        : new SearchIndexClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey!));

    /// <summary>Marks that the options are already bound, so hosts that add both sides bind them once.</summary>
    private sealed class KnowledgeGraphOptionsMarker;
}
