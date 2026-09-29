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
