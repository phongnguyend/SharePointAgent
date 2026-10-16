using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Api;

public static class GraphSearchEndpoints
{
    public static void MapGraphSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/graph/search", async (HttpContext context, GraphSearchPayload payload, ISearchQueryStore search, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // Never accept a caller-supplied identity or fall back to an unfiltered search.
            var userId = context.User.FindFirst("oid")?.Value;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }
            if (string.IsNullOrWhiteSpace(payload.Query) || payload.Query.Length > 2000 || payload.Top is < 1 or > 20)
            {
                return Results.BadRequest(new { error = "Enter 1–2,000 characters and a seed limit between 1 and 20." });
            }
            var retrieval = context.RequestServices.GetService<IGraphRetrievalService>();
            if (retrieval?.ReturnsResults != true)
            {
                return Results.Conflict(new { error = "Graph search is unavailable. Enable KnowledgeGraph:RetrievalEnabled for this tenant; shadow mode does not expose results." });
            }
            var stopwatch = Stopwatch.StartNew();
            var seeds = await search.SearchAsync(SearchQueryMode.Hybrid, new SearchQueryRequest(payload.Query.Trim(), userId, payload.Top, 0), ct);
            // Seeds identify entities, but remain eligible as relationship evidence in the result.
            var result = await retrieval.AugmentAsync(new GraphRetrievalRequest(userId, payload.Query.Trim(), [], SeedHits: seeds.Items), ct);
            return Results.Ok(new { status = result.Status.ToString(), chunks = result.Chunks, elapsedMs = stopwatch.ElapsedMilliseconds });
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));
    }

    public sealed record GraphSearchPayload(string? Query, int Top = 10);
}
