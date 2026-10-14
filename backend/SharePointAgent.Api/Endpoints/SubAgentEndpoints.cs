using Microsoft.EntityFrameworkCore;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;

namespace SharePointAgent.Api;

public static class SubAgentEndpoints
{
    public sealed record SubAgentRequest(string Name, string Description, string ModelId,
        string Instructions, string[] Tools, string[] Skills);

    public static void MapSubAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/agents/{parentId:guid}/sub-agents", async (
            Guid parentId, IDbContextFactory<SharePointIndexDbContext> factory, CancellationToken cancellationToken) =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (!await db.AgentDefinitions.AnyAsync(x => x.Id == parentId, cancellationToken))
            {
                return Results.NotFound();
            }
            return Results.Ok(await db.SubAgentDefinitions.AsNoTracking().Where(x => x.ParentAgentId == parentId)
                .OrderBy(x => x.Name).ToArrayAsync(cancellationToken));
        });

        app.MapPost("/api/agents/{parentId:guid}/sub-agents", async (
            Guid parentId, SubAgentRequest body, IDbContextFactory<SharePointIndexDbContext> factory,
            CancellationToken cancellationToken) =>
            await SaveAsync(parentId, null, body, factory, cancellationToken));

        app.MapPut("/api/agents/{parentId:guid}/sub-agents/{id:guid}", async (
            Guid parentId, Guid id, SubAgentRequest body, IDbContextFactory<SharePointIndexDbContext> factory,
            CancellationToken cancellationToken) =>
            await SaveAsync(parentId, id, body, factory, cancellationToken));

        app.MapDelete("/api/agents/{parentId:guid}/sub-agents/{id:guid}", async (
            Guid parentId, Guid id, IDbContextFactory<SharePointIndexDbContext> factory, CancellationToken cancellationToken) =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var entity = await db.SubAgentDefinitions.SingleOrDefaultAsync(x => x.Id == id && x.ParentAgentId == parentId, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound();
            }
            db.SubAgentDefinitions.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });
    }

    internal static async Task<IResult> SaveAsync(Guid parentId, Guid? id, SubAgentRequest body,
        IDbContextFactory<SharePointIndexDbContext> factory, CancellationToken cancellationToken)
    {
        var error = Validate(body);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }
        var tools = ChatAgentService.GetTools(true, true, true).Select(x => x.Name);
        var skills = await ChatAgentSkills.GetCatalogAsync(cancellationToken);
        if (body.Tools.Except(tools, StringComparer.Ordinal).Any() ||
            body.Skills.Except(skills.Select(x => x.Name), StringComparer.Ordinal).Any())
        {
            return Results.BadRequest(new { error = "Select tools and skills from the available catalog." });
        }
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.AgentDefinitions.AnyAsync(x => x.Id == parentId, cancellationToken))
        {
            return Results.NotFound();
        }
        var entity = id is null ? new SubAgentDefinitionEntity { ParentAgentId = parentId }
            : await db.SubAgentDefinitions.SingleOrDefaultAsync(x => x.Id == id && x.ParentAgentId == parentId, cancellationToken);
        if (entity is null)
        {
            return Results.NotFound();
        }
        entity.Name = body.Name.Trim();
        entity.Description = body.Description.Trim();
        entity.ModelId = body.ModelId.Trim();
        entity.Instructions = body.Instructions.Trim();
        entity.Tools = body.Tools.Distinct(StringComparer.Ordinal).ToArray();
        entity.Skills = body.Skills.Distinct(StringComparer.Ordinal).ToArray();
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (await db.SubAgentDefinitions.AnyAsync(x => x.ParentAgentId == parentId && x.Name == entity.Name && x.Id != entity.Id, cancellationToken))
        {
            return Results.Conflict(new { error = "A sub-agent with this name already exists for this parent." });
        }
        if (id is null)
        {
            db.SubAgentDefinitions.Add(entity);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            return Results.Conflict(new { error = "A sub-agent with this name already exists for this parent." });
        }
        return id is null ? Results.Created($"/api/agents/{parentId}/sub-agents/{entity.Id}", entity) : Results.Ok(entity);
    }

    public static string? Validate(SubAgentRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(body.Description) || body.Description.Length > 1000 ||
            string.IsNullOrWhiteSpace(body.ModelId) || body.ModelId.Length > 200 ||
            string.IsNullOrWhiteSpace(body.Instructions) || body.Instructions.Length > 50000)
        {
            return "Name (100), description (1000), model ID (200), and instructions (50000) are required and must fit their character limits.";
        }
        if (body.Tools is null || body.Skills is null || body.Tools.Length > 100 || body.Skills.Length > 100 ||
            body.Tools.Any(string.IsNullOrWhiteSpace) || body.Skills.Any(string.IsNullOrWhiteSpace))
        {
            return "Tools and skills must be arrays of catalog names (maximum 100 each).";
        }
        return null;
    }
}
