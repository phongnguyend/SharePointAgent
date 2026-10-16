using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

public sealed class ContentSafetyRejectedException(string message, int statusCode, string code) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class ContentSafetyService(HttpClient http, TokenCredential credential,
    IDbContextFactory<SharePointIndexDbContext> factory, IOptions<ContentSafetyOptions> options)
{
    public bool Enabled => options.Value.Enabled;

    public bool Configured => Uri.TryCreate(options.Value.Endpoint, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme == "https"
        && (options.Value.UseManagedIdentity || !string.IsNullOrWhiteSpace(options.Value.ApiKey));

    public async Task<Guid?> CheckAsync(string text, Guid? userId, Guid? conversationId, CancellationToken ct,
        string operation = "UserMessage", Guid? questionId = null, Guid? attachmentId = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var assessmentId = Guid.NewGuid();
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(10000, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }
            await AnalyzeChunkAsync(text.Substring(offset, length), assessmentId, userId, conversationId,
                operation, questionId, attachmentId, ct);
            offset += length;
        }
        return assessmentId;
    }

    public async Task<ContentSafetyAssessment> EvaluateAsync(string text, Guid userId, CancellationToken ct)
    {
        if (!Configured)
        {
            throw new InvalidOperationException("Configure a ContentSafety HTTPS endpoint and credentials to use this playground.");
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 10000)
        {
            throw new ArgumentException("Enter between 1 and 10,000 characters.");
        }
        var row = await AnalyzeChunkAsync(text, Guid.NewGuid(), userId, null, "Playground", null, null, ct, rejectBlocked: false);
        return new(row.AssessmentId, row.Status,
        [
            new("Hate", row.HateSeverity!.Value, row.HateThreshold),
            new("Sexual", row.SexualSeverity!.Value, row.SexualThreshold),
            new("Violence", row.ViolenceSeverity!.Value, row.ViolenceThreshold),
            new("SelfHarm", row.SelfHarmSeverity!.Value, row.SelfHarmThreshold)
        ], row.CharacterCount, row.DurationMs, row.TraceId);
    }

    private async Task<ContentSafetyUsageEntity> AnalyzeChunkAsync(string text, Guid assessmentId, Guid? userId, Guid? conversationId,
        string operation, Guid? questionId, Guid? attachmentId, CancellationToken ct, bool rejectBlocked = true)
    {
        var settings = options.Value;
        var row = new ContentSafetyUsageEntity
        {
            AssessmentId = assessmentId,
            Operation = operation,
            QuestionId = questionId,
            AttachmentId = attachmentId,
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UserId = userId,
            ConversationId = conversationId,
            CharacterCount = text.Length,
            EstimatedTextRecords = (text.Length + 999) / 1000,
            HateThreshold = settings.HateThreshold,
            SexualThreshold = settings.SexualThreshold,
            ViolenceThreshold = settings.ViolenceThreshold,
            SelfHarmThreshold = settings.SelfHarmThreshold,
            TraceId = Activity.Current?.TraceId.ToString()
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ContentSafetyUsage.Add(row);
        await db.SaveChangesAsync(ct);
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                settings.Endpoint.TrimEnd('/') + "/contentsafety/text:analyze?api-version=2024-09-01");
            request.Content = JsonContent.Create(new { text, categories = new[] { "Hate", "Sexual", "Violence", "SelfHarm" }, outputType = "EightSeverityLevels" });
            if (settings.UseManagedIdentity)
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }
            else
            {
                request.Headers.Add("Ocp-Apim-Subscription-Key", settings.ApiKey);
            }
            using var response = await http.SendAsync(request, ct);
            row.HttpStatusCode = (int)response.StatusCode;
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var categories = json.RootElement.GetProperty("categoriesAnalysis").EnumerateArray()
                .ToDictionary(x => x.GetProperty("category").GetString()!, x => x.GetProperty("severity").GetInt32());
            int Severity(string category)
            {
                if (!categories.TryGetValue(category, out var value) || value is < 0 or > 7)
                {
                    throw new InvalidDataException("Invalid content safety response.");
                }
                return value;
            }
            row.HateSeverity = Severity("Hate");
            row.SexualSeverity = Severity("Sexual");
            row.ViolenceSeverity = Severity("Violence");
            row.SelfHarmSeverity = Severity("SelfHarm");
            row.Status = row.HateSeverity >= settings.HateThreshold || row.SexualSeverity >= settings.SexualThreshold
                || row.ViolenceSeverity >= settings.ViolenceThreshold || row.SelfHarmSeverity >= settings.SelfHarmThreshold ? "Blocked" : "Allowed";
        }
        catch (Exception ex)
        {
            row.Status = ct.IsCancellationRequested ? "Cancelled" : "Failed";
            row.ErrorCode = ex is HttpRequestException ? "ServiceHttpError" : ex is OperationCanceledException ? "TimeoutOrCancellation" : "AnalysisUnavailable";
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new ContentSafetyRejectedException("Content safety is temporarily unavailable. Please try again.", 503, "content_safety_unavailable");
        }
        finally
        {
            row.DurationMs = timer.ElapsedMilliseconds;
            using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await db.SaveChangesAsync(saveTimeout.Token);
        }
        if (rejectBlocked && row.Status == "Blocked")
        {
            throw new ContentSafetyRejectedException("This content was blocked by the application's content safety policy.", 422, "content_safety_blocked");
        }
        return row;
    }

    public async Task LinkQuestionAsync(Guid? usageId, Guid questionId, CancellationToken ct)
    {
        if (usageId is not { } id)
        {
            return;
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.ContentSafetyUsage.Where(x => x.AssessmentId == id).ExecuteUpdateAsync(set => set.SetProperty(x => x.QuestionId, questionId), ct);
    }
}
