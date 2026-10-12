using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;
using System.Diagnostics;

namespace SharePointAgent.Infrastructure;

/// <summary>Server-side client for TypeSafe's Jev structured decision API.</summary>
public sealed class JevClient(HttpClient httpClient, IOptions<JevOptions> options,
    IDbContextFactory<SharePointIndexDbContext>? contextFactory = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        RespectRequiredConstructorParameters = true
    };

    public Task<JevResult> EvaluateAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions,
        CancellationToken cancellationToken = default) => EvaluateAsync(state, questions, new JevUsageContext(), cancellationToken);

    public async Task<JevResult> EvaluateAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions,
        JevUsageContext usageContext, CancellationToken cancellationToken = default)
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
                case JevChoiceQuestion choice:
                    if (choice.Criteria is null || choice.Criteria.Count is < 1 or > 255)
                    {
                        throw new ArgumentException("Choice requires 1 to 255 options.", nameof(questions));
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
                case JevScoreQuestion score:
                    if (score.Criteria is null || score.Criteria.Count is < 2 or > 10)
                    {
                        throw new ArgumentException("Score requires 2 to 10 levels.", nameof(questions));
                    }
                    foreach (var level in score.Criteria)
                    {
                        ValidateDescription(level, nameof(questions));
                    }
                    break;
                case JevNoulQuestion noul:
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
                    throw new ArgumentException("Unsupported Jev question type.", nameof(questions));
            }
        }
        using var request = CreateRequest(HttpMethod.Post);
        request.Content = JsonContent.Create(new { state, model = options.Value.Model, questions }, options: Json);
        var result = await SendAsync<JevResult>(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Model) || result.Usage is null || result.Answers is null ||
            result.Answers.Count != questions.Count || questions.Any(q => !result.Answers.TryGetValue(q.Key, out var answer) ||
                (q.Value, answer) is not ((JevChoiceQuestion, JevChoiceAnswer) or (JevScoreQuestion, JevScoreAnswer) or (JevNoulQuestion, JevNoulAnswer))))
        {
            throw new JsonException("Jev returned missing or mismatched answers.");
        }
        if (result.Usage.InputTokens < 0 || result.Usage.OutputTokens < 0)
        {
            throw new JsonException("Jev returned negative token usage.");
        }
        if (contextFactory is not null)
        {
            var now = DateTimeOffset.UtcNow;
            // The evaluation has completed: persist billed usage even if the caller disconnected.
            await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            db.JevTokenUsage.Add(new JevTokenUsageEntity
            {
                CreatedAtUtc = now,
                Day = now.Year * 10000 + now.Month * 100 + now.Day,
                Month = now.Year * 100 + now.Month,
                UserId = usageContext.UserId,
                ConversationId = usageContext.ConversationId,
                QuestionId = usageContext.QuestionId,
                ModelId = result.Model,
                TraceId = Activity.Current?.TraceId.ToString(),
                QuestionCount = questions.Count,
                InputTokens = result.Usage.InputTokens,
                OutputTokens = result.Usage.OutputTokens,
                TotalTokens = checked(result.Usage.InputTokens + result.Usage.OutputTokens)
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return result;
    }

    public async Task<JevModelsResult> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "models");
        var result = await SendAsync<JevModelsResult>(request, cancellationToken);
        return result.Models is not null ? result : throw new JsonException("Jev returned an invalid model list.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string? relativePath = null)
    {
        var config = options.Value;
        if (!config.IsConfigured)
        {
            throw new InvalidOperationException("Set Jev:ApiKey before calling Jev.");
        }
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.AbsolutePath == "/")
        {
            throw new InvalidOperationException("Jev:Endpoint must be the full HTTPS evaluation URL, without credentials or fragment.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Model);
        var request = new HttpRequestMessage(method, relativePath is null ? endpoint : new Uri(endpoint, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Upstream bodies can contain the submitted state; do not expose or log them.
            throw new HttpRequestException($"Jev returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new JsonException("Jev returned an empty response.");
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
