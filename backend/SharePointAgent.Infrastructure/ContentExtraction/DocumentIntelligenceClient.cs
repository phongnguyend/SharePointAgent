using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

public sealed class DocumentIntelligenceClient(
    HttpClient httpClient,
    IOptions<DocumentIntelligenceOptions> options)
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly DocumentIntelligenceOptions _options = options.Value;
    private readonly TokenCredential? _credential = options.Value.UsedManagedIdentity
        ? DependencyInjection.CreateManagedIdentityCredential()
        : null;

    public async Task<string> ExtractAsync(byte[] content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new InvalidOperationException("Configure DocumentIntelligence:Endpoint to extract document text.");
        }
        var endpoint = _options.Endpoint!.TrimEnd('/');
        var url = $"{endpoint}/documentintelligence/documentModels/{Uri.EscapeDataString(_options.ModelId)}:analyze?_overload=analyzeDocument&api-version={Uri.EscapeDataString(_options.ApiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { base64Source = Convert.ToBase64String(content) })
        };
        await AuthorizeAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var operationUrl = response.Headers.Location?.ToString()
            ?? (response.Headers.TryGetValues("Operation-Location", out var values) ? values.Single() : throw new InvalidOperationException("Document Intelligence omitted Operation-Location."));

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            using var poll = new HttpRequestMessage(HttpMethod.Get, operationUrl);
            await AuthorizeAsync(poll, cancellationToken);
            using var pollResponse = await httpClient.SendAsync(poll, cancellationToken);
            await EnsureSuccessAsync(pollResponse, cancellationToken);
            using var json = await JsonDocument.ParseAsync(await pollResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var status = json.RootElement.GetProperty("status").GetString();
            if (status == "succeeded")
            {
                return json.RootElement.GetProperty("analyzeResult").GetProperty("content").GetString() ?? "";
            }

            if (status is "failed" or "canceled")
            {
                throw new InvalidOperationException($"Document Intelligence analysis {status}: {json.RootElement}");
            }
        }
        throw new TimeoutException("Document Intelligence analysis did not finish within two minutes.");
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.UsedManagedIdentity)
        {
            var token = await _credential!.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        else
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey!);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException($"Document Intelligence returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
    }
}
