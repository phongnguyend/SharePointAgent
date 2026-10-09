using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// One typed question for a decision model. <c>Type</c> is <c>choice</c>, <c>noul</c> (yes, no, or
/// unknown), or <c>score</c>; <c>Criteria</c> carries a choice question's options.
/// </summary>
public sealed record OllayaQuestion(string Type, string Instructions, JsonElement? Criteria = null);

/// <summary>
/// The answers, keyed by question id in question order. Each answer's shape depends on its question
/// type, so it is kept as JSON for the caller to read.
/// </summary>
public sealed record OllayaDecision(string Model, IReadOnlyDictionary<string, JsonElement> Answers, JsonElement? Usage);

/// <summary>Asks the Ollaya server's decision model typed questions about a state.</summary>
public sealed class OllayaClient(
    HttpClient httpClient,
    IOptions<OllayaOptions> options,
    ILogger<OllayaClient>? logger = null)
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(10);

    private readonly OllayaOptions _options = options.Value;

    /// <summary>Calls the TypeSafe-compatible decision route with the configured model unless one is given.</summary>
    public async Task<OllayaDecision> DecideAsync(string state, IReadOnlyDictionary<string, OllayaQuestion> questions,
        CancellationToken cancellationToken, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("A decision needs a state to decide about.", nameof(state));
        }

        if (questions.Count is < 1 or > 256)
        {
            throw new ArgumentException("A decision takes between 1 and 256 questions.", nameof(questions));
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(model) ? _options.Model : model,
            state,
            questions = questions.ToDictionary(question => question.Key, question => new
            {
                type = question.Value.Type,
                instructions = question.Value.Instructions,
                criteria = question.Value.Criteria,
            }),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(_options.DecidePath))
        {
            Content = JsonContent.Create(body, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            logger?.LogWarning("Ollaya decision failed: StatusCode={StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException($"Ollaya returned {(int)response.StatusCode}: {detail}", null, response.StatusCode);
        }

        var decision = await response.Content.ReadFromJsonAsync<OllayaDecision>(cancellationToken);
        if (decision?.Answers is null || decision.Answers.Count == 0)
        {
            throw new JsonException("Ollaya returned no answers.");
        }

        return decision;
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HealthTimeout);
        using var response = await httpClient.GetAsync(BuildUrl(_options.HealthPath), timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new HttpRequestException($"Ollaya returned {(int)response.StatusCode}: {detail}", null, response.StatusCode);
        }
    }

    private string BuildUrl(string path)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("Ollaya:Endpoint is required to call the Ollaya service.");
        }

        return $"{_options.Endpoint!.TrimEnd('/')}/{path.TrimStart('/')}";
    }
}
