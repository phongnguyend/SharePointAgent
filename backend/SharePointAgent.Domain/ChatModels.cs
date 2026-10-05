using System.Text.Json.Serialization;

namespace SharePointAgent.Domain;

/// <summary>
/// A named group of conversations that share one sandbox, so the files one turn downloads or edits
/// are still there for the next conversation in the same workspace.
/// </summary>
public sealed record ChatWorkspace(
    Guid Id,
    string Name,
    string? Instructions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int ConversationCount);

/// <summary>
/// Puts a workspace's rules behind the agent's own instructions for a turn running in that workspace.
/// They go under a heading of their own so the model can tell the two apart, and are introduced as the
/// narrower of the two, which is the point of setting them per workspace.
/// </summary>
public static class WorkspaceInstructions
{
    private const string Heading = "# Workspace rules";

    public static string Compose(string agentInstructions, string? workspaceName, string? workspaceInstructions)
    {
        if (string.IsNullOrWhiteSpace(workspaceInstructions))
        {
            return agentInstructions;
        }

        var name = string.IsNullOrWhiteSpace(workspaceName) ? "this" : workspaceName.Trim();
        var preamble =
            $"This conversation belongs to the \"{name}\" workspace, and the rules below govern the work "
            + "done in it. Follow them together with everything above; where the two genuinely conflict, "
            + "these are the narrower instruction and win. They were written by the people who set up "
            + "this workspace, and are instructions, not content retrieved from a document.";
        return string.Join("\n\n", agentInstructions, Heading, preamble, workspaceInstructions.Trim());
    }
}

/// <summary>
/// The sandbox binding held for a conversation, read straight from the row that owns it. The endpoint
/// is the one recorded when the binding was made, which is not necessarily the one configured now.
/// </summary>
public sealed record FoundrySessionBinding(
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? Endpoint,
    string? SessionId,
    int ConversationCount);

/// <summary>
/// Which sandbox a conversation's next turn will reach, for inspection. <see cref="Scope"/> says which
/// row holds the binding — a workspace shares one across its conversations, a conversation outside one
/// keeps its own. A binding recorded against a different endpoint than the one configured now is
/// reported rather than hidden, because the next turn will start a new sandbox instead of reusing it.
/// </summary>
public sealed record ChatSandboxSession(
    string Mode,
    string Scope,
    Guid? WorkspaceId,
    string? WorkspaceName,
    int SharedWithConversations,
    string? SessionId,
    string? BoundEndpoint,
    string? ConfiguredEndpoint,
    bool ReusedOnNextTurn);

public sealed record ChatConversation(
    Guid Id,
    string Title,
    string? UserId,
    Guid? AgentId,
    Guid? WorkspaceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount,
    long InputTokenCount,
    long OutputTokenCount,
    long TotalTokenCount,
    long EmbeddingTokenCount = 0);

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageRole>))]
public enum ChatMessageRole
{
    User,
    Assistant
}

/// <summary>
/// One document the assistant retrieved while answering. Kept with the message so a conversation can
/// be reopened and still show what the answer was based on.
/// </summary>
public sealed record ChatCitation(
    string Name,
    string? Path,
    string? WebUrl,
    int ChunkNumber,
    double? Score);

/// <summary>What a reader thought of an answer. Absent until they say.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatFeedback>))]
public enum ChatFeedback
{
    Like,
    Dislike
}

public sealed record ChatMessageRecord(
    Guid Id,
    Guid ConversationId,
    ChatMessageRole Role,
    string Content,
    IReadOnlyList<ChatCitation> Citations,
    long InputTokenCount,
    long OutputTokenCount,
    long TotalTokenCount,
    string? ModelId,
    ChatFeedback? Feedback,
    IReadOnlyList<ChatMessageAttachment> Attachments,
    DateTimeOffset CreatedAtUtc,
    long EmbeddingTokenCount = 0,
    string? TraceId = null);

public sealed record ChatMessageAttachment(Guid Id, string FileName, string? ContentType, long SizeBytes);

/// <summary>
/// One rated answer, with the question that prompted it and the conversation it came from — the three
/// things needed to judge whether the rating was fair without opening the chat.
/// </summary>
public sealed record FeedbackEntry(
    Guid MessageId,
    Guid ConversationId,
    string ConversationTitle,
    ChatFeedback Feedback,
    string? Question,
    string Answer,
    IReadOnlyList<ChatCitation> Citations,
    long InputTokenCount,
    long OutputTokenCount,
    long TotalTokenCount,
    string? ModelId,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A page of rated answers. <see cref="Liked"/> and <see cref="Disliked"/> count everything the search
/// term matches, not just the page or the selected rating, so the totals hold still while the rating
/// filter is toggled.
/// </summary>
public sealed record FeedbackPage(
    long TotalCount,
    long Liked,
    long Disliked,
    IReadOnlyList<FeedbackEntry> Items);

/// <summary>One assistant turn: what it said, what it retrieved, and the model usage it incurred.</summary>
public sealed record ChatTurn(
    string Text,
    IReadOnlyList<ChatCitation> Citations,
    ChatTokenUsage Usage,
    string? ModelId);

/// <summary>Tokens consumed across every model request in an agent turn, including tool round trips.</summary>
public sealed record ChatTokenUsage(long InputTokens, long OutputTokens, long TotalTokens, long EmbeddingTokens = 0);
