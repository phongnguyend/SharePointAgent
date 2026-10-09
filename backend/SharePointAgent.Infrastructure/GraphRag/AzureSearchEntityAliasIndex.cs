using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

using AzureSearchOptions = Azure.Search.Documents.SearchOptions;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// The alias lookup, as a dedicated Azure AI Search index: the SharePoint index has no entity fields and
/// should stay unchanged. Lookups are exact filter matches on the normalized alias, always within one tenant,
/// so resolution never scans the graph store. The index holds names from many documents and is read only
/// on the server side; nothing from it is shown to a user.
/// </summary>
public sealed class AzureSearchEntityAliasIndex(SearchIndexClient indexClient, IOptions<GraphRagOptions> options) : IEntityAliasIndex
{
    private const int AliasesPerQuery = 30;

    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly SearchClient _client = indexClient.GetSearchClient(options.Value.AliasIndexName);
    private bool _initialized;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FindAsync(
        string tenantId, string? entityType, IReadOnlyCollection<string> normalizedAliases, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var batch in normalizedAliases.Where(alias => alias.Length > 0).Distinct(StringComparer.Ordinal).Chunk(AliasesPerQuery))
        {
            var filter = new StringBuilder($"tenantId eq '{Escape(tenantId)}'");
            if (entityType is not null)
            {
                filter.Append($" and entityType eq '{Escape(entityType)}'");
            }
            filter.Append(" and (").AppendJoin(" or ", batch.Select(alias => $"normalizedAlias eq '{Escape(alias)}'")).Append(')');

            var search = new AzureSearchOptions { Filter = filter.ToString(), Size = 1000 };
            search.Select.Add("normalizedAlias");
            search.Select.Add("entityId");
            var response = await _client.SearchAsync<AliasDocument>("*", search, cancellationToken);
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (!found.TryGetValue(result.Document.NormalizedAlias, out var ids))
                {
                    ids = [];
                    found[result.Document.NormalizedAlias] = ids;
                }

                if (!ids.Contains(result.Document.EntityId))
                {
                    ids.Add(result.Document.EntityId);
                }
            }
        }
        return found.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
    }

    public async Task UpsertAsync(IReadOnlyCollection<EntityAliasEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await EnsureIndexAsync(cancellationToken);
        var documents = entries
            .Where(entry => entry.NormalizedAlias.Length > 0)
            .Select(entry => new AliasDocument
            {
                Id = Key(entry),
                TenantId = entry.TenantId,
                EntityType = entry.EntityType,
                NormalizedAlias = entry.NormalizedAlias,
                EntityId = entry.EntityId
            })
            .DistinctBy(document => document.Id)
            .ToList();
        foreach (var batch in documents.Chunk(1000))
        {
            await _client.MergeOrUploadDocumentsAsync(batch, cancellationToken: cancellationToken);
        }
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var index = new SearchIndex(_client.IndexName, new List<SearchField>
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
                new SimpleField("tenantId", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("entityType", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("normalizedAlias", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("entityId", SearchFieldDataType.String) { IsFilterable = true }
            });
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
                    break;
                }
                catch (RequestFailedException exception) when (exception.Status == 409 && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << attempt, 16)), cancellationToken);
                }
            }
            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    /// <summary>A key per tenant, type, alias, and entity: index keys allow only URL-safe characters.</summary>
    private static string Key(EntityAliasEntry entry)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{entry.TenantId.Length}:{entry.TenantId};{entry.EntityType.Length}:{entry.EntityType};{entry.NormalizedAlias.Length}:{entry.NormalizedAlias};{entry.EntityId.Length}:{entry.EntityId};"));
        return Convert.ToHexStringLower(digest);
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class AliasDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = "";

        [JsonPropertyName("entityType")]
        public string EntityType { get; init; } = "";

        [JsonPropertyName("normalizedAlias")]
        public string NormalizedAlias { get; init; } = "";

        [JsonPropertyName("entityId")]
        public string EntityId { get; init; } = "";
    }
}
