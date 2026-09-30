using Azure.AI.OpenAI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Persistence;
using SharePointAgent.Domain;

namespace SharePointAgent.Infrastructure;

public sealed class AttachmentImageService(
    AttachmentContentCache contentCache,
    AzureOpenAIClient openAi,
    DocumentIntelligenceClient documentIntelligence,
    IOptions<OpenAiOptions> openAiOptions,
    IOptions<DocumentIntelligenceOptions> documentOptions,
    IOptions<UploadOptions> uploadOptions,
    IDbContextFactory<SharePointIndexDbContext> contextFactory)
{
    public static readonly string[] DescriptionExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    public static readonly string[] TextExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    public async Task<string?> DescribeAsync(Guid id, Guid? userId, CancellationToken ct)
    {
        var file = await DownloadAsync(id, ct);
        if (file is null)
        {
            return null;
        }

        await using var content = file.Content;
        ValidateImageFormat(file.FileName, DescriptionExtensions);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var model = openAiOptions.Value.ChatDeployment;
        var describer = new ImageDescriber(openAi.GetChatClient(model).AsIChatClient(), model, async result =>
        {
            using var recording = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var db = await contextFactory.CreateDbContextAsync(recording.Token);
            var now = DateTimeOffset.UtcNow;
            db.ImageDescriptionTokenUsage.Add(new ImageDescriptionTokenUsageEntity
            {
                CreatedAtUtc = now,
                Day = MonthlyTokenQuota.DayKey(now),
                Month = MonthlyTokenQuota.MonthKey(now),
                UserId = userId,
                // Standalone attachment actions have no conversation or question.
                ConversationId = Guid.Empty,
                QuestionId = Guid.Empty,
                AttachmentId = id,
                ModelId = model,
                SystemPrompt = result.SystemPrompt,
                Prompt = result.Prompt,
                Description = result.Description,
                InputTokens = result.UsageReported ? result.Usage.InputTokens : null,
                OutputTokens = result.UsageReported ? result.Usage.OutputTokens : null,
                TotalTokens = result.UsageReported ? result.Usage.TotalTokens : null
            });
            await db.SaveChangesAsync(recording.Token);
        });
        return (await describer.DescribeContentAsync(file.FileName, bytes, null, null, ct)).Description;
    }

    public async Task<string?> ExtractTextAsync(Guid id, CancellationToken ct)
    {
        var file = await DownloadAsync(id, ct);
        if (file is null)
        {
            return null;
        }

        await using var content = file.Content;
        ValidateImageFormat(file.FileName, TextExtensions);
        if (string.IsNullOrWhiteSpace(documentOptions.Value.Endpoint))
        {
            throw new InvalidOperationException("Configure DocumentIntelligence:Endpoint to extract image text.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return await documentIntelligence.ExtractAsync(buffer.ToArray(), ct);
    }

    public async Task<string> ConvertForIndexAsync(ChatMessageAttachmentFileEntity file, CancellationToken ct)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var sections = new List<string>();
        if (DescriptionExtensions.Contains(extension))
        {
            var description = await DescribeAsync(file.Id, EmbeddingUsageScope.Current.UserId ?? file.CreatedById, ct);
            if (!string.IsNullOrWhiteSpace(description))
            {
                sections.Add("## Image description\n\n" + description);
            }
        }

        if (TextExtensions.Contains(extension) &&
            (!string.IsNullOrWhiteSpace(documentOptions.Value.Endpoint) || !DescriptionExtensions.Contains(extension)))
        {
            var text = await ExtractTextAsync(file.Id, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections.Add("## Extracted text\n\n" + text);
            }
        }

        if (sections.Count == 0)
        {
            throw new InvalidOperationException("No searchable content could be extracted from this image. Check the image format and image processing configuration.");
        }

        return string.Join("\n\n", sections);
    }

    private async Task<AttachmentFileDownload?> DownloadAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var file = await db.ChatMessageAttachmentFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (file is null)
        {
            return null;
        }

        var cached = await contentCache.DownloadAsync(file, ct);
        return new AttachmentFileDownload(new FileStream(cached.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
            file.FileName, file.ContentType ?? "application/octet-stream");
    }

    private void ValidateImageFormat(string fileName, string[] supported)
    {
        if (!uploadOptions.Value.IsImageFile(fileName) || !supported.Contains(Path.GetExtension(fileName).ToLowerInvariant()))
        {
            throw new ArgumentException("This image format is not supported for this action.");
        }
    }
}
