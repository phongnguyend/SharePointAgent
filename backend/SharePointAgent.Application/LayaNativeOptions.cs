using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

public sealed class LayaNativeOptions
{
    public const string SectionName = "LayaNative";

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "multilingual";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 300;
}
