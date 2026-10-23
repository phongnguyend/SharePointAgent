using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

public sealed class LayaNativeClient(HttpClient client, IOptions<LayaNativeOptions> options)
{
    public async Task<JsonElement> EvaluateAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException($"Configure LayaNative:Endpoint and ApiKey.");
        }
        var state = input.GetProperty("state");
        var questions = input.GetProperty("questions");
        var count = questions.EnumerateObject().Count();
        if (state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            (state.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(state.GetString())) ||
            count < 1 || count > 64)
        {
            throw new ArgumentException("Provide a nonempty state and questions within the provider limit.");
        }
        foreach (var question in questions.EnumerateObject())
        {
            var value = question.Value;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("choice" or "score" or "noul") ||
                !value.TryGetProperty("instructions", out var instructions) ||
                instructions.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(instructions.GetString()))
            {
                throw new ArgumentException("Each question needs a supported type and instructions.");
            }
            if (type.GetString() is "choice" or "score")
            {
                if (!value.TryGetProperty("criteria", out var criteria) ||
                    (type.GetString() == "choice" && (criteria.ValueKind != JsonValueKind.Object || criteria.EnumerateObject().Count() < 2)) ||
                    (type.GetString() == "score" && (criteria.ValueKind != JsonValueKind.Array || criteria.GetArrayLength() < 2)))
                {
                    throw new ArgumentException("Choice and score questions need at least two criteria.");
                }
            }
        }
        // Preserve LayaNative request controls when adding the configured default model.
        var body = JsonNode.Parse(input.GetRawText())!.AsObject();
        if (!input.TryGetProperty("model", out var model))
        {
            body["model"] = settings.Model;
        }
        else if (model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()))
        {
            throw new ArgumentException("Model must be a nonempty string.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint.TrimEnd('/') + "/v1/systemone")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var response = await client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Object || !answers.EnumerateObject().Any())
        {
            throw new JsonException("LayaNative returned no answers.");
        }
        return result;
    }
}
