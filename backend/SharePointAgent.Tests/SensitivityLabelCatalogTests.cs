using System.Net;
using System.Text;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SensitivityLabelCatalogTests
{
    [Fact]
    public async Task ReadsPagesAndNestedDisplayNamesWithoutCaching()
    {
        var handler = new CatalogHandler();
        using var http = new HttpClient(handler);
        using var graph = new GraphServiceClient(http);
        var catalog = new SensitivityLabelCatalog(graph);
        var names = await catalog.ReadAsync(default);
        Assert.Equal("Confidential · Employees", names["child"]);
        Assert.Equal("Public", names["public"]);
        handler.ParentName = "Renamed";
        names = await catalog.ReadAsync(default);
        Assert.Equal("Renamed · Employees", names["child"]);
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task PermissionDenialIsNotReturnedAsAnEmptyCatalog()
    {
        using var http = new HttpClient(new CatalogHandler { Denied = true });
        using var graph = new GraphServiceClient(http);
        var error = await Assert.ThrowsAnyAsync<ApiException>(() => new SensitivityLabelCatalog(graph).ReadAsync(default));
        Assert.Equal(403, error.ResponseStatusCode);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string ParentName { get; set; } = "Confidential";
        public bool Denied { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var json = Denied ? """{"error":{"code":"Authorization_RequestDenied","message":"Denied"}}"""
                : request.RequestUri!.Query.Length > 0 ? """{"value":[{"id":"public","name":"Public"}]}"""
                : $$"""{"@odata.nextLink":"https://graph.microsoft.com/v1.0/security/dataSecurityAndGovernance/sensitivityLabels?$skiptoken=next","value":[{"id":"parent","name":"internal-name","displayName":"{{ParentName}}","sublabels":[{"id":"CHILD","name":"internal-child","displayName":"Employees"}]}]}""";
            return Task.FromResult(new HttpResponseMessage(Denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
