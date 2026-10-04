namespace DocumentParsers;

public interface IDocumentFormatResolver
{
    DocumentFormat Resolve(string fileName, string? contentType);
}
