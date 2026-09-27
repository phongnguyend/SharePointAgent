using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Api;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class EmbeddingUsageReportTests
{
    [Theory]
    [InlineData(AppRoles.GlobalAdmin, true)]
    [InlineData(AppRoles.GlobalReaderAdmin, true)]
    [InlineData(AppRoles.User, false)]
    public void ReportRequiresAnAdministrationRole(string role, bool allowed)
    {
        Assert.Equal(allowed, AppAccess.Allows([role], "GET", "/api/usage/embeddings"));
        Assert.Equal(allowed, AppAccess.Allows([role], "GET", "/api/usage/tokens"));
    }

    [Theory]
    [InlineData(-1, 25, 10)]
    [InlineData(0, 101, 10)]
    [InlineData(0, 0, 10)]
    [InlineData(0, 25, -1)]
    [InlineData(0, 25, 366)]
    public async Task InvalidFiltersAreRejectedBeforeQueryingTheDatabase(int skip, int top, int days)
    {
        await using var db = new SharePointIndexDbContext(new DbContextOptionsBuilder<SharePointIndexDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var from = new DateOnly(2026, 1, 1);
        var result = await EmbeddingUsageEndpoints.ReadAsync(db, default, from, from.AddDays(days), skip: skip, top: top);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var tokens = await TokenUsageEndpoints.ReadAsync(db, default, from, from.AddDays(days), skip: skip, top: top);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(tokens).StatusCode);
    }
}
