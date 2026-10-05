using System.Text.Json.Serialization;

namespace SharePointAgent.Infrastructure;

public sealed class PageIndexResult
{
    [JsonPropertyName("doc_name")]
    public required string DocumentName { get; init; }

    [JsonPropertyName("structure")]
    public required List<PageIndexNode> Structure { get; init; }

    /// <summary>Subtract from Markdown line numbers to locate original text.</summary>
    [JsonPropertyName("source_line_offset")]
    public int? SourceLineOffset { get; init; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];

    /// <summary>Aggregate model response usage; null for older PageIndex servers.</summary>
    [JsonPropertyName("usage")]
    public PageIndexTokenUsage? Usage { get; init; }
}
