namespace SharePointAgent.Application;

public sealed record EmbeddingUsageContext(
    string? Operation = null,
    Guid? UserId = null,
    Guid? ConversationId = null,
    Guid? QuestionId = null,
    string? DriveId = null,
    string? FileId = null,
    Guid? AttachmentId = null,
    Guid? ScanId = null,
    int? ChunkNumber = null);

/// <summary>Flows attribution through async calls without sharing it between concurrent requests.</summary>
public sealed class EmbeddingUsageScope : IDisposable
{
    private static readonly AsyncLocal<EmbeddingUsageContext?> Slot = new();
    private readonly EmbeddingUsageContext? previous;
    public static EmbeddingUsageContext Current => Slot.Value ?? new();

    private EmbeddingUsageScope(EmbeddingUsageContext context)
    {
        previous = Slot.Value;
        var parent = Current;
        Slot.Value = context with
        {
            Operation = context.Operation ?? parent.Operation,
            UserId = context.UserId ?? parent.UserId,
            ConversationId = context.ConversationId ?? parent.ConversationId,
            QuestionId = context.QuestionId ?? parent.QuestionId,
            DriveId = context.DriveId ?? parent.DriveId,
            FileId = context.FileId ?? parent.FileId,
            AttachmentId = context.AttachmentId ?? parent.AttachmentId,
            ScanId = context.ScanId ?? parent.ScanId,
            ChunkNumber = context.ChunkNumber ?? parent.ChunkNumber
        };
    }

    public static EmbeddingUsageScope Begin(EmbeddingUsageContext context) => new(context);
    public void Dispose() => Slot.Value = previous;
}
