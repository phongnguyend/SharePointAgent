namespace DocumentParsers;

public sealed class DocumentFormatResolver : IDocumentFormatResolver
{
    public DocumentFormat Resolve(string fileName, string? contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var (format, expectedType) = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => (DocumentFormat.Pdf, "application/pdf"),
            ".docx" => (DocumentFormat.Docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ".pptx" => (DocumentFormat.Pptx, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            ".xlsx" => (DocumentFormat.Xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => throw new NotSupportedException("Supported formats are PDF, DOCX, PPTX and XLSX.")
        };
        var mime = contentType?.Split(';')[0].Trim();
        if (!string.IsNullOrWhiteSpace(mime) && !mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
            !mime.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Content type does not match the document extension.");
        }
        return format;
    }
}
