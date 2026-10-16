namespace SharePointAgent.Application;

public sealed class SpeechOptions
{
    public string Endpoint { get; set; } = "";

    public string ResourceId { get; set; } = "";

    public bool UseManagedIdentity { get; set; } = true;

    public string ApiKey { get; set; } = "";
}
