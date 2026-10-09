using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

public sealed record RecognizedImageText(string FilePath, string Text);

/// <summary>Reads text from sandbox images using Document Intelligence.</summary>
public sealed class ImageTextRecognizer(
    IAgentWorkspace files,
    DocumentIntelligenceClient client,
    IOptions<DocumentIntelligenceOptions> options)
{
    public async Task<RecognizedImageText> RecognizeAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A filePath is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Value.Endpoint))
        {
            throw new InvalidOperationException("Configure DocumentIntelligence:Endpoint to recognize image text.");
        }

        var extension = Path.GetExtension(files.Normalize(filePath)).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff"))
        {
            throw new ArgumentException("OCR supports PNG, JPEG, BMP, and TIFF image files.");
        }

        var file = await files.ReadAsync(filePath, cancellationToken);
        var text = await client.ExtractAsync(file.Content, cancellationToken);
        return new RecognizedImageText(file.Path, text);
    }
}
