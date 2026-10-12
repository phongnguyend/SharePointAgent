using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

public sealed class JevOptions
{
    public const string SectionName = "Jev";

    [Required]
    public string Endpoint { get; set; } = "https://api.typesafe.ai/v1/systemone";

    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "jev-latest";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
