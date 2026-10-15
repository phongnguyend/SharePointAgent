using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class AgentsEndpoints
{
    public static void MapAgentsEndpoints(this WebApplication app)
    {
        // Script execution is listed only where the API runs the agent itself and an isolated workspace mode
        // can be chosen, because only a conversation in such a mode is offered the tool.
        app.MapGet("/api/agents/capabilities", async (
            IServiceProvider services,
            AgentSkillStore skillStore,
            Guid? agentId, Guid? subAgentId,
            CancellationToken cancellationToken) =>
        {
            if (!await skillStore.ScopeExistsAsync(agentId, subAgentId, cancellationToken))
            {
                return Results.NotFound();
            }
            return Results.Ok(new
            {
                tools = ChatAgentService.GetTools(services.GetService<IAgentWorkspaceProvider>() is { } workspaces
                    && workspaces.AvailableModes.Any(mode => mode != AgentWorkspaceMode.Local),
                    services.GetService<AgentDocumentOutlines>() is not null,
                    services.GetService<IGraphRetrievalService>() is { ReturnsResults: true }),
                skills = await skillStore.ListAsync(AgentSkillStore.Scope(agentId, subAgentId), cancellationToken)
            });
        });

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
            AgentSkillStore skillStore,
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
            var error = ValidateAgentDefinition(name, modelId, instructions)
                ?? await ValidateCapabilitiesAsync(body?.Tools, body?.Skills, skillStore, "global", cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                var created = await store.CreateAsync(name, modelId, instructions, body?.Tools, body?.Skills, cancellationToken);
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
            AgentSkillStore skillStore,
            CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim() ?? "";
            var modelId = body?.ModelId?.Trim() ?? "";
            var instructions = body?.Instructions?.Trim() ?? "";
            var error = ValidateAgentDefinition(name, modelId, instructions)
                ?? await ValidateCapabilitiesAsync(body?.Tools, body?.Skills, skillStore, AgentSkillStore.Scope(id, null), cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                var updated = await store.UpdateAsync(id, name, modelId, instructions, body?.Tools, body?.Skills, cancellationToken);
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

    internal static async Task<string?> ValidateCapabilitiesAsync(string[]? tools, string[]? skills, AgentSkillStore skillStore, string scope, CancellationToken cancellationToken)
    {
        if (tools is { Length: > 100 } || skills is { Length: > 100 } ||
            tools?.Any(string.IsNullOrWhiteSpace) == true || skills?.Any(string.IsNullOrWhiteSpace) == true)
        {
            return "Tools and skills must contain at most 100 non-empty catalog names.";
        }
        if (tools is not null && tools.Except(ChatAgentService.GetTools(true, true, true).Select(x => x.Name), StringComparer.Ordinal).Any())
        {
            return "Select tools from the available catalog.";
        }
        if (skills is { Length: > 0 })
        {
            var catalog = await skillStore.ListAsync(scope, cancellationToken);
            if (skills.Except(catalog.Select(x => x.Name), StringComparer.Ordinal).Any())
            {
                return "Select skills from the available catalog.";
            }
        }
        return null;
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
