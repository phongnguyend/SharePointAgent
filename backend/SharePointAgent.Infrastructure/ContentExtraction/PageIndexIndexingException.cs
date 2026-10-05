namespace SharePointAgent.Infrastructure;

public sealed class PageIndexIndexingException(string message, System.Net.HttpStatusCode statusCode)
    : HttpRequestException(message, null, statusCode);
