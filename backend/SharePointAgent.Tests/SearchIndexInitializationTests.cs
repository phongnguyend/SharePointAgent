using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Infrastructure;
using Xunit;
using SearchOptions = SharePointAgent.Application.SearchOptions;

namespace SharePointAgent.Tests;

public sealed class SearchIndexInitializationTests
{
    [Fact]
    public async Task ConcurrentCallersShareOneSuccessfulInitialization()
    {
        var client = Substitute.For<SearchIndexClient>();
        var completion = new TaskCompletionSource<Response<SearchIndex>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CreateOrUpdateIndexAsync(Arg.Any<SearchIndex>(), false, false, Arg.Any<CancellationToken>())
            .Returns(completion.Task);
        var store = CreateStore(client);

        var first = store.EnsureIndexAsync(default);
        var second = store.EnsureIndexAsync(default);
        completion.SetResult(Response.FromValue(new SearchIndex("test"), Substitute.For<Response>()));
        await Task.WhenAll(first, second);
        await store.EnsureIndexAsync(default);

        await client.Received(1).CreateOrUpdateIndexAsync(Arg.Any<SearchIndex>(), false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetriesConflictBeforeMarkingIndexReady()
    {
        var client = Substitute.For<SearchIndexClient>();
        client.CreateOrUpdateIndexAsync(Arg.Any<SearchIndex>(), false, false, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Response<SearchIndex>>(new RequestFailedException(409, "Concurrent index update")),
                _ => Task.FromResult(Response.FromValue(new SearchIndex("test"), Substitute.For<Response>())));
        var store = CreateStore(client);

        await store.EnsureIndexAsync(default);
        await store.EnsureIndexAsync(default);

        await client.Received(2).CreateOrUpdateIndexAsync(Arg.Any<SearchIndex>(), false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailureDoesNotMarkIndexReadyOrHoldLock()
    {
        var client = Substitute.For<SearchIndexClient>();
        client.CreateOrUpdateIndexAsync(Arg.Any<SearchIndex>(), false, false, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Response<SearchIndex>>(new RequestFailedException(403, "Forbidden")),
                _ => Task.FromResult(Response.FromValue(new SearchIndex("test"), Substitute.For<Response>())));
        var store = CreateStore(client);

        await Assert.ThrowsAsync<RequestFailedException>(() => store.EnsureIndexAsync(default));
        await store.EnsureIndexAsync(default);

        await client.Received(2).CreateOrUpdateIndexAsync(Arg.Any<SearchIndex>(), false, false, Arg.Any<CancellationToken>());
    }

    private static AzureSearchIndexStore CreateStore(SearchIndexClient client) =>
        new(client, Substitute.For<SearchClient>(), Options.Create(new SearchOptions()));
}
