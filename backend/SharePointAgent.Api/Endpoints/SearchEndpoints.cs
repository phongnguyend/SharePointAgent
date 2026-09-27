using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/search/fulltext", (
            HttpContext context,
            SearchPayload payload,
            ISearchQueryStore store,
            CancellationToken cancellationToken) => SearchAsync(SearchQueryMode.FullText, payload, store, context, cancellationToken));

        app.MapPost("/api/search/vector", (
            HttpContext context,
            SearchPayload payload,
            ISearchQueryStore store,
            CancellationToken cancellationToken) => SearchAsync(SearchQueryMode.Vector, payload, store, context, cancellationToken));

        app.MapPost("/api/search/hybrid", (
            HttpContext context,
            SearchPayload payload,
            ISearchQueryStore store,
            CancellationToken cancellationToken) => SearchAsync(SearchQueryMode.Hybrid, payload, store, context, cancellationToken));
    }

    private static async Task<IResult> SearchAsync(
        SearchQueryMode mode,
        SearchPayload payload,
        ISearchQueryStore store,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.Query))
        {
            return Results.BadRequest(new { error = "A non-empty 'query' is required." });
        }

        if (payload.Top is < 1 or > 100)
        {
            return Results.BadRequest(new { error = "'top' must be between 1 and 100." });
        }

        if (payload.Skip < 0)
        {
            return Results.BadRequest(new { error = "'skip' must not be negative." });
        }

        var request = new SearchQueryRequest(payload.Query,
            !AppAccess.CanReadAdministration(context.AppUser().Roles) ? context.EntraObjectId() : payload.UserId, payload.Top, payload.Skip);
        var results = await store.SearchAsync(mode, request, cancellationToken);
        return Results.Ok(results);
    }
}
