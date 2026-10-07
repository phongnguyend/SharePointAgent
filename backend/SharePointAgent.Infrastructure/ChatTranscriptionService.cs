using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

public sealed record ChatTranscriptionResult(string Text);

public sealed record ChatTranscriptionOptionsView(bool Enabled, long MaxBytes, int MaxSeconds);

/// <summary>
/// Turns a short recording from the chat composer into text. The audio is sent to the configured Azure
/// OpenAI transcription deployment and discarded; nothing but the usage row is stored. The transcript
/// is screened by content safety later, if and when the user sends it as a message.
/// </summary>
public sealed class ChatTranscriptionService(
    HttpClient http,
    IOptions<OpenAiOptions> options,
    MonthlyTokenQuota quota,
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    TimeProvider clock)
{
    // Azure OpenAI accepts up to 25 MB per transcription request.
    public const long MaxBytes = 25 * 1024 * 1024;

    // The composer stops recording at this length; long dictation is better split into messages.
    public const int MaxSeconds = 300;

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/webm"] = "webm",
        ["audio/ogg"] = "ogg",
        ["audio/mp4"] = "m4a",
        ["audio/x-m4a"] = "m4a",
        ["audio/mpeg"] = "mp3",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
    };

    private readonly OpenAiOptions settings = options.Value;

    // The same managed identity as the other Azure OpenAI clients; created once so its token is cached.
    private static readonly Lazy<TokenCredential> ManagedIdentity = new(DependencyInjection.CreateManagedIdentityCredential);

    public bool Enabled => !string.IsNullOrWhiteSpace(settings.TranscriptionDeployment);

    public ChatTranscriptionOptionsView View => new(Enabled, MaxBytes, MaxSeconds);

    /// <summary>Maps a recorder content type such as "audio/webm;codecs=opus" to a file extension, or null.</summary>
    public static string? ExtensionFor(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return mediaType is not null && Extensions.TryGetValue(mediaType, out var extension) ? extension : null;
    }

    /// <param name="recordedSeconds">The recording length measured by the browser, stored when the model does not report one.</param>
    public async Task<ChatTranscriptionResult> TranscribeAsync(Guid userId, byte[] audio, string? contentType, double? recordedSeconds, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Voice dictation is not configured. Ask an administrator to set AzureOpenAI:TranscriptionDeployment.");
        }
        var extension = ExtensionFor(contentType)
            ?? throw new ArgumentException("Record audio as WebM, Ogg, MP4, MP3, or WAV.");
        if (audio.Length == 0)
        {
            throw new ArgumentException("The recording is empty. Check your microphone and try again.");
        }
        if (audio.Length > MaxBytes)
        {
            throw new ArgumentException("The recording is too long. Keep dictation under five minutes.");
        }
        await quota.EnsureAvailableAsync(userId, ct);

        var deployment = settings.TranscriptionDeployment!;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType!.Split(';', 2)[0].Trim());
        form.Add(file, "file", "dictation." + extension);
        form.Add(new StringContent("json"), "response_format");

        // Called over REST with an explicit api-version, so newer transcription models work without
        // upgrading the Azure OpenAI client library. The deployment path works on both the
        // *.openai.azure.com and *.cognitiveservices.azure.com hosts; the v1 path does not.
        var endpoint = string.IsNullOrWhiteSpace(settings.TranscriptionEndpoint) ? settings.Endpoint : settings.TranscriptionEndpoint;
        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/transcriptions"
            + $"?api-version={Uri.EscapeDataString(settings.TranscriptionApiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = form
        };
        if (settings.UsedManagedIdentity)
        {
            var token = await ManagedIdentity.Value.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        else
        {
            request.Headers.Add("api-key", string.IsNullOrWhiteSpace(settings.TranscriptionApiKey) ? settings.ApiKey : settings.TranscriptionApiKey);
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Provider messages can echo request details, so expose only the status.
            throw new HttpRequestException($"The transcription service returned HTTP {(int)response.StatusCode}. Try again, or type your message instead.", null, response.StatusCode);
        }
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        var text = root.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
        await RecordUsageAsync(userId, deployment, audio.LongLength, root, recordedSeconds, ct);
        return new(text);
    }

    private async Task RecordUsageAsync(Guid userId, string deployment, long audioBytes, JsonElement root, double? recordedSeconds, CancellationToken ct)
    {
        long? Read(JsonElement usage, string name) =>
            usage.TryGetProperty(name, out var number) && number.TryGetInt64(out var result) ? result : null;

        var now = clock.GetUtcNow();
        var row = new TranscriptionTokenUsageEntity
        {
            CreatedAtUtc = now, Day = MonthlyTokenQuota.DayKey(now), Month = MonthlyTokenQuota.MonthKey(now),
            UserId = userId, ModelId = deployment, AudioBytes = audioBytes
        };
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            // gpt-4o transcription models report tokens; Whisper reports only the audio duration.
            row.InputTokens = Read(usage, "input_tokens");
            row.OutputTokens = Read(usage, "output_tokens");
            row.TotalTokens = Read(usage, "total_tokens");
            if (usage.TryGetProperty("seconds", out var seconds) && seconds.TryGetDouble(out var duration))
            {
                row.DurationSeconds = duration;
            }
        }
        // gpt-4o transcription models report tokens but no duration, so fall back to the browser's
        // measurement. It is client-supplied and informational only, so keep it within the recording limit.
        if (row.DurationSeconds is null && recordedSeconds is { } recorded && double.IsFinite(recorded) && recorded > 0)
        {
            row.DurationSeconds = Math.Round(Math.Min(recorded, MaxSeconds), 1);
        }
        // The transcript has been produced, so record its cost even if the caller has gone away.
        await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
        db.TranscriptionTokenUsage.Add(row);
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
