namespace SharePointAgent.Infrastructure;

public sealed class MarkItDownConversionException(string message, System.Net.HttpStatusCode statusCode)
    : HttpRequestException(message, null, statusCode);
