using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public sealed partial class SharePointClient
{
    // Ordinary SharePoint sites are supported only by Graph beta, not the v1.0 Embedded container API.
    public async Task<SharePointRecycleBinListing> BrowseRecycleBinAsync(CancellationToken ct)
    {
        var site = await GetSiteAsync(ct);
        if (string.IsNullOrWhiteSpace(site.Id))
        {
            throw new InvalidDataException("Microsoft Graph returned a site without an ID.");
        }
        var endpoint = new Uri($"https://graph.microsoft.com/beta/sites/{Uri.EscapeDataString(site.Id)}/recycleBin/items");
        string? next = endpoint.AbsoluteUri + "?$top=200";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var items = new Dictionary<string, SharePointRecycleBinItem>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ParsableFactory<IParsable>>
        {
            ["4XX"] = ODataError.CreateFromDiscriminatorValue,
            ["5XX"] = ODataError.CreateFromDiscriminatorValue
        };
        while (!string.IsNullOrEmpty(next))
        {
            // Only follow Graph's continuation for this site's collection; never forward credentials elsewhere.
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri)
                || uri.Scheme != endpoint.Scheme || uri.Authority != endpoint.Authority
                || Uri.UnescapeDataString(uri.AbsolutePath) != Uri.UnescapeDataString(endpoint.AbsolutePath)
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
                || !visited.Add(next))
            {
                throw new InvalidDataException("Microsoft Graph returned an invalid recycle bin continuation.");
            }
            var request = new RequestInformation { HttpMethod = Method.GET, URI = uri };
            request.Headers.Add("Accept", "application/json");
            await using var stream = await graph.RequestAdapter.SendPrimitiveAsync<Stream>(request, errors, ct)
                ?? throw new InvalidDataException("Microsoft Graph returned an empty recycle bin response.");
            RecycleBinPage? page;
            try
            {
                page = await JsonSerializer.DeserializeAsync<RecycleBinPage>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Microsoft Graph returned invalid recycle bin data.", ex);
            }
            if (page?.Value is null || page.Value.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Name is null))
            {
                throw new InvalidDataException("Microsoft Graph returned incomplete recycle bin data.");
            }
            foreach (var item in page.Value)
            {
                items[item.Id] = item;
            }
            next = page.NextLink;
        }
        var recycleBinUrl = Uri.TryCreate(site.WebUrl, UriKind.Absolute, out var siteUrl) && siteUrl.Scheme == "https"
            ? siteUrl.AbsoluteUri.TrimEnd('/') + "/_layouts/15/RecycleBin.aspx" : null;
        return new(site.DisplayName ?? site.Name ?? "SharePoint site", recycleBinUrl,
            items.Values.OrderByDescending(item => item.DeletedDateTime).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed record RecycleBinPage(
        IReadOnlyList<SharePointRecycleBinItem>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink);
}
