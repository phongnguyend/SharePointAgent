using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>Used whenever graph indexing is off, so the indexer's hook costs nothing.</summary>
public sealed class NullGraphIndexingSignal : IGraphIndexingSignal
{
    public Task PublishAsync(GraphIndexingRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Queues indexing requests on the existing Service Bus namespace. Messages carry identifiers only; the
/// worker reads everything else from the indexed-file table and the search index when it processes them.
/// </summary>
public sealed class ServiceBusGraphIndexingSignal(ServiceBusClient client, IOptions<GraphRagOptions> options) : IGraphIndexingSignal, IAsyncDisposable
{
    public const string Subject = "graph.indexing.requested";

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ServiceBusSender _sender = client.CreateSender(options.Value.IndexingQueueName);

    public async Task PublishAsync(GraphIndexingRequest request, CancellationToken cancellationToken)
    {
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(request, SerializerOptions))
        {
            ContentType = "application/json",
            Subject = Subject
        };
        await _sender.SendMessageAsync(message, cancellationToken);
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}
