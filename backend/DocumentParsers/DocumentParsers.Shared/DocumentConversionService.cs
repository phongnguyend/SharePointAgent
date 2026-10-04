namespace DocumentParsers;

public sealed class DocumentConversionService(
    IPdfDocumentParser pdf,
    IDocxDocumentParser docx,
    IPptxDocumentParser pptx,
    IXlsxDocumentParser xlsx,
    ImageDescriptionProcessor? images = null,
    IDocumentFormatResolver? resolver = null)
{
    public async Task<MarkdownConversionResult> ConvertAsync(
        Stream stream,
        string fileName,
        string? contentType = null,
        ImageProcessingOptions? imageOptions = null,
        CancellationToken cancellationToken = default, bool skipImages = false)
    {
        var format = (resolver ?? new DocumentFormatResolver()).Resolve(fileName, contentType);
        return format switch
        {
            DocumentFormat.Pdf => await ConvertAsync(await pdf.ParseAsync(stream, cancellationToken), imageOptions, cancellationToken, skipImages),
            DocumentFormat.Docx => await ConvertAsync(await docx.ParseAsync(stream, cancellationToken), imageOptions, cancellationToken, skipImages),
            DocumentFormat.Pptx => await ConvertAsync(await pptx.ParseAsync(stream, cancellationToken), imageOptions, cancellationToken, skipImages),
            DocumentFormat.Xlsx => await ConvertAsync(await xlsx.ParseAsync(stream, cancellationToken), imageOptions, cancellationToken, skipImages),
            _ => throw new NotSupportedException("Unsupported document format.")
        };
    }

    public async Task<MarkdownConversionResult> ConvertAsync(PdfParseResult result, ImageProcessingOptions? options = null, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        await EnrichAsync(result.Elements.OfType<ImageElement>(), result.Warnings, options, cancellationToken, skipImages);
        return new(pdf.ConvertToMarkdown(result, cancellationToken, skipImages), new Dictionary<string, string>(result.Metadata), result.Warnings.ToArray());
    }

    public async Task<MarkdownConversionResult> ConvertAsync(DocxParseResult result, ImageProcessingOptions? options = null, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        await EnrichAsync(result.BodyElements.OfType<ImageElement>(), result.Warnings, options, cancellationToken, skipImages);
        return new(docx.ConvertToMarkdown(result, cancellationToken, skipImages), new Dictionary<string, string>(result.Metadata), result.Warnings.ToArray());
    }

    public async Task<MarkdownConversionResult> ConvertAsync(PptxParseResult result, ImageProcessingOptions? options = null, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        await EnrichAsync(result.Slides.SelectMany(slide => slide.Elements).OfType<ImageElement>(), result.Warnings, options, cancellationToken, skipImages);
        return new(pptx.ConvertToMarkdown(result, cancellationToken, skipImages), new Dictionary<string, string>(result.Metadata), result.Warnings.ToArray());
    }

    public async Task<MarkdownConversionResult> ConvertAsync(XlsxParseResult result, ImageProcessingOptions? options = null, CancellationToken cancellationToken = default, bool skipImages = false)
    {
        await EnrichAsync(result.Worksheets.SelectMany(sheet => sheet.Images), result.Warnings, options, cancellationToken, skipImages);
        return new(xlsx.ConvertToMarkdown(result, cancellationToken, skipImages), new Dictionary<string, string>(result.Metadata), result.Warnings.ToArray());
    }

    private async Task EnrichAsync(IEnumerable<ImageElement> elements, List<DocumentParseWarning> warnings, ImageProcessingOptions? options, CancellationToken token, bool skipImages)
    {
        token.ThrowIfCancellationRequested();
        if (!skipImages && images is not null)
        {
            warnings.AddRange(await images.ProcessAsync(elements, options, token));
        }
    }
}
