using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

/// <summary>Server-side client for TypeSafe's Jev structured decision API.</summary>
public sealed class JevClient(HttpClient httpClient, IOptions<JevOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        RespectRequiredConstructorParameters = true
    };

    public async Task<JevResult> EvaluateAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions,
        CancellationToken cancellationToken = default)
    {
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
        using var request = CreateRequest(HttpMethod.Post, "systemone");
        request.Content = JsonContent.Create(new { state, model = options.Value.Model, questions }, options: Json);
        var result = await SendAsync<JevResult>(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Model) || result.Usage is null || result.Answers is null ||
            result.Answers.Count != questions.Count || questions.Any(q => !result.Answers.TryGetValue(q.Key, out var answer) ||
                (q.Value, answer) is not ((JevChoiceQuestion, JevChoiceAnswer) or (JevScoreQuestion, JevScoreAnswer) or (JevNoulQuestion, JevNoulAnswer))))
        {
            throw new JsonException("Jev returned missing or mismatched answers.");
        }
        return result;
    }

    public async Task<JevModelsResult> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "models");
        var result = await SendAsync<JevModelsResult>(request, cancellationToken);
        return result.Models is not null ? result : throw new JsonException("Jev returned an invalid model list.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var config = options.Value;
        if (!config.IsConfigured)
        {
            throw new InvalidOperationException("Set Jev:ApiKey before calling Jev.");
        }
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
        {
            throw new InvalidOperationException("Jev:Endpoint must be an HTTPS base URL without credentials, query, or fragment.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Model);
        var request = new HttpRequestMessage(method, config.Endpoint.TrimEnd('/') + "/" + path);
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
