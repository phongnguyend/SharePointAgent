using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using Microsoft.Extensions.Options;
using SharePointAgent.Infrastructure.KnowledgeGraph;

namespace SharePointAgent.Api;

public static class GraphSearchEndpoints
{
    public static void MapGraphSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/graph-extraction-playground", async (HttpContext context, GraphExtractionPayload payload, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.StatusCode(403);
            }
            if (string.IsNullOrWhiteSpace(payload.Text) || payload.Text.Length > 20000)
            {
                return Results.BadRequest(new { error = "Enter document text between 1 and 20,000 characters." });
            }
            var options = context.RequestServices.GetRequiredService<IOptions<KnowledgeGraphOptions>>().Value.Extraction;
            if (string.IsNullOrWhiteSpace(options.Deployment))
            {
                return Results.Conflict(new { error = "Configure KnowledgeGraph:Extraction:Deployment to test indexing extraction." });
            }
            using var usage = new GraphModelUsageScope("graph.extraction_playground");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var extractor = context.RequestServices.GetService<IGraphExtractor>()
                    ?? ActivatorUtilities.CreateInstance<AzureOpenAiGraphExtractor>(context.RequestServices);
                var processor = context.RequestServices.GetRequiredService<IOptions<ProcessorOptions>>().Value;
                var ontology = context.RequestServices.GetRequiredService<GraphOntology>();
                var chunks = TextChunker.Split(payload.Text, processor.ChunkSizeCharacters, processor.ChunkOverlapCharacters)
                    .Take(options.MaxChunksPerDocument).ToList();
                var results = new List<object>();
                for (var index = 0; index < chunks.Count; index++)
                {
                    var before = index > 0 ? chunks[index - 1][^Math.Min(chunks[index - 1].Length, options.NeighborContextCharacters)..] : null;
                    var after = index + 1 < chunks.Count ? chunks[index + 1][..Math.Min(chunks[index + 1].Length, options.NeighborContextCharacters)] : null;
                    var raw = await extractor.ExtractAsync(new GraphExtractionInput("playground.txt", null, chunks[index], before, after), ct);
                    var grounded = GraphExtractionGrounding.Ground(raw, chunks[index], ontology, options.MaxEntitiesPerChunk, options.MaxRelationsPerChunk);
                    results.Add(new { chunkNumber = index, text = chunks[index], raw, grounded });
                }
                return Results.Ok(new { chunks = results, usage = usage.Requests, elapsedMs = stopwatch.ElapsedMilliseconds });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Results.Problem("Entity extraction failed. Check the extraction deployment and token limit or retry.", statusCode: 502);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(128 * 1024));

        app.MapPost("/api/admin/graph-query-playground", async (HttpContext context, GraphSearchPayload payload, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.StatusCode(403);
            }
            if (string.IsNullOrWhiteSpace(payload.Query) || payload.Query.Length > 2000)
            {
                return Results.BadRequest(new { error = "Enter a question between 1 and 2,000 characters." });
            }
            using var usage = new GraphModelUsageScope("graph.query_playground");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var plan = await context.RequestServices.GetRequiredService<IGraphQueryPlanner>().PlanAsync(payload.Query.Trim(), ct);
                return Results.Ok(new { plan, usage = usage.Requests, elapsedMs = stopwatch.ElapsedMilliseconds });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Results.Problem("The question could not be interpreted. Check the chat deployment or retry.", statusCode: 502);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

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
                return Results.Conflict(new { error = "Graph search is unavailable. Check the knowledge graph configuration and tenant access." });
            }
            using var usage = new GraphModelUsageScope("graph.search_planning");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            GraphQueryPlan plan;
            try
            {
                plan = await context.RequestServices.GetRequiredService<IGraphQueryPlanner>().PlanAsync(payload.Query.Trim(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Results.Problem("The question could not be interpreted. Please retry or rephrase it.", statusCode: 502);
            }
            var seeds = await search.SearchAsync(SearchQueryMode.Hybrid, new SearchQueryRequest(payload.Query.Trim(), userId, payload.Top, 0), ct);
            // Seeds identify entities, but remain eligible as relationship evidence in the result.
            var result = await retrieval.AugmentAsync(new GraphRetrievalRequest(userId, payload.Query.Trim(), [], SeedHits: seeds.Items, Plan: plan), ct);
            return Results.Ok(new { status = result.Status.ToString(), chunks = result.Chunks, plan, elapsedMs = stopwatch.ElapsedMilliseconds });
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));
    }

    public sealed record GraphSearchPayload(string? Query, int Top = 10);

    public sealed record GraphExtractionPayload(string? Text);
}
