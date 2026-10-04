namespace DocumentParsers;

public interface IImageAnalysisService
{
    /// <summary>Describe visual meaning using a vision-capable LLM, not OCR alone.</summary>
    Task<string> DescribeAsync(ReadOnlyMemory<byte> image, string contentType, string? contextualText = null, CancellationToken cancellationToken = default);

    /// <summary>Transcribe visible text using OCR; return an empty string when no text is found.</summary>
    Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, CancellationToken cancellationToken = default);
}
