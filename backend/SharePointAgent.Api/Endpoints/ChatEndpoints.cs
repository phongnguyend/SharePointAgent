using Microsoft.EntityFrameworkCore;
using SharePointAgent.Persistence;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        // Conversations live in SQL Server; each turn replays the stored history to an agent that can
        // search the index, and both the question and the answer are appended.
        app.MapGet("/api/chat/conversations", (
            HttpContext context,
            IChatRepository store,
            CancellationToken cancellationToken) => store.ListConversationsAsync(cancellationToken,
                !AppAccess.CanReadAdministration(context.AppUser().Roles) ? context.AppUser().Id : null));

        app.MapPost("/api/chat/conversations", async (
            HttpContext context,
            NewConversation? body,
            IChatRepository store,
            IAgentRepository agentRepository,
            CancellationToken cancellationToken) =>
        {
            var title = string.IsNullOrWhiteSpace(body?.Title) ? "New chat" : body!.Title!.Trim();
            var userId = !context.AppUser().Roles.Contains(AppRoles.GlobalAdmin) ? context.EntraObjectId()
                : string.IsNullOrWhiteSpace(body?.UserId) ? null : body!.UserId!.Trim();
            Guid? requestedAgentId = null;
            if (!string.IsNullOrWhiteSpace(body?.AgentId))
            {
                if (!Guid.TryParse(body.AgentId, out var parsedAgentId))
                {
                    return Results.BadRequest(new { error = "'agentId' must be a valid GUID when provided." });
                }

                requestedAgentId = parsedAgentId;
            }

            var usesDefaultAgent = requestedAgentId is null || requestedAgentId == Guid.Empty;
            var selectedAgent = await ResolveAgentAsync(requestedAgentId, agentRepository, cancellationToken);
            if (selectedAgent is null)
            {
                return usesDefaultAgent
                    ? Results.Problem("The default agent is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.BadRequest(new { error = "The selected agent does not exist." });
            }

            return Results.Ok(await store.CreateConversationAsync(title, userId, selectedAgent.Id, cancellationToken, context.AppUser().Id));
        });

        app.MapDelete("/api/chat/conversations/{id:guid}", async (
            Guid id,
            IChatRepository store,
            CancellationToken cancellationToken) =>
            await store.DeleteConversationAsync(id, cancellationToken)
                ? Results.Ok(new { deleted = id })
                : Results.NotFound());

        app.MapPost("/api/chat/conversations/{id:guid}/branch/{messageId:guid}", async (
            Guid id,
            Guid messageId,
            HttpContext context,
            IChatRepository store,
            CancellationToken cancellationToken) =>
        {
            var branch = await store.BranchConversationAsync(id, messageId, cancellationToken, context.AppUser().Id);
            return branch is null
                ? Results.NotFound(new { error = "The conversation or selected message does not exist." })
                : Results.Ok(branch);
        });

        app.MapGet("/api/chat/conversations/{id:guid}/messages", async (
            Guid id,
            IChatRepository store,
            CancellationToken cancellationToken) =>
        {
            var conversation = await store.GetConversationAsync(id, cancellationToken);
            if (conversation is null)
            {
                return Results.NotFound();
            }

            var messages = await store.ListMessagesAsync(id, cancellationToken);
            return Results.Ok(new { conversation, messages });
        });

        app.MapPost("/api/chat/conversations/{id:guid}/messages", async (
            HttpContext context,
            SharePointIndexDbContext db,
            Guid id,
            ChatTurnRequest body,
            MonthlyTokenQuota tokenQuota,
            ContentSafetyService contentSafety,
            IChatRepository store,
            IAgentRepository agentRepository,
            IChatAgentExecutor agent,
            ChatMessageAttachmentFileService attachmentFiles,
            ILogger<Program> logger,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Content))
            {
                return Results.BadRequest(new { error = "A non-empty 'content' is required." });
            }

            var attachmentFileIds = body.AttachmentFileIds?.Distinct().ToArray() ?? [];
            if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                var owner = context.AppUser().Id;
                var ownedCount = await db.ChatMessageAttachmentFiles.CountAsync(x => attachmentFileIds.Contains(x.Id) && x.CreatedById == owner, cancellationToken);
                if (ownedCount != attachmentFileIds.Length)
                {
                    return Results.NotFound(new { error = "Attachment not found." });
                }
                // A former admin may own conversations created with another user's search scope.
                await db.ChatConversations.Where(x => x.Id == id && x.CreatedById == owner)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.UserId, context.EntraObjectId()), cancellationToken);
            }
            if (attachmentFileIds.Length > 10)
            {
                return Results.BadRequest(new { error = "At most 10 attachments can be sent with one message." });
            }

            var conversation = await store.GetConversationAsync(id, cancellationToken);
            if (conversation is null)
            {
                return Results.NotFound();
            }

            var selectedAgent = await ResolveAgentAsync(conversation.AgentId, agentRepository, cancellationToken);
            if (selectedAgent is null)
            {
                return Results.Problem(
                    "The agent assigned to this conversation is unavailable.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var content = body.Content.Trim();
            try
            {
                await attachmentFiles.ValidateReadyAsync(attachmentFileIds, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await using var tokenLease = await tokenQuota.BeginAsync(context.AppUser().Id, cancellationToken);
            Guid? safetyAssessment;
            try
            {
                safetyAssessment = await contentSafety.CheckAsync(content, context.AppUser().Id, id, cancellationToken);
            }
            catch (ContentSafetyRejectedException ex)
            {
                return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.StatusCode);
            }

            // The question is stored before the model runs, so a failed or cancelled turn still leaves the
            // conversation showing what was asked.
            ChatMessageRecord question;
            try
            {
                question = await store.AppendMessageAsync(
                    id, ChatMessageRole.User, content, [], null, null, attachmentFileIds, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await contentSafety.LinkQuestionAsync(safetyAssessment, question.Id, cancellationToken);

            // A conversation created from the sidebar has no title until its first question supplies one.
            var renamed = conversation.Title;
            if (conversation.MessageCount == 0 && conversation.Title == "New chat")
            {
                renamed = content.Length <= 60 ? content : content[..60].TrimEnd() + "…";
                await store.RenameConversationAsync(id, renamed, cancellationToken);
            }

            using var streamWriter = new ChatStreamWriter<ChatStreamEvent>(response);
            streamWriter.Start();
            ValueTask WriteEventAsync(ChatStreamEvent item, CancellationToken token) => streamWriter.WriteAsync(item, token);

            await WriteEventAsync(new ChatStreamEvent("started", Question: question, Title: renamed), cancellationToken);

            ChatTurn turn;
            try
            {
                turn = await agent.RunStreamingAsync(
                    new ChatAgentRequest(id, question.Id, context.AppUser().Id),
                    (text, token) => contentSafety.Enabled ? ValueTask.CompletedTask : WriteEventAsync(new ChatStreamEvent("delta", Text: text), token),
                    (status, token) => contentSafety.Enabled ? ValueTask.CompletedTask : WriteEventAsync(new ChatStreamEvent("status", Message: status), token),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The chat agent failed while answering in conversation {ConversationId}.", id);
                await WriteEventAsync(
                    new ChatStreamEvent("error", Message: $"The assistant could not answer: {ex.Message}"),
                    cancellationToken);
                return Results.Empty;
            }

            // Persist provider-reported usage even if the caller disconnects before the answer is saved.
            using (var accountingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await tokenLease.RecordAsync(question.Id, turn.Usage, turn.ModelId, accountingTimeout.Token);
            }

            try
            {
                await contentSafety.CheckAsync(turn.Text, context.AppUser().Id, id, cancellationToken,
                    "AssistantResponse", question.Id);
            }
            catch (ContentSafetyRejectedException ex)
            {
                await WriteEventAsync(new ChatStreamEvent("error", Message: ex.Message), cancellationToken);
                return Results.Empty;
            }

            var answer = await store.AppendMessageAsync(
                id, ChatMessageRole.Assistant, turn.Text, turn.Citations, turn.Usage, turn.ModelId, [], cancellationToken);
            await WriteEventAsync(new ChatStreamEvent("completed", Answer: answer, Title: renamed), cancellationToken);
            return Results.Empty;
        });

        app.MapGet("/api/chat/feedback", async (
            IChatRepository store,
            CancellationToken cancellationToken,
            ChatFeedback? feedback = null,
            string? search = null,
            int skip = 0,
            int top = 20) =>
        {
            if (top is < 1 or > 100)
            {
                return Results.BadRequest(new { error = "'top' must be between 1 and 100." });
            }

            return Results.Ok(await store.ListFeedbackAsync(feedback, search, Math.Max(0, skip), top, cancellationToken));
        });

        app.MapPost("/api/chat/messages/{id:guid}/feedback", async (
            Guid id,
            MessageFeedback body,
            IChatRepository store,
            CancellationToken cancellationToken) =>
            await store.SetFeedbackAsync(id, body?.Feedback, cancellationToken)
                ? Results.Ok(new { id, feedback = body?.Feedback })
                : Results.NotFound());
    }

    private static Task<AgentDefinition?> ResolveAgentAsync(
        Guid? agentId,
        IAgentRepository agentRepository,
        CancellationToken cancellationToken) =>
        agentId is { } id && id != Guid.Empty
            ? agentRepository.GetAsync(id, cancellationToken)
            : agentRepository.GetByNameAsync(AgentDefaults.Name, cancellationToken);
}
