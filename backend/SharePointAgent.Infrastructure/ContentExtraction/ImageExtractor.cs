using Azure.AI.OpenAI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

/// <summary>
/// Extracts text from an image by running OCR and a vision model description together, so both the
/// literal text in the image and a description of what it shows survive into the indexed content.
/// </summary>
public sealed class ImageExtractor(
    DocumentIntelligenceClient documentIntelligence,
    AzureOpenAIClient openAi,
    IOptions<OpenAiOptions> openAiOptions,
    IOptions<DocumentIntelligenceOptions> documentOptions,
    IDbContextFactory<SharePointIndexDbContext> contextFactory) : IImageExtractor
{
    public async Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken)
    {
        var filePath = string.IsNullOrWhiteSpace(item.ParentPath) ? item.Name : $"{item.ParentPath}/{item.Name}";
        var text = await ExtractSectionsAsync(item.Name, filePath, null, null, content, cancellationToken);
        return text.Length > 0
            ? text
            : $"File name: {item.Name}\nContent type: {item.MimeType}\nPath: {item.ParentPath}";
    }

    public async Task<string> ExtractAsync(ChatMessageAttachmentFileEntity attachment, byte[] content, CancellationToken cancellationToken)
    {
        var userId = EmbeddingUsageScope.Current.UserId ?? attachment.CreatedById;
        var text = await ExtractSectionsAsync(attachment.FileName, null, attachment.Id, userId, content, cancellationToken);
        return text.Length > 0
            ? text
            : throw new InvalidOperationException("No searchable content could be extracted from this image. Check the image format and image processing configuration.");
    }

    private async Task<string> ExtractSectionsAsync(string fileName, string? filePath, Guid? attachmentId, Guid? userId, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var sections = new List<string>();

        if (AttachmentImageService.DescriptionExtensions.Contains(extension))
        {
            var description = await DescribeAsync(fileName, filePath, attachmentId, userId, content, cancellationToken);
            if (!string.IsNullOrWhiteSpace(description))
            {
                sections.Add("## Image description\n\n" + description);
            }
        }

        if (AttachmentImageService.TextExtensions.Contains(extension) && !string.IsNullOrWhiteSpace(documentOptions.Value.Endpoint))
        {
            var text = await documentIntelligence.ExtractAsync(content, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections.Add("## Extracted text\n\n" + text);
            }
        }

        return string.Join("\n\n", sections);
    }

    private async Task<string> DescribeAsync(string fileName, string? filePath, Guid? attachmentId, Guid? userId, byte[] content, CancellationToken cancellationToken)
    {
        var model = openAiOptions.Value.ChatDeployment;
        var describer = new ImageDescriber(openAi.GetChatClient(model).AsIChatClient(), model, async result =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            db.ImageDescriptionTokenUsage.Add(new ImageDescriptionTokenUsageEntity
            {
                CreatedAtUtc = now,
                Day = MonthlyTokenQuota.DayKey(now),
                Month = MonthlyTokenQuota.MonthKey(now),
                UserId = userId,
                // Indexing runs outside any conversation or question.
                ConversationId = Guid.Empty,
                QuestionId = Guid.Empty,
                AttachmentId = attachmentId,
                FilePath = filePath,
                ModelId = model,
                SystemPrompt = result.SystemPrompt,
                Prompt = result.Prompt,
                Description = result.Description,
                InputTokens = result.UsageReported ? result.Usage.InputTokens : null,
                OutputTokens = result.UsageReported ? result.Usage.OutputTokens : null,
                TotalTokens = result.UsageReported ? result.Usage.TotalTokens : null
            });
            await db.SaveChangesAsync(cancellationToken);
        });
        return (await describer.DescribeContentAsync(fileName, content, filePath, null, cancellationToken)).Description;
    }
}

