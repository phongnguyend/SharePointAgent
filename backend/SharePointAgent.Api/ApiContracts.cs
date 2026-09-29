using SharePointAgent.Domain;

/// <summary>
/// Request body for the search endpoints. When <see cref="UserId"/> is supplied, results are restricted to
/// content that user is allowed to view; omitting it searches the whole index.
/// </summary>
public sealed record SearchPayload(string? Query, string? UserId, int Top = 10, int Skip = 0);

/// <summary>
/// How long a renewed subscription should last. Omit <see cref="Days"/> to use
/// <c>SharePoint:SubscriptionLifetimeDays</c>; the value is clamped to what Microsoft Graph allows.
/// </summary>
public sealed record SubscriptionLifetime(int? Days);

public sealed record SubscriptionAutoRenewRequest(bool? Enabled);

/// <summary>
/// A new subscription. <see cref="NotificationUrl"/> overrides <c>SharePoint:NotificationUrl</c> for
/// this subscription only and must be an absolute HTTPS URL that Microsoft Graph can reach; omit it to
/// use the configured value. A URL other than the configured one produces a subscription the renewal
/// service does not treat as its own.
/// </summary>
public sealed record CreateSubscriptionRequest(string? Name, int? Days, string? NotificationUrl, string? ClientState);

/// <summary>
/// A new conversation. <see cref="UserId"/> optionally restricts search permissions, while a null or
/// empty <see cref="AgentId"/> selects the built-in default agent. A <see cref="WorkspaceId"/> puts
/// the conversation in that workspace, so it shares the sandbox — and the files — of every other
/// conversation in it; omitting it gives the conversation a sandbox of its own. This is the only
/// point at which membership is decided: a conversation cannot be moved afterwards.
/// </summary>
public sealed record NewConversation(string? Title, string? UserId, string? AgentId, string? WorkspaceId = null);

/// <summary>
/// A workspace's name and its rules, on create and on update. <see cref="Instructions"/> is added to
/// the agent's own instructions for every conversation in the workspace; null or empty clears it. An
/// update applies from the next turn asked in the workspace.
/// </summary>
public sealed record ChatWorkspaceRequest(string? Name, string? Instructions = null);

public sealed record ChatTurnRequest(string? Content, IReadOnlyList<Guid>? AttachmentFileIds);

public sealed record AgentDefinitionRequest(string? Name, string? ModelId, string? Instructions);

/// <summary>One newline-delimited event sent while a chat turn is running.</summary>
public sealed record ChatStreamEvent(
    string Type,
    string? Text = null,
    string? Message = null,
    ChatMessageRecord? Question = null,
    ChatMessageRecord? Answer = null,
    string? Title = null);

/// <summary>A reaction to one answer. A null <see cref="Feedback"/> clears an earlier one.</summary>
public sealed record MessageFeedback(ChatFeedback? Feedback);
