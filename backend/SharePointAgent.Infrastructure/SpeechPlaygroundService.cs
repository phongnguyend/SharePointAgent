using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Azure.Core;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure;

public sealed class SpeechPlaygroundService(HttpClient http, TokenCredential credential, IOptions<SpeechOptions> options)
{
    public const int MaxCharacters = 3000;

    public bool Configured => Uri.TryCreate(options.Value.Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.UserInfo.Length == 0
        && (options.Value.UseManagedIdentity ? !string.IsNullOrWhiteSpace(options.Value.ResourceId) : !string.IsNullOrWhiteSpace(options.Value.ApiKey));

    public async Task<SpeechVoice[]> GetVoicesAsync(CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "tts/cognitiveservices/voices/list", ct);
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<SpeechVoice[]>(ct) ?? [];
    }

    public async Task<byte[]> SynthesizeAsync(string? text, string? voice, string? format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCharacters)
        {
            throw new ArgumentException($"Enter between 1 and {MaxCharacters:N0} characters.");
        }
        if (format is not ("mp3" or "wav"))
        {
            throw new ArgumentException("Select MP3 or WAV output.");
        }
        try
        {
            XmlConvert.VerifyXmlChars(text);
        }
        catch (XmlException)
        {
            throw new ArgumentException("Text contains unsupported control characters.");
        }
        var selected = (await GetVoicesAsync(ct)).FirstOrDefault(item => item.ShortName == voice);
        if (selected is null)
        {
            throw new ArgumentException("Select an available voice.");
        }
        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        var ssml = new XElement(ns + "speak", new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xml + "lang", selected.Locale),
            new XElement(ns + "voice", new XAttribute("name", selected.ShortName), text));
        using var request = await CreateRequestAsync(HttpMethod.Post, "tts/cognitiveservices/v1", ct);
        request.Headers.Add("X-Microsoft-OutputFormat", format == "mp3" ? "audio-24khz-48kbitrate-mono-mp3" : "riff-24khz-16bit-mono-pcm");
        request.Content = new StringContent(ssml.ToString(), Encoding.UTF8, "application/ssml+xml");
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        if (!Configured)
        {
            throw new InvalidOperationException("Configure Speech endpoint and credentials on the server.");
        }
        var settings = options.Value;
        // Regional Speech APIs use this composite Entra token to identify the resource.
        var token = settings.UseManagedIdentity
            ? await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct)
            : default;
        var request = new HttpRequestMessage(method, new Uri(new Uri(settings.Endpoint), path));
        request.Headers.UserAgent.ParseAdd("SharePointAgent/1.0");
        if (settings.UseManagedIdentity)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"aad#{settings.ResourceId}#{token.Token}");
        }
        else
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", settings.ApiKey);
        }
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Azure Speech request failed (HTTP {(int)response.StatusCode}). Check the resource configuration, Speech roles, and quota.", null, response.StatusCode);
        }
    }
}

public sealed record SpeechVoice(string ShortName, string DisplayName, string Locale, string Gender);
