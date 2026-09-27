using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using SharePointAgent.Application;
using SharePointAgent.Persistence;

namespace SharePointAgent.Infrastructure;

/// <summary>Persists provider-reported usage before downstream indexing or search can fail.</summary>
public sealed class TrackedEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    IDbContextFactory<SharePointIndexDbContext> factory,
    string deploymentId) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var attribution = EmbeddingUsageScope.Current;
        var generated = await inner.GenerateAsync(values, options, cancellationToken);
        // A completed provider call consumed tokens even if the caller has since disconnected.
        using var recording = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var db = await factory.CreateDbContextAsync(recording.Token);
        if (attribution.AttachmentId is { } attachmentId && attribution.QuestionId is null)
        {
            var links = await db.ChatMessageAttachments.Where(x => x.AttachmentFileId == attachmentId)
                .Select(x => new { x.MessageId, x.Message!.ConversationId }).Distinct().Take(2)
                .ToListAsync(recording.Token);
            if (links.Count == 1)
            {
                attribution = attribution with
                {
                    QuestionId = links[0].MessageId,
                    ConversationId = attribution.ConversationId ?? links[0].ConversationId
                };
            }
        }
        var userId = attribution.UserId;
        if (userId is null && attribution.ConversationId is { } conversationId)
        {
            userId = await db.ChatConversations.Where(x => x.Id == conversationId)
                .Select(x => x.CreatedById).SingleOrDefaultAsync(recording.Token);
        }

        db.EmbeddingTokenUsage.Add(new EmbeddingTokenUsageEntity
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Operation = attribution.Operation ?? "Embedding",
            EmbeddingModelId = generated.FirstOrDefault()?.ModelId ?? options?.ModelId ?? deploymentId,
            DeploymentId = deploymentId,
            InputTokens = generated.Usage?.InputTokenCount,
            TotalTokens = generated.Usage?.TotalTokenCount ?? generated.Usage?.InputTokenCount,
            UserId = userId,
            ConversationId = attribution.ConversationId,
            QuestionId = attribution.QuestionId,
            DriveId = attribution.DriveId,
            FileId = attribution.FileId,
            AttachmentId = attribution.AttachmentId,
            ScanId = attribution.ScanId,
            ChunkNumber = attribution.ChunkNumber,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString()
        });
        await db.SaveChangesAsync(recording.Token);
        return generated;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
