using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>What settling one queued message needs, so the rules do not depend on Service Bus types.</summary>
public interface IGraphMessageContext
{
    BinaryData Body { get; }

    /// <summary>1 for the first delivery; each scheduled retry carries the next number.</summary>
    int Attempt { get; }

    CancellationToken CancellationToken { get; }

    Task CompleteAsync();

    Task DeadLetterAsync(string reason, string description);

    /// <summary>Sends a copy for a later attempt, then completes this message.</summary>
    Task RescheduleAsync(int nextAttempt, DateTimeOffset enqueueAt);
}

/// <summary>
/// How a graph indexing message is settled. Delivery is at least once and the pipeline is idempotent, so a
/// redelivered or duplicated request is harmless.
/// <para>
/// A transient failure, such as throttling, an index rewrite in progress, or a concurrent writer, is retried
/// by scheduling a copy of the message with exponential backoff and jitter, up to
/// <see cref="GraphExtractionOptions.MaxDeliveryAttempts"/> attempts. A permanent failure, such as an
/// invalid snapshot or a malformed message, is dead-lettered, and so is a request that runs out of retries.
/// Dead-letter reasons are codes, never content.
/// </para>
/// </summary>
public sealed class GraphIndexingMessageHandler(
    IGraphIndexingPipeline pipeline,
    IOptions<GraphRagOptions> options,
    TimeProvider time,
    ILogger<GraphIndexingMessageHandler> logger)
{
    public async Task HandleAsync(IGraphMessageContext message, CancellationToken stoppingToken)
    {
        GraphIndexingRequest? request;
        try
        {
            request = message.Body.ToObjectFromJson<GraphIndexingRequest>(ServiceBusGraphIndexingSignal.SerializerOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.DriveId) || string.IsNullOrWhiteSpace(request.ItemId) || !Enum.IsDefined(request.Kind))
        {
            GraphRagMetrics.IndexingFailed("message.invalid", retried: false);
            await message.DeadLetterAsync("InvalidMessage", "The message is not a graph indexing request.");
            return;
        }

        var attempt = message.Attempt;
        try
        {
            await pipeline.ProcessAsync(request, message.CancellationToken);
            await message.CompleteAsync();
        }
        catch (GraphPermanentFailureException exception)
        {
            GraphRagMetrics.IndexingFailed(exception.Code, retried: false);
            logger.LogWarning("Dead-lettering the graph indexing request for item {ItemId}: {FailureCode}.", request.ItemId, exception.Code);
            await message.DeadLetterAsync(exception.Code, "Graph indexing failed permanently; see the failure code.");
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            var code = exception is GraphTransientFailureException transient ? transient.Code : exception.GetType().Name;
            if (attempt >= options.Value.Extraction.MaxDeliveryAttempts)
            {
                GraphRagMetrics.IndexingFailed(code, retried: false);
                logger.LogWarning("Dead-lettering the graph indexing request for item {ItemId} after {Attempts} attempts: {FailureCode}.", request.ItemId, attempt, code);
                await message.DeadLetterAsync("RetriesExhausted", $"Last failure: {code}.");
                return;
            }

            var delay = GraphRetryPolicy.Delay(attempt, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30));
            GraphRagMetrics.IndexingFailed(code, retried: true);
            logger.LogInformation("Retrying the graph indexing request for item {ItemId} in {Delay}: {FailureCode}.", request.ItemId, delay, code);
            await message.RescheduleAsync(attempt + 1, time.GetUtcNow() + delay);
        }
    }
}
