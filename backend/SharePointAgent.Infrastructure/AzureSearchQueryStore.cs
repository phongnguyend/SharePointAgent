using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AzureSearchOptions = Azure.Search.Documents.SearchOptions;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public sealed class AzureSearchQueryStore(
    SearchClient searchClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    SharePointClient sharePointClient,
    ILogger<AzureSearchQueryStore> logger) : ISearchQueryStore
{
    private static readonly string[] ProjectedFields =
    [
        "id", "driveId", "itemId", "name", "path", "webUrl",
        "mimeType", "size", "lastModifiedUtc", "chunkNumber", "content"
    ];

    public async Task<SearchQueryResults> SearchAsync(SearchQueryMode mode, SearchQueryRequest request, CancellationToken cancellationToken)
    {
        var options = new AzureSearchOptions
        {
            Filter = await SearchPermissionFilter.BuildAsync(sharePointClient, request.UserId, cancellationToken),
            Size = request.Top,
            Skip = request.Skip,
            IncludeTotalCount = true
        };
        foreach (var field in ProjectedFields)
        {
            options.Select.Add(field);
        }

        if (mode is SearchQueryMode.Vector or SearchQueryMode.Hybrid)
        {
            using var embeddingAttribution = EmbeddingUsageScope.Begin(new(Operation: "SharePoint" + mode + "Search"));
            var vector = await ChatEmbeddingUsage.GenerateQueryVectorAsync(embeddings, request.Query, cancellationToken);
            options.VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(vector)
                    {
                        // Retrieve enough neighbours to still fill the requested page after skipping.
                        KNearestNeighborsCount = request.Top + request.Skip,
                        Fields = { "contentVector" }
                    }
                }
            };
        }

        // Pure vector search must not send query text, otherwise the request becomes a hybrid one.
        var searchText = mode is SearchQueryMode.Vector ? null : request.Query;
        var response = await searchClient.SearchAsync<SearchChunkDocument>(searchText, options, cancellationToken);

        var items = new List<SearchQueryHit>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var document = result.Document;
            items.Add(new SearchQueryHit(
                document.Id,
                document.DriveId,
                document.ItemId,
                document.Name,
                document.Path,
                document.WebUrl,
                document.MimeType,
                document.Size,
                document.LastModifiedUtc,
                document.ChunkNumber,
                document.Content,
                result.Score));
        }

        logger.LogInformation("{Mode} search returned {Count} chunks for user {UserId}.", mode, items.Count, request.UserId ?? "(unfiltered)");
        return new SearchQueryResults(response.Value.TotalCount, items);
    }
}
