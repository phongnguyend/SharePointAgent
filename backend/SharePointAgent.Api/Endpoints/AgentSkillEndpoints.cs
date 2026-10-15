using System.Text;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class AgentSkillEndpoints
{
    public static void MapAgentSkillEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents/skills");

        group.AddEndpointFilter(async (context, next) =>
        {
            var query = context.HttpContext.Request.Query;
            Guid? agentId = Guid.TryParse(query["agentId"], out var parent) ? parent : null;
            Guid? subAgentId = Guid.TryParse(query["subAgentId"], out var child) ? child : null;
            var store = context.HttpContext.RequestServices.GetRequiredService<AgentSkillStore>();
            if (!await store.ScopeExistsAsync(agentId, subAgentId, context.HttpContext.RequestAborted))
            {
                return Results.NotFound();
            }
            try
            {
                return await next(context);
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("", async (Guid? agentId, Guid? subAgentId,
            AgentSkillStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(AgentSkillStore.Scope(agentId, subAgentId), cancellationToken)));

        group.MapPost("", async (Guid? agentId, Guid? subAgentId, HttpRequest request,
            AgentSkillStore store, CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Upload a ZIP using multipart form field 'file'." });
            }
            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null || form.Files.Count != 1 || file.Length > AgentSkillStore.MaxArchiveBytes ||
                !file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Select one ZIP file no larger than 10 MB." });
            }
            using var content = new MemoryStream();
            await file.CopyToAsync(content, cancellationToken);
            var result = await store.UploadAsync(AgentSkillStore.Scope(agentId, subAgentId), content.ToArray(), cancellationToken);
            return Results.Created($"/api/agents/skills/{result.Id}", result);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));

        group.MapDelete("/{id:guid}", async (Guid id, Guid? agentId, Guid? subAgentId,
            AgentSkillStore store, CancellationToken cancellationToken) =>
            await store.DeleteAsync(AgentSkillStore.Scope(agentId, subAgentId), id, cancellationToken)
                ? Results.NoContent() : Results.NotFound());

        group.MapGet("/{id:guid}/files", async (Guid id, Guid? agentId, Guid? subAgentId,
            AgentSkillStore store, CancellationToken cancellationToken) =>
        {
            var files = await store.ListFilesAsync(AgentSkillStore.Scope(agentId, subAgentId), id, cancellationToken);
            return files is null ? Results.NotFound() : Results.Ok(files);
        });

        group.MapGet("/{id:guid}/download", async (Guid id, string path, Guid? agentId, Guid? subAgentId,
            AgentSkillStore store, CancellationToken cancellationToken) =>
        {
            var file = await store.GetFileAsync(AgentSkillStore.Scope(agentId, subAgentId), id, path, cancellationToken);
            if (file is null)
            {
                return Results.NotFound();
            }
            var bytes = await store.ReadFileAsync(file, cancellationToken);
            return Results.File(bytes, "application/octet-stream", Path.GetFileName(file.Path));
        });

        group.MapGet("/{id:guid}/file", async (Guid id, string path, Guid? agentId, Guid? subAgentId,
            AgentSkillStore store, CancellationToken cancellationToken) =>
        {
            var file = await store.GetFileAsync(AgentSkillStore.Scope(agentId, subAgentId), id, path, cancellationToken);
            if (file is null)
            {
                return Results.NotFound();
            }
            if (file.SizeBytes > 512 * 1024)
            {
                return Results.Ok(new { content = (string?)null, message = "Preview is available for text files up to 512 KB." });
            }
            var bytes = await store.ReadFileAsync(file, cancellationToken);
            if (bytes.Contains((byte)0))
            {
                return Results.Ok(new { content = (string?)null, message = "This file is binary and cannot be previewed as text." });
            }
            try
            {
                return Results.Ok(new { content = new UTF8Encoding(false, true).GetString(bytes), message = (string?)null });
            }
            catch (DecoderFallbackException)
            {
                return Results.Ok(new { content = (string?)null, message = "This file is not UTF-8 text." });
            }
        });
    }
}
