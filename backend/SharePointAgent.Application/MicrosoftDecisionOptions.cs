using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

public sealed class MicrosoftDecisionOptions
{
    public const string SectionName = "MicrosoftDecision";

    public string Endpoint { get; set; } = "";

    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "Microsoft-Decision-1";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}
