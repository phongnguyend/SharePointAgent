namespace DocumentParsers;

public sealed record ImageProcessingOptions
{
    public bool ExtractText { get; init; }

    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Include provider, model, prompt version and preprocessing configuration.</summary>
    public string ConfigurationKey { get; init; } = "default";
}
