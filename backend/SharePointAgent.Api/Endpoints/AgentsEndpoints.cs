using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class AgentsEndpoints
{
    public static void MapAgentsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/agents/capabilities", async (CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                tools = ChatAgentService.GetTools(),
                skills = await ChatAgentSkills.GetCatalogAsync(cancellationToken)
            }));

        // Persisted agent definitions. The default instruction text is exposed by the chat service so the
        // editor and server-side creation fallback always use the same private template.
        app.MapGet("/api/agents/default-instructions", (
            IOptions<OpenAiOptions> openAiOptions,
            IOptions<ChatAgentHostingOptions> hostingOptions) =>
            Results.Ok(new
            {
                instructions = AgentDefaults.Instructions,
                modelId = openAiOptions.Value.ChatDeployment,
                mode = hostingOptions.Value.Mode.ToString(),
                endpoint = hostingOptions.Value.Mode == ChatAgentExecutionMode.Foundry
                    ? hostingOptions.Value.Foundry.Endpoint
                    : openAiOptions.Value.Endpoint,
            }));

        app.MapGet("/api/agents", (IAgentRepository store, CancellationToken cancellationToken) =>
            store.ListAsync(cancellationToken));

        app.MapGet("/api/agents/{id:guid}", async (
            Guid id,
            IAgentRepository store,
            CancellationToken cancellationToken) =>
        {
            var agent = await store.GetAsync(id, cancellationToken);
            return agent is null ? Results.NotFound() : Results.Ok(agent);
        });

        app.MapPost("/api/agents", async (
            AgentDefinitionRequest? body,
            IAgentRepository store,
            IOptions<OpenAiOptions> openAiOptions,
            CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim() ?? "";
            var modelId = string.IsNullOrWhiteSpace(body?.ModelId)
                ? openAiOptions.Value.ChatDeployment
                : body.ModelId.Trim();
            var instructions = string.IsNullOrWhiteSpace(body?.Instructions)
                ? AgentDefaults.Instructions
                : body.Instructions.Trim();
            var error = ValidateAgentDefinition(name, modelId, instructions);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                var created = await store.CreateAsync(name, modelId, instructions, cancellationToken);
                return Results.Created($"/api/agents/{created.Id}", created);
            }
            catch (AgentNameConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapPut("/api/agents/{id:guid}", async (
            Guid id,
            AgentDefinitionRequest? body,
            IAgentRepository store,
            CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim() ?? "";
            var modelId = body?.ModelId?.Trim() ?? "";
            var instructions = body?.Instructions?.Trim() ?? "";
            var error = ValidateAgentDefinition(name, modelId, instructions);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                var updated = await store.UpdateAsync(id, name, modelId, instructions, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (AgentNameConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (DefaultAgentNameChangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static string? ValidateAgentDefinition(string name, string modelId, string instructions)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A non-empty 'name' is required.";
        }

        if (name.Length > 100)
        {
            return "'name' cannot exceed 100 characters.";
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return "A non-empty 'modelId' is required.";
        }

        if (modelId.Length > 200)
        {
            return "'modelId' cannot exceed 200 characters.";
        }

        return string.IsNullOrWhiteSpace(instructions) ? "Non-empty 'instructions' are required." : null;
    }
}
