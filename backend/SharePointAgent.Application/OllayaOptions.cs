using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

/// <summary>
/// The Ollaya decision-model server. Blank <see cref="Endpoint"/> means not configured; callers check
/// <see cref="IsConfigured"/> before using it.
/// </summary>
public sealed class OllayaOptions
{
    public const string SectionName = "Ollaya";

    public string? Endpoint { get; set; }

    /// <summary>Sent as a bearer token; the deployed server rejects requests without it.</summary>
    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "winnow:e4b";

    [Required]
    public string DecidePath { get; set; } = "/v1/systemone";

    /// <summary>Ollaya's anonymous liveness route, which answers "Ollaya is running".</summary>
    [Required]
    public string HealthPath { get; set; } = "/";

    /// <summary>Long enough for a GPU replica to start from zero and load the model on the first call.</summary>
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}
