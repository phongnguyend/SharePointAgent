using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

public sealed class PageIndexTokenUsage
{
    /// <summary>Configured server model, including the provider/deployment prefix; null for older servers.</summary>
    [JsonPropertyName("model_id")]
    public string? ModelId { get; init; }

    /// <summary>Total input tokens across completed model calls for this indexing request.</summary>
    [JsonPropertyName("input_tokens")]
    public long InputTokens => PromptTokens;

    /// <summary>Total output tokens across completed model calls for this indexing request.</summary>
    [JsonPropertyName("output_tokens")]
    public long OutputTokens => CompletionTokens;

    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("model_calls")]
    public int ModelCalls { get; init; }

    [JsonPropertyName("calls_without_usage")]
    public int CallsWithoutUsage { get; init; }
}
