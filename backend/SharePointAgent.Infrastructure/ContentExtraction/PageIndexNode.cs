using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

public sealed class PageIndexNode
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("node_id")]
    public string? NodeId { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("prefix_summary")]
    public string? PrefixSummary { get; init; }

    [JsonPropertyName("line_num")]
    public int? LineNumber { get; init; }

    /// <summary>Inclusive, one-based PDF page number.</summary>
    [JsonPropertyName("start_index")]
    public int? StartPage { get; init; }

    /// <summary>Inclusive, one-based PDF page number.</summary>
    [JsonPropertyName("end_index")]
    public int? EndPage { get; init; }

    [JsonPropertyName("nodes")]
    public List<PageIndexNode> Nodes { get; init; } = [];
}
