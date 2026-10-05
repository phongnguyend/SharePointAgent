using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

public sealed class PageIndexOptions
{
    public const string SectionName = "PageIndex";

    [Required, Url]
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    [Required]
    public string IndexPath { get; set; } = "/index";

    [Required]
    public string HealthPath { get; set; } = "/health";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 360;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}
