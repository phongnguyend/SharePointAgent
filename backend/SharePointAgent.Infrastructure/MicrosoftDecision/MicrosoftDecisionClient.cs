using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SharePointAgent.Application;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

/// <summary>Server-side client for Microsoft Foundry's structured decision API.</summary>
public sealed class MicrosoftDecisionClient(HttpClient httpClient, IOptions<MicrosoftDecisionOptions> options,
    TokenCredential? credential = null, ILogger<MicrosoftDecisionClient>? logger = null,
    IDbContextFactory<SharePointIndexDbContext>? contextFactory = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        RespectRequiredConstructorParameters = true
    };

    private readonly TokenCredential tokenCredential = credential ?? new DefaultAzureCredential();

    public Task<MicrosoftDecisionResult> EvaluateAsync(object state,
        IReadOnlyDictionary<string, MicrosoftDecisionQuestion> questions, CancellationToken cancellationToken = default)
        => EvaluateAsync(state, questions, new SystemOneUsageContext(), cancellationToken);

    public async Task<MicrosoftDecisionResult> EvaluateAsync(object state,
        IReadOnlyDictionary<string, MicrosoftDecisionQuestion> questions, SystemOneUsageContext usageContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageContext);
        ValidateDescription(state, nameof(state));
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
        {
            throw new ArgumentException("Provide at least one question.", nameof(questions));
        }
        foreach (var (id, question) in questions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(question);
            ValidateDescription(question.Instructions, nameof(questions));
            switch (question)
            {
                case MicrosoftDecisionChoiceQuestion choice:
                    if (choice.Criteria is null || choice.Criteria.Count < 1)
                    {
                        throw new ArgumentException("Choice requires at least one option.", nameof(questions));
                    }
                    foreach (var (key, value) in choice.Criteria)
                    {
                        ArgumentException.ThrowIfNullOrWhiteSpace(key);
                        if (value is not null)
                        {
                            ValidateDescription(value, nameof(questions));
                        }
                    }
                    break;
                case MicrosoftDecisionScoreQuestion score:
                    if (score.Criteria is null || score.Criteria.Count < 2)
                    {
                        throw new ArgumentException("Score requires at least two levels.", nameof(questions));
                    }
                    foreach (var level in score.Criteria)
                    {
                        ValidateDescription(level, nameof(questions));
                    }
                    break;
                case MicrosoftDecisionNoulQuestion noul:
                    if (noul.Criteria is not null)
                    {
                        foreach (var (key, value) in noul.Criteria)
                        {
                            if (key is not ("true" or "false"))
                            {
                                throw new ArgumentException("Noul criteria keys must be true or false.", nameof(questions));
                            }
                            ValidateDescription(value, nameof(questions));
                        }
                    }
                    break;
                default:
                    throw new ArgumentException("Unsupported MicrosoftDecision question type.", nameof(questions));
            }
        }
        using var request = await CreateRequestAsync(cancellationToken);
        request.Content = JsonContent.Create(new { state, model = options.Value.Model, questions }, options: Json);
        // Foundry requires Content-Length and rejects streamed/chunked JSON requests.
        await request.Content.LoadIntoBufferAsync(cancellationToken);
        var result = await SendAsync<MicrosoftDecisionResult>(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Model) || result.Usage is null || result.Answers is null ||
            result.Answers.Count != questions.Count || questions.Any(q => !result.Answers.TryGetValue(q.Key, out var answer) ||
                (q.Value, answer) is not ((MicrosoftDecisionChoiceQuestion, MicrosoftDecisionChoiceAnswer) or (MicrosoftDecisionScoreQuestion, MicrosoftDecisionScoreAnswer) or (MicrosoftDecisionNoulQuestion, MicrosoftDecisionNoulAnswer))))
        {
            throw new JsonException("MicrosoftDecision returned missing or mismatched answers.");
        }
        if (result.Usage.InputTokens < 0 || result.Usage.OutputTokens < 0)
        {
            throw new JsonException("MicrosoftDecision returned negative token usage.");
        }
        await SystemOneUsageRecorder.RecordAsync(contextFactory, "MicrosoftDecision", result.Model, questions.Count, result.Usage, usageContext);
        return result;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.AbsolutePath == "/")
        {
            throw new InvalidOperationException("MicrosoftDecision:Endpoint must be the full HTTPS decision API URL, without credentials or fragment.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Model);
        AuthenticationHeaderValue? authorization = null;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            var token = await tokenCredential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), cancellationToken);
            authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (authorization is not null)
        {
            request.Headers.Authorization = authorization;
        }
        else
        {
            request.Headers.Add("api-key", config.ApiKey);
        }
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (logger is not null && logger.IsEnabled(LogLevel.Warning))
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                const int maxLength = 8192;
                logger.LogWarning("Microsoft Decision returned HTTP {StatusCode} for deployment {Model}. Foundry response: {ResponseBody}",
                    (int)response.StatusCode, options.Value.Model,
                    body.Length > maxLength ? body[..maxLength] + " [truncated]" : body);
            }
            // Keep provider response details in server logs, not browser-facing exceptions.
            throw new HttpRequestException($"MicrosoftDecision returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new JsonException("MicrosoftDecision returned an empty response.");
    }

    private static void ValidateDescription(object value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        var kind = JsonSerializer.SerializeToElement(value, Json).ValueKind;
        if (kind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new ArgumentException("Expected text, a JSON object, or an array.", parameter);
        }
    }
}
