using System.ComponentModel.DataAnnotations;

namespace SharePointAgent.Application;

public sealed class ContentSafetyOptions
{
    public const string SectionName = "ContentSafety";
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool UseManagedIdentity { get; set; } = true;
    public string? ManagedIdentityClientId { get; set; }
    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 15;
    [Range(1, 7)] public int HateThreshold { get; set; } = 4;
    [Range(1, 7)] public int SexualThreshold { get; set; } = 4;
    [Range(1, 7)] public int ViolenceThreshold { get; set; } = 4;
    [Range(1, 7)] public int SelfHarmThreshold { get; set; } = 4;
}
