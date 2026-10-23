using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

public sealed class WinnowNativeOptions
{
    public const string SectionName = "WinnowNative";

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "Winnow-E4B";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 300;
}
