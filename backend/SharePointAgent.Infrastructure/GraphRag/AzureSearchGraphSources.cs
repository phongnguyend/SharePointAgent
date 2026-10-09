using System.Text.RegularExpressions;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using SharePointAgent.Application;
using SharePointAgent.Domain;

using AzureSearchOptions = Azure.Search.Documents.SearchOptions;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Reads a file's chunks back from the existing SharePoint index for extraction, so the graph pipeline
/// neither crawls SharePoint nor extracts or embeds anything again. This is a trusted, worker-only read: its
/// output goes to the extraction model and the archive's hashes, never to a user.
/// </summary>
public sealed class AzureSearchGraphChunkSource(SearchClient searchClient) : IGraphChunkSource
{
    public async Task<IReadOnlyList<GraphSourceChunk>> GetChunksAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        var options = new AzureSearchOptions
        {
            Filter = $"driveId eq '{SearchPermissionFilter.Escape(driveId)}' and itemId eq '{SearchPermissionFilter.Escape(itemId)}'",
            Select = { "id", "chunkNumber", "content" },
            OrderBy = { "chunkNumber asc" }
        };
        var response = await searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken);
        var chunks = new List<GraphSourceChunk>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var document = result.Document;
            chunks.Add(new GraphSourceChunk(
                document["id"].ToString()!,
                Convert.ToInt32(document["chunkNumber"], System.Globalization.CultureInfo.InvariantCulture),
                document["content"]?.ToString() ?? ""));
        }
        return chunks;
    }
}

/// <summary>
/// Fetches named chunks through the same permission filter as the search endpoints. It refuses to run
/// without a user rather than fall back to an unfiltered read the way a search with no user does.
/// </summary>
public sealed partial class AzureSearchAuthorizedChunkStore(SearchClient searchClient, SharePointClient sharePointClient) : IAuthorizedChunkStore
{
    private const int MaxChunksPerRequest = 200;

    private static readonly string[] ProjectedFields =
    [
        "id", "driveId", "itemId", "name", "path", "webUrl",
        "mimeType", "size", "lastModifiedUtc", "chunkNumber", "content"
    ];

    public async Task<IReadOnlyList<SearchQueryHit>> GetAuthorizedChunksAsync(
        string userId, IReadOnlyCollection<string> chunkIds, CancellationToken cancellationToken)
    {
        var permissionFilter = await SearchPermissionFilter.BuildAsync(sharePointClient, userId, cancellationToken)
            ?? throw new ArgumentException("Authorized chunks can only be read for a specific user.", nameof(userId));

        // Chunk keys are base64url; anything else cannot be a key and is not put into a filter.
        var ids = chunkIds.Where(id => ChunkKeyPattern().IsMatch(id)).Distinct(StringComparer.Ordinal).ToList();
        var hits = new List<SearchQueryHit>();
        foreach (var batch in ids.Chunk(MaxChunksPerRequest))
        {
            var options = new AzureSearchOptions
            {
                Filter = $"search.in(id, '{string.Join(',', batch)}', ',') and ({permissionFilter})",
                Size = batch.Length
            };
            foreach (var field in ProjectedFields)
            {
                options.Select.Add(field);
            }

            var response = await searchClient.SearchAsync<SearchChunkDocument>("*", options, cancellationToken);
            await foreach (var result in response.Value.GetResultsAsync())
            {
                var document = result.Document;
                hits.Add(new SearchQueryHit(
                    document.Id, document.DriveId, document.ItemId, document.Name, document.Path, document.WebUrl,
                    document.MimeType, document.Size, document.LastModifiedUtc, document.ChunkNumber, document.Content, result.Score));
            }
        }
        return hits;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,1024}$")]
    private static partial Regex ChunkKeyPattern();
}
