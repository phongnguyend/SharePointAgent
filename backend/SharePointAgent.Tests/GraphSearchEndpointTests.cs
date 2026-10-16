using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class GraphSearchEndpointTests
{
    [Theory]
    [InlineData(GraphRetrievalStatus.Augmented)]
    [InlineData(GraphRetrievalStatus.FellBack)]
    [InlineData(GraphRetrievalStatus.NoSeedEntities)]
    public async Task ReturnsPlanWithVerifiedResultsOrEmptyRetrieval(GraphRetrievalStatus status)
    {
        var search = Substitute.For<ISearchQueryStore>();
        var retrieval = Substitute.For<IGraphRetrievalService>();
        retrieval.ReturnsResults.Returns(true);
        var hit = new SearchQueryHit("chunk", "drive", "item", "Source", null, null, null, null, null, 0, "Evidence", null);
        search.SearchAsync(Arg.Any<SearchQueryMode>(), Arg.Any<SearchQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SearchQueryResults(1, [hit]));
        retrieval.AugmentAsync(Arg.Any<GraphRetrievalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GraphRetrievalResult(status, status == GraphRetrievalStatus.Augmented ? [new GraphRelatedChunk(hit, "depends_on", 1, false)] : []));
        await using var app = await CreateApp(search, retrieval, "reader");
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/graph/search", new { query = "What depends on Portal?", top = 10, userId = "someone-else" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        await search.Received(1).SearchAsync(SearchQueryMode.Hybrid, Arg.Is<SearchQueryRequest>(r => r.UserId == "reader" && r.Top == 10), Arg.Any<CancellationToken>());
        await retrieval.Received(1).AugmentAsync(Arg.Is<GraphRetrievalRequest>(r => r.UserId == "reader" && r.BaselineHits.Count == 0 && r.SeedHits!.Count == 1
            && r.Plan != null && r.Plan.Entities.Contains("Portal") && r.Plan.RelationshipTypes.Contains("DEPENDS_ON")), Arg.Any<CancellationToken>());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status.ToString(), body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Portal", body.RootElement.GetProperty("plan").GetProperty("entities")[0].GetString());
        Assert.Equal("DEPENDS_ON", body.RootElement.GetProperty("plan").GetProperty("relationshipTypes")[0].GetString());
        Assert.Equal(status == GraphRetrievalStatus.Augmented ? 1 : 0, body.RootElement.GetProperty("chunks").GetArrayLength());
    }

    [Theory]
    [InlineData(null, true, "Question", 10, HttpStatusCode.Unauthorized)]
    [InlineData("reader", false, "Question", 10, HttpStatusCode.Conflict)]
    [InlineData("reader", true, "", 10, HttpStatusCode.BadRequest)]
    [InlineData("reader", true, "Question", 21, HttpStatusCode.BadRequest)]
    public async Task RejectsUnavailableOrInvalidRequestsBeforeSearching(string? user, bool enabled, string query, int top, HttpStatusCode expected)
    {
        var search = Substitute.For<ISearchQueryStore>();
        var retrieval = Substitute.For<IGraphRetrievalService>();
        retrieval.ReturnsResults.Returns(enabled);
        await using var app = await CreateApp(search, retrieval, user);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/graph/search", new { query, top });
        Assert.Equal(expected, response.StatusCode);
        await search.DidNotReceiveWithAnyArgs().SearchAsync(default, default!, default);
    }

    [Theory]
    [InlineData(AppRoles.User)]
    [InlineData(AppRoles.GlobalReaderAdmin)]
    [InlineData(AppRoles.GlobalAdmin)]
    public void RolesCanSearchButNotMutateGraph(string role)
    {
        Assert.True(AppAccess.Allows([role], "POST", "/api/graph/search"));
        if (role != AppRoles.GlobalAdmin)
        {
            Assert.False(AppAccess.Allows([role], "POST", "/api/admin/graph-rag/entities/merge"));
        }
    }

    [Fact]
    public async Task PlannerFailureDoesNotRunSearchOrExposeProviderDetails()
    {
        var search = Substitute.For<ISearchQueryStore>();
        var retrieval = Substitute.For<IGraphRetrievalService>();
        retrieval.ReturnsResults.Returns(true);
        var planner = Substitute.For<IGraphQueryPlanner>();
        planner.PlanAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<GraphQueryPlan>(_ => throw new InvalidOperationException("private provider details"));
        await using var app = await CreateApp(search, retrieval, "reader", planner);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/graph/search", new { query = "CRM follows what?" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("private provider details", await response.Content.ReadAsStringAsync());
        await search.DidNotReceiveWithAnyArgs().SearchAsync(default, default!, default);
        await retrieval.DidNotReceiveWithAnyArgs().AugmentAsync(default!, default);
    }

    [Theory]
    [InlineData(AppRoles.GlobalAdmin, "What follows Portal?", 200)]
    [InlineData(AppRoles.GlobalAdmin, " ", 400)]
    [InlineData(AppRoles.User, "What follows Portal?", 403)]
    [InlineData(AppRoles.GlobalReaderAdmin, "What follows Portal?", 403)]
    public async Task PlaygroundOnlyPlansAndChecksAccess(string role, string question, int expectedStatus)
    {
        var search = Substitute.For<ISearchQueryStore>();
        var retrieval = Substitute.For<IGraphRetrievalService>();
        var planner = Substitute.For<IGraphQueryPlanner>();
        planner.PlanAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GraphQueryPlan { Entities = ["Portal"] });
        await using var app = await CreateApp(search, retrieval, "reader", planner, role);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/admin/graph-query-playground", new { query = question });

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        await planner.Received(expectedStatus == 200 ? 1 : 0).PlanAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await search.DidNotReceiveWithAnyArgs().SearchAsync(default, default!, default);
        await retrieval.DidNotReceiveWithAnyArgs().AugmentAsync(default!, default);
        if (expectedStatus == 200)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Portal", body.RootElement.GetProperty("plan").GetProperty("entities")[0].GetString());
        }
    }

    [Fact]
    public async Task ExtractionPlaygroundUsesIndexingExtractorWithoutSearch()
    {
        var search = Substitute.For<ISearchQueryStore>();
        var retrieval = Substitute.For<IGraphRetrievalService>();
        var extractor = Substitute.For<IGraphExtractor>();
        extractor.ExtractAsync(Arg.Any<GraphExtractionInput>(), Arg.Any<CancellationToken>())
            .Returns(new GraphExtractionResponse());
        await using var app = await CreateApp(search, retrieval, "reader", role: AppRoles.GlobalAdmin, extractor: extractor);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/admin/graph-extraction-playground", new { text = "CRM depends on ERP." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await extractor.Received(1).ExtractAsync(Arg.Is<GraphExtractionInput>(input => input.ChunkText == "CRM depends on ERP."), Arg.Any<CancellationToken>());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("chunks")[0].GetProperty("grounded").GetProperty("rejectedRelations").GetInt32());
        await search.DidNotReceiveWithAnyArgs().SearchAsync(default, default!, default);
        await retrieval.DidNotReceiveWithAnyArgs().AugmentAsync(default!, default);
    }

    private static async Task<WebApplication> CreateApp(ISearchQueryStore search, IGraphRetrievalService retrieval, string? user, IGraphQueryPlanner? queryPlanner = null, string role = AppRoles.User, IGraphExtractor? extractor = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(search);
        builder.Services.AddSingleton(retrieval);
        if (extractor is not null)
        {
            builder.Services.AddSingleton(extractor);
            builder.Services.Configure<KnowledgeGraphOptions>(options => options.Extraction.Deployment = "test");
            builder.Services.AddOptions<ProcessorOptions>();
            builder.Services.AddSingleton(GraphFixtures.Ontology);
        }
        var planner = Substitute.For<IGraphQueryPlanner>();
        planner.PlanAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GraphQueryPlan { Entities = ["Portal"], RelationshipTypes = ["DEPENDS_ON"] });
        builder.Services.AddSingleton(queryPlanner ?? planner);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "test@example.test", "Test",
                [role], true, true, DateTimeOffset.UtcNow, null, "");
            if (user is not null)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", user)], "test"));
            }
            return next(context);
        });
        app.MapGraphSearchEndpoints();

        await app.StartAsync();
        return app;
    }
}
