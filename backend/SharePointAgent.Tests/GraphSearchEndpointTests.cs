using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
    [Fact]
    public async Task UsesAuthenticatedIdentityAndVerifiedRetrievalWithoutShadowResults()
    {
        var search = Substitute.For<ISearchQueryStore>();
        var retrieval = Substitute.For<IGraphRetrievalService>();
        retrieval.ReturnsResults.Returns(true);
        var hit = new SearchQueryHit("chunk", "drive", "item", "Source", null, null, null, null, null, 0, "Evidence", null);
        search.SearchAsync(Arg.Any<SearchQueryMode>(), Arg.Any<SearchQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SearchQueryResults(1, [hit]));
        retrieval.AugmentAsync(Arg.Any<GraphRetrievalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GraphRetrievalResult(GraphRetrievalStatus.Augmented, [new GraphRelatedChunk(hit, "depends_on", 1, false)]));
        await using var app = await CreateApp(search, retrieval, "reader");
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/graph/search", new { query = "What depends on Portal?", top = 10, userId = "someone-else" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        await search.Received(1).SearchAsync(SearchQueryMode.Hybrid, Arg.Is<SearchQueryRequest>(r => r.UserId == "reader" && r.Top == 10), Arg.Any<CancellationToken>());
        await retrieval.Received(1).AugmentAsync(Arg.Is<GraphRetrievalRequest>(r => r.UserId == "reader" && !r.IncludeShadowResults && r.BaselineHits.Count == 0 && r.SeedHits!.Count == 1), Arg.Any<CancellationToken>());
        Assert.Contains("Evidence", await response.Content.ReadAsStringAsync());
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

    private static async Task<WebApplication> CreateApp(ISearchQueryStore search, IGraphRetrievalService retrieval, string? user)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(search);
        builder.Services.AddSingleton(retrieval);
        var app = builder.Build();
        app.Use((context, next) =>
        {
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
