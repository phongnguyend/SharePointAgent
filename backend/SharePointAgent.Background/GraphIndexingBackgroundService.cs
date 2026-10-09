using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure.GraphRag;

namespace SharePointAgent.Background;

/// <summary>
/// Receives graph indexing requests from their Service Bus queue, with the concurrency
/// <see cref="GraphExtractionOptions.MaxConcurrentDocuments"/> allows. How each message is settled is decided
/// by <see cref="GraphIndexingMessageHandler"/>.
/// </summary>
public sealed class GraphIndexingBackgroundService(
    ServiceBusClient serviceBus,
    GraphIndexingMessageHandler handler,
    IOptions<GraphRagOptions> options,
    ILogger<GraphIndexingBackgroundService> logger) : BackgroundService
{
    public const string AttemptProperty = "graphAttempt";

    private ServiceBusProcessor? _processor;
    private ServiceBusSender? _sender;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var sender = serviceBus.CreateSender(settings.IndexingQueueName);
        _sender = sender;
        _processor = serviceBus.CreateProcessor(settings.IndexingQueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = settings.Extraction.MaxConcurrentDocuments,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(30),
            PrefetchCount = 0
        });
        _processor.ProcessMessageAsync += args => handler.HandleAsync(new ServiceBusMessageContext(args, sender), stoppingToken);
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Graph indexing queue error. Entity: {EntityPath}; source: {ErrorSource}.", args.EntityPath, args.ErrorSource);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }

        if (_sender is not null)
        {
            await _sender.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }

    private sealed class ServiceBusMessageContext(ProcessMessageEventArgs args, ServiceBusSender sender) : IGraphMessageContext
    {
        public BinaryData Body => args.Message.Body;

        public int Attempt => args.Message.ApplicationProperties.TryGetValue(AttemptProperty, out var value) && value is int attempt ? attempt : 1;

        public CancellationToken CancellationToken => args.CancellationToken;

        public Task CompleteAsync() => args.CompleteMessageAsync(args.Message, args.CancellationToken);

        public Task DeadLetterAsync(string reason, string description) =>
            args.DeadLetterMessageAsync(args.Message, reason, description, args.CancellationToken);

        public async Task RescheduleAsync(int nextAttempt, DateTimeOffset enqueueAt)
        {
            var copy = new ServiceBusMessage(args.Message)
            {
                ScheduledEnqueueTime = enqueueAt
            };
            copy.ApplicationProperties[AttemptProperty] = nextAttempt;

            // If scheduling fails, the exception abandons the original and Service Bus redelivers it, so the
            // request is never lost; at worst it is retried sooner.
            await sender.SendMessageAsync(copy, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
    }
}
