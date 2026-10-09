using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure.Monitoring;

namespace SharePointAgent.Infrastructure.GraphRag;

/// <summary>
/// Extracts one chunk with the Azure OpenAI deployment the application already uses, through
/// Microsoft.Extensions.AI and JSON-schema structured output. Throttling and server errors are retried with
/// jittered backoff; output that is not valid JSON is retried and then treated as no extraction, so one
/// unusual chunk cannot block a document.
/// </summary>
public sealed class AzureOpenAiGraphExtractor : IGraphExtractor
{
    private readonly IChatClient _chat;
    private readonly string _instructions;
    private readonly GraphExtractionOptions _options;
    private readonly ILogger<AzureOpenAiGraphExtractor> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AzureOpenAiGraphExtractor(
        AzureOpenAIClient client,
        IOptions<OpenAiOptions> openAiOptions,
        IOptions<GraphRagOptions> options,
        GraphOntology ontology,
        ILogger<AzureOpenAiGraphExtractor> logger)
        : this(
            client.GetChatClient(ResolveModel(openAiOptions.Value, options.Value)).AsIChatClient(),
            ResolveModel(openAiOptions.Value, options.Value),
            options,
            ontology,
            logger,
            Task.Delay)
    {
    }

    internal AzureOpenAiGraphExtractor(
        IChatClient chat,
        string modelId,
        IOptions<GraphRagOptions> options,
        GraphOntology ontology,
        ILogger<AzureOpenAiGraphExtractor> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _chat = chat.AsBuilder().UseOpenTelemetry(sourceName: Telemetry.SourceName).Build();
        ModelId = modelId;
        _options = options.Value.Extraction;
        _instructions = GraphExtractionPrompt.BuildInstructions(ontology);
        _logger = logger;
        _delay = delay;
    }

    public string ModelId { get; }

    public string PromptVersion => GraphExtractionPrompt.Version;

    public async Task<GraphExtractionResponse> ExtractAsync(GraphExtractionInput input, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, _instructions),
            new(ChatRole.User, GraphExtractionPrompt.BuildUserMessage(input))
        ];
        var chatOptions = new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _chat.GetResponseAsync<GraphExtractionResponse>(
                    messages, chatOptions, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken);
                GraphRagMetrics.Tokens(response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0);
                if (response.TryGetResult(out var result) && result is not null)
                {
                    return result;
                }

                if (attempt > _options.MaxRetries)
                {
                    GraphRagMetrics.Rejected("malformedOutput");
                    _logger.LogWarning("The extraction model returned output that is not the expected JSON; the chunk was skipped.");
                    return new GraphExtractionResponse();
                }
            }
            catch (ClientResultException exception) when (IsTransient(exception.Status))
            {
                if (exception.Status == 429)
                {
                    GraphRagMetrics.Throttle("extraction");
                }

                if (attempt > _options.MaxRetries)
                {
                    throw new GraphTransientFailureException("extraction.unavailable", $"The extraction model kept failing with HTTP {exception.Status}.", exception);
                }
            }
            catch (ClientResultException exception) when (exception.Status == 400)
            {
                // Typically the content filter. Skipping this chunk is better than dead-lettering the document.
                GraphRagMetrics.Rejected("requestRejected");
                _logger.LogWarning("The extraction model rejected a chunk request with HTTP 400; the chunk was skipped.");
                return new GraphExtractionResponse();
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException
                || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt > _options.MaxRetries)
                {
                    throw new GraphTransientFailureException("extraction.unavailable", "The extraction model could not be reached.", exception);
                }
            }

            await _delay(GraphRetryPolicy.Delay(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), cancellationToken);
        }
    }

    private static bool IsTransient(int status) => status is 408 or 429 or >= 500;

    private static string ResolveModel(OpenAiOptions openAi, GraphRagOptions options) =>
        string.IsNullOrWhiteSpace(options.Extraction.Deployment) ? openAi.ChatDeployment : options.Extraction.Deployment;
}
