using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

public sealed record SystemOneUsage(
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens);

