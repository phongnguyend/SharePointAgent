using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DocumentParsers;

public sealed class ImageDescriptionProcessor(IImageAnalysisService service)
{
    public async Task<IReadOnlyList<DocumentParseWarning>> ProcessAsync(
        IEnumerable<ImageElement> images,
        ImageProcessingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        options ??= new();
        if (options.MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Per-document cache avoids retaining sensitive image output indefinitely.
        var cache = new ConcurrentDictionary<string, Lazy<Task<string>>>();
        var warnings = new ConcurrentQueue<DocumentParseWarning>();
        await Parallel.ForEachAsync(images, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxConcurrency,
            CancellationToken = cancellationToken
        }, async (image, token) =>
        {
            var hash = Convert.ToHexString(SHA256.HashData(image.Data));
            var context = string.Join("\n", new[] { image.AltText, image.Caption }.Where(value => !string.IsNullOrWhiteSpace(value)));
            await RunAsync("Description", context, () => service.DescribeAsync(image.Data, image.ContentType, context, token), value => image.Description = value);
            if (options.ExtractText)
            {
                await RunAsync("OCR", null, () => service.ExtractTextAsync(image.Data, image.ContentType, token), value => image.ExtractedText = value);
            }

            async Task RunAsync(string operation, string? contextualText, Func<Task<string>> action, Action<string> apply)
            {
                token.ThrowIfCancellationRequested();
                var key = JsonSerializer.Serialize(new[] { hash, image.ContentType, operation, options.ConfigurationKey, contextualText });
                try
                {
                    var request = cache.GetOrAdd(key, _ => new Lazy<Task<string>>(action));
                    apply(await request.Value);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    warnings.Enqueue(new(operation + "Failed", $"Image {operation} failed; other extracted content remains available."));
                }
            }
        });
        return warnings.ToArray();
    }
}
