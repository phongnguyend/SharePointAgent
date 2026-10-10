using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
            IChatWorkspaceRepository workspaceRepository,
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

            Guid? workspaceId = null;
            if (!string.IsNullOrWhiteSpace(body?.WorkspaceId))
            {
                if (!Guid.TryParse(body.WorkspaceId, out var parsedWorkspaceId))
                {
                    return Results.BadRequest(new { error = "'workspaceId' must be a valid GUID when provided." });
                }

                var owner = context.AppUser().Roles.Contains(AppRoles.GlobalAdmin) ? null : (Guid?)context.AppUser().Id;
                if (await workspaceRepository.GetAsync(parsedWorkspaceId, cancellationToken, owner) is null)
                {
                    return Results.BadRequest(new { error = "The selected workspace does not exist." });
                }

                workspaceId = parsedWorkspaceId;
            }

            var usesDefaultAgent = requestedAgentId is null || requestedAgentId == Guid.Empty;
            var selectedAgent = await ResolveAgentAsync(requestedAgentId, agentRepository, cancellationToken);
            if (selectedAgent is null)
            {
                return usesDefaultAgent
                    ? Results.Problem("The default agent is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.BadRequest(new { error = "The selected agent does not exist." });
            }

            return Results.Ok(await store.CreateConversationAsync(
                title, userId, selectedAgent.Id, workspaceId, cancellationToken, context.AppUser().Id));
        });

        app.MapDelete("/api/chat/conversations/{id:guid}", async (
            Guid id,
            IChatRepository store,
            CancellationToken cancellationToken) =>
            await store.DeleteConversationAsync(id, cancellationToken)
                ? Results.Ok(new { deleted = id })
                : Results.NotFound());

        // What is in the sandbox right now, read without running a turn: in Local mode straight off
        // this process's disk, in Foundry mode by asking the host, which answers from disk without
        // calling the model. Either way no tokens are spent and the conversation is unchanged.
        app.MapGet("/api/chat/conversations/{id:guid}/files", async (
            Guid id,
            IChatRepository store,
            IAgentFileBrowser browser,
            CancellationToken cancellationToken,
            string? path = null,
            bool recursive = false) =>
        {
            if (await store.GetConversationAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await browser.ListAsync(id, path, recursive, cancellationToken));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidDataException or HttpRequestException)
            {
                return Results.Problem(
                    $"The sandbox could not be read: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // One file out of the sandbox, to show or to save. Same no-model path as the listing.
        app.MapGet("/api/chat/conversations/{id:guid}/files/content", async (
            Guid id,
            string path,
            IChatRepository store,
            IAgentFileBrowser browser,
            HttpResponse response,
            CancellationToken cancellationToken,
            bool download = false) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest(new { error = "A 'path' is required." });
            }

            if (await store.GetConversationAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            FileContent file;
            try
            {
                file = await browser.ReadAsync(id, path, cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidDataException or HttpRequestException)
            {
                return Results.Problem(
                    $"The sandbox could not be read: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }

            // These bytes are whatever the agent or a document author put there, served from the API's
            // own origin. Never let the browser decide the type, and only render inline the kinds that
            // cannot carry script; everything else is handed over as a download instead.
            response.Headers.XContentTypeOptions = "nosniff";
            var inline = !download && RendersSafelyInline(file.ContentType);
            return Results.File(file.Content, file.ContentType, inline ? null : file.Name);
        });

        // Which sandbox the next turn will reach, and whether anything else reaches it too. Reading
        // this runs no turn and changes no binding. The configured endpoint is a deployment detail, so
        // only an administration reader is shown it; the session ID belongs to the conversation.
        app.MapGet("/api/chat/conversations/{id:guid}/session", async (
            HttpContext context,
            Guid id,
            IFoundrySessionRepository sessions,
            IOptions<ChatAgentHostingOptions> hosting,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            var binding = await sessions.DescribeAsync(id, cancellationToken);
            if (binding is null)
            {
                return Results.NotFound();
            }

            var options = hosting.Value;
            var local = options.Mode != ChatAgentExecutionMode.Foundry;
            var configured = local ? null : options.Foundry.Endpoint;
            var showsEndpoints = AppAccess.CanReadAdministration(context.AppUser().Roles);

            // Only a binding made against the endpoint in force now is sent back to Foundry; any other
            // is dead weight, and saying so is the point of showing the two side by side.
            var reused = !local && binding.SessionId is not null && binding.Endpoint == configured;

            // When the API runs the agent, its isolated environment is tracked on the same row, apart from
            // the Foundry session. Only the API's own agent registers a workspace provider.
            var environment = local && services.GetService<IAgentWorkspaceProvider>() is { } workspaces
                ? await workspaces.DescribeAsync(id, cancellationToken)
                : null;
            if (environment is not null)
            {
                reused = environment.EnvironmentId is not null;
            }

            return Results.Ok(new ChatSandboxSession(
                options.Mode.ToString(),
                binding.WorkspaceId is null ? "Conversation" : "Workspace",
                binding.WorkspaceId,
                binding.WorkspaceName,
                binding.ConversationCount,
                local ? null : binding.SessionId,
                showsEndpoints ? binding.Endpoint : null,
                showsEndpoints ? configured : null,
                reused,
                environment?.Mode.ToString() ?? (local ? AgentWorkspaceMode.Local.ToString() : null),
                environment?.EnvironmentId));
        });

        // Discards the dynamic session or sandbox the API's own agent uses for this conversation's scope,
        // with its files, so the next turn starts a fresh one. A workspace's conversations share it.
        app.MapPost("/api/chat/conversations/{id:guid}/session/reset", async (
            Guid id,
            IFoundrySessionRepository sessions,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            if (await sessions.DescribeAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            if (services.GetService<IAgentWorkspaceProvider>() is not { } workspaces)
            {
                return Results.Conflict(new { error = "This conversation has no isolated environment of its own to reset." });
            }

            try
            {
                if (!await workspaces.ResetAsync(id, cancellationToken))
                {
                    return Results.Conflict(new { error = "This conversation has no isolated environment of its own to reset." });
                }
            }
            catch (AgentWorkspaceUnavailableException exception)
            {
                // Its messages are written for users and name no resources.
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { reset = id });
        });

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
            // Keep one correlation ID for the question and answer, across agent/tool activities.
            using var fallbackActivity = System.Diagnostics.Activity.Current is null
                ? new System.Diagnostics.Activity("ChatTurn").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start()
                : null;
            var traceId = System.Diagnostics.Activity.Current!.TraceId.ToString();

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
                    id, ChatMessageRole.User, content, [], null, null, attachmentFileIds, traceId, cancellationToken);
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
                    new ChatAgentRequest(id, question.Id, context.AppUser().Id, tokenLease.StartedAt),
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

            // The agent records each model request as it completes. This is the safety net for a turn
            // that left no row at all, so usage is never lost entirely; it does nothing otherwise.
            using (var accountingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await tokenLease.RecordAsync(id, question.Id, turn.Usage, turn.ModelId, accountingTimeout.Token);
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
                id, ChatMessageRole.Assistant, turn.Text, turn.Citations, turn.Usage, turn.ModelId, [], traceId, cancellationToken);
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

    /// <summary>
    /// Whether a type can be shown in the page without the risk an HTML, SVG, or XML document carries:
    /// those run script in the API's origin if a browser renders them, so they are downloaded instead.
    /// </summary>
    private static bool RendersSafelyInline(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? !contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)
            : contentType is "application/pdf" or "text/plain" or "text/markdown" or "text/csv" or "application/json";

    private static Task<AgentDefinition?> ResolveAgentAsync(
        Guid? agentId,
        IAgentRepository agentRepository,
        CancellationToken cancellationToken) =>
        agentId is { } id && id != Guid.Empty
            ? agentRepository.GetAsync(id, cancellationToken)
            : agentRepository.GetByNameAsync(AgentDefaults.Name, cancellationToken);
}
