using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DocumentParsers;

public sealed class PdfDocumentParser : IPdfDocumentParser
{
    private readonly DocumentIntelligenceClient? _ocrClient;
    private readonly PdfParserOptions _options;

    // Supplying a client enables OCR fallback for pages without native text.
    public PdfDocumentParser(DocumentIntelligenceClient? client = null, PdfParserOptions? options = null)
    {
        _ocrClient = client;
        _options = options ?? new();
        _options.Validate();
    }

    public async Task<PdfParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = await ParserInput.ReadAsync(stream, _options.MaxInputBytes, false, cancellationToken);
        if (buffer.Length < 5 || !buffer.GetBuffer().AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException("Input is not a PDF document.");
        }
        using var document = PdfDocument.Open(buffer.ToArray());
        var result = new PdfParseResult { PageCount = document.NumberOfPages };
        result.Metadata["Parser"] = "PdfPig";
        var images = new ImageReader(_options.MaxImages, _options.MaxImageBytes);
        var layout = new LayoutAnalyzer(_options);
        var ocrPages = new Dictionary<int, (double Width, double Height)>();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Metadata[$"Page{page.Number}.Unit"] = "point";
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
                .Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
            var pageImages = new List<ImageElement>();
            foreach (var image in page.GetImages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((long)image.WidthInSamples * image.HeightInSamples * 4 > _options.MaxImageBytes)
                {
                    throw new InvalidDataException("PDF image exceeds the decoded image byte limit.");
                }
                if (!image.TryGetPng(out var png))
                {
                    result.Warnings.Add(new("ImageUnavailable", $"An image on page {page.Number} could not be decoded."));
                    continue;
                }
                using var imageStream = new MemoryStream(png);
                var element = new ImageElement
                {
                    Data = images.Read(imageStream, cancellationToken),
                    ContentType = "image/png",
                    PageNumber = page.Number,
                    BoundingBox = LayoutAnalyzer.Normalize(image.BoundingBox, page.Height)
                };
                pageImages.Add(element);
            }
            result.Elements.AddRange(layout.Analyze(words, pageImages, page.Height, page.Number, cancellationToken));
            if (words.Length == 0)
            {
                ocrPages[page.Number] = (page.Width, page.Height);
            }
        }
        if (ocrPages.Count > 0)
        {
            if (_ocrClient is null)
            {
                result.Warnings.Add(new("OcrNotConfigured", "Pages without native text were found. Supply a Document Intelligence client to enable OCR fallback."));
            }
            else
            {
                await ReadOcrAsync(buffer.ToArray(), ocrPages, result, cancellationToken);
            }
        }
        var ordered = result.Elements.OrderBy(element => element.PageNumber).ToArray();
        result.Elements.Clear();
        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Elements.Add(ordered[index] with { Order = index });
        }
        return result;
    }

    private async Task ReadOcrAsync(byte[] data, Dictionary<int, (double Width, double Height)> pages,
        PdfParseResult result, CancellationToken token)
    {
        var request = new AnalyzeDocumentOptions("prebuilt-read", BinaryData.FromBytes(data))
        {
            Pages = string.Join(",", pages.Keys)
        };
        var operation = await _ocrClient!.AnalyzeDocumentAsync(WaitUntil.Started, request, token);
        var completed = await operation.WaitForCompletionAsync(token);
        foreach (var warning in completed.Value.Warnings)
        {
            result.Warnings.Add(new(warning.Code, warning.Message));
        }
        foreach (var page in completed.Value.Pages)
        {
            token.ThrowIfCancellationRequested();
            if (!pages.TryGetValue(page.PageNumber, out var size))
            {
                continue;
            }
            var pageElements = result.Elements.Where(element => element.PageNumber == page.PageNumber).ToList();
            result.Elements.RemoveAll(element => element.PageNumber == page.PageNumber);
            foreach (var line in page.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line.Content))
                {
                    continue;
                }
                DocumentBoundingBox? bounds = null;
                if (line.Polygon.Count >= 4 && line.Polygon.Count % 2 == 0 && page.Width > 0 && page.Height > 0)
                {
                    var xs = line.Polygon.Where((_, index) => index % 2 == 0).ToArray();
                    var ys = line.Polygon.Where((_, index) => index % 2 != 0).ToArray();
                    bounds = new(xs.Min() * size.Width / page.Width.Value, ys.Min() * size.Height / page.Height.Value,
                        (xs.Max() - xs.Min()) * size.Width / page.Width.Value, (ys.Max() - ys.Min()) * size.Height / page.Height.Value);
                }
                pageElements.Add(new TextElement(line.Content) { PageNumber = page.PageNumber, BoundingBox = bounds });
            }
            result.Elements.AddRange(LayoutAnalyzer.Order(pageElements, token, _options.PdfReadingOrder));
        }
        result.Metadata["OcrModelId"] = "prebuilt-read";
    }

    public string ConvertToMarkdown(PdfParseResult result, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        var output = new StringBuilder();
        int? page = null;
        foreach (var element in result.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.PageNumber is not null && element.PageNumber != page)
            {
                page = element.PageNumber;
                output.AppendLine($"<!-- Page {page} -->").AppendLine();
            }
            output.AppendLine(Markdown.Elements([element], cancellationToken, skipImages)).AppendLine();
        }
        return output.ToString().TrimEnd();
    }
}
