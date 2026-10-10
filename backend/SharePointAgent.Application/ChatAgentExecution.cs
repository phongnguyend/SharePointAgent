using SharePointAgent.Domain;

namespace SharePointAgent.Application;

/// <summary>
/// Only persisted identifiers cross the hosting boundary; SQL owns the conversation.
/// <para>
/// <paramref name="StartedAtUtc"/> is when the turn's quota lease began. Usage rows are stamped with it
/// rather than with the time each request finished, so every request of a turn is billed to the day and
/// month whose allowance was checked at the start — including a turn that runs across midnight UTC. A
/// request that arrives without it falls back to the time the agent starts.
/// </para>
/// </summary>
public sealed record ChatAgentRequest(
    Guid ConversationId,
    Guid QuestionId,
    Guid? UserId = null,
    DateTimeOffset? StartedAtUtc = null);

/// <summary>
/// Reads the agent's working directory from outside a turn, so a person can see what is in the
/// sandbox without asking the model for it. Implemented by reading the disk in <c>Local</c> mode and
/// by asking the sandbox in <c>Foundry</c> mode, where that disk belongs to another process.
/// </summary>
public interface IAgentFileBrowser
{
    Task<FileSystemListing> ListAsync(
        Guid conversationId,
        string? path,
        bool recursive,
        CancellationToken cancellationToken);

    /// <summary>
    /// One file's bytes, for previewing or saving it. Throws <see cref="ArgumentException"/> when the
    /// path is outside the directory, missing, a directory, or over the size limit, and
    /// <see cref="InvalidOperationException"/> when there is no sandbox to read from yet.
    /// </summary>
    Task<FileContent> ReadAsync(Guid conversationId, string path, CancellationToken cancellationToken);

    Task<WorkspaceFileChangeResult> ManageAsync(Guid conversationId, WorkspaceFileChange change, CancellationToken cancellationToken);
}

/// <summary>
/// What a hosted invocation is asking the sandbox for. Everything arrives at the one Invocations
/// endpoint, so the operation is named in a header, which lets the host decide what a request is
/// before reading its body and keeps each operation's payload to its own shape.
/// </summary>
public static class AgentInvocation
{
    /// <summary>
    /// Absent means a chat turn, which is what every earlier caller sends. Prefixed <c>x-client-</c>
    /// because that is the only prefix the hosted agent platform forwards from caller to container;
    /// anything else is stripped before the request reaches this process.
    /// </summary>
    public const string OperationHeader = "x-client-agent-operation";

    public const string ListFilesOperation = "listFiles";

    public const string ReadFileOperation = "readFile";

    public const string ManageFilesOperation = "manageFiles";
}

/// <summary>
/// A hosted invocation that lists the sandbox's working directory instead of running a turn, named as
/// such by <see cref="AgentInvocation.OperationHeader"/>. The host answers it straight from disk, so
/// there is no model request, no token usage, and no conversation history involved.
/// </summary>
public sealed record AgentFileListingRequest(Guid ConversationId, string? Path, bool Recursive);

/// <summary>
/// A hosted invocation that returns one file out of the sandbox, named by
/// <see cref="AgentInvocation.ReadFileOperation"/>. Like a listing it
/// never reaches the model.
/// </summary>
public sealed record AgentFileReadRequest(Guid ConversationId, string Path);

public sealed record AgentFileChangeRequest(Guid ConversationId, WorkspaceFileChange Change);

public interface IChatAgentExecutor
{
    Task<ChatTurn> RunStreamingAsync(
        ChatAgentRequest request,
        Func<string, CancellationToken, ValueTask> onText,
        Func<string, CancellationToken, ValueTask> onStatus,
        CancellationToken cancellationToken);
}

/// <summary>
/// <paramref name="Instructions"/> is what the agent is actually given: its own instructions, plus the
/// rules of the workspace the conversation belongs to. Read it rather than
/// <see cref="AgentDefinition.Instructions"/>, which is only the agent's half.
/// </summary>
public sealed record ChatAgentContext(
    ChatConversation Conversation,
    AgentDefinition Agent,
    IReadOnlyList<ChatMessageRecord> History,
    ChatMessageRecord Question,
    string Instructions);

/// <summary>Loads the same bounded, database-backed context for local and hosted execution.</summary>
public sealed class ChatAgentContextLoader(
    IChatRepository chats,
    IAgentRepository agents,
    IChatWorkspaceRepository workspaces)
{
    public const int MaxHistoryMessages = 40;

    public async Task<ChatAgentContext> LoadAsync(ChatAgentRequest request, CancellationToken cancellationToken)
    {
        var conversation = await chats.GetConversationAsync(request.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation no longer exists.");
        var agent = conversation.AgentId is { } id && id != Guid.Empty
            ? await agents.GetAsync(id, cancellationToken)
            : await agents.GetByNameAsync(AgentDefaults.Name, cancellationToken);
        if (agent is null)
        {
            throw new InvalidOperationException("The agent assigned to this conversation is unavailable.");
        }

        var messages = await chats.ListMessagesAsync(request.ConversationId, cancellationToken);
        var question = messages.SingleOrDefault(m => m.Id == request.QuestionId && m.Role == ChatMessageRole.User)
            ?? throw new InvalidOperationException("The saved question does not belong to this conversation.");
        // The API saves the question before invoking us. Never replay it twice, or include later turns.
        var history = messages.TakeWhile(m => m.Id != question.Id).TakeLast(MaxHistoryMessages).ToArray();

        // The rules are read per turn, so editing them takes effect on the next question rather than
        // only in conversations started afterwards. A workspace deleted mid-turn simply has none.
        var workspace = conversation.WorkspaceId is { } workspaceId
            ? await workspaces.GetAsync(workspaceId, cancellationToken)
            : null;
        var instructions = WorkspaceInstructions.Compose(agent.Instructions, workspace?.Name, workspace?.Instructions);
        return new(conversation, agent, history, question, instructions);
    }
}
