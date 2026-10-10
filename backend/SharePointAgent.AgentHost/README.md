# Local API or Foundry execution

Each chat workspace (or conversation outside one) chooses where its agent runs, under **Agent runs in** in the conversation's **Workspace details** panel. `Local` runs `ChatAgentService` in the API process; `Foundry` calls this separate host using Foundry's **Invocations protocol 2.0.0**. `ChatAgent:Mode` is only the default for scopes that have not chosen (`Local` unless configured). Foundry can be chosen whenever `ChatAgent:Foundry:Endpoint` is configured, and the API's own agent is always available. The choice is stored as `AgentMode` on the workspace or conversation row (`PUT /api/chat/conversations/{id}/session/agent-mode`); `RoutingChatAgentExecutor` and `RoutingAgentFileBrowser` send each turn and file request to the chosen agent. The Foundry session and the API's own environment are kept apart, so switching back finds each one's files. Both use the same agent, tools, context loader, and streaming writer.

## History and streaming

1. The API validates and saves the question, including attachment links, in SQL Server.
2. It invokes the executor with only `conversationId` and `questionId`.
3. The shared context loader reads the conversation, its agent/model/instructions, the question, and up to 40 preceding messages from SQL. It excludes the question itself and any later messages.
4. The agent emits status and text callbacks. The hosted adapter sends these immediately as NDJSON; the remote executor forwards them to the existing browser stream. Citations, token usage, and model ID travel in the final result.
5. The API alone persists the completed answer. Errors, cancellation, timeout, and a stream ending without `completed` do not save a successful answer. Invocations are not automatically retried because tools can modify files or upload them.

SQL remains the history authority in **both** modes. This does not use Responses-managed history or Foundry conversation storage. The frontend's `started`, `status`, `delta`, `completed`, and `error` events are unchanged.

The API stores `FoundryEndpoint` and `FoundrySessionId` and sends the session ID in the next invocation's query string. That preserves sandbox files across turns and API restarts. Changing the configured endpoint starts a new binding; deleting a Foundry session requires clearing its binding before further use. SQL history survives either operation, but unsaved sandbox edits do not transfer between local and Foundry execution or between different endpoints. Deletion removes the SQL binding, not the remote session; manage remote retention separately.

## Workspaces: one sandbox for several conversations

The sandbox file browser also supports create directory, binary upload, rename, recursive copy/delete, and move. The API forwards these using `x-client-agent-operation: manageFiles` and an `AgentFileChangeRequest` to the existing session, without invoking the model or creating a session. Deploy this host together with the API for file management support. The host applies the same filesystem checks and limits as Local mode: 5 MB uploads, 100 MB copies, 5,000 entries per operation, no replacement of existing destinations, and no paths outside the working directory or through symbolic links. Deletes are permanent. Changes affect every conversation sharing this session.

The binding lives on whichever row owns the sandbox. A conversation outside a workspace holds its own on `ChatConversations`, and a new branch of it starts unbound, as before. A conversation in a **workspace** uses the binding on `ChatWorkspaces` instead, so every conversation in that workspace opens onto the same files: a document downloaded in one is already there in the next, and a branch inherits the workspace rather than starting empty.

Membership is optional but **fixed**: it is chosen by the `workspaceId` on `POST /api/chat/conversations` and there is no endpoint that moves an existing conversation. A conversation therefore keeps one sandbox for its whole life, and the set of conversations sharing a sandbox only ever grows. To work in a different workspace, start a new conversation in it.

The one thing that ends a membership is deleting the workspace. That releases its conversations — their history is untouched and each falls back to a sandbox of its own, empty, since the shared binding went with the workspace. As for a deleted conversation, the remote session is not deleted.

## Workspace rules

A workspace can also carry **rules** — instructions every conversation in it works under. They are stored on the workspace and set through `name` and `instructions` on `POST` or `PUT /api/chat/workspaces`, up to 8000 characters; empty means none.

The shared context loader reads them each turn and appends them to the agent's own instructions under a `# Workspace rules` heading, naming the workspace and saying that where the two genuinely conflict the workspace rules are the narrower instruction and win. Both hosting modes use that loader, so `Local` and `Foundry` send the same system prompt. Because they are read per turn rather than copied at creation, editing them reaches conversations that already exist, from their next question; answers already given are not revisited.

Rules are a prompt, not a permission. They cannot widen what a conversation may reach: the search permission filter and the Graph credentials still decide that. They can be written by anyone who can create a workspace, which is any signed-in user for their own, so treat them as what that user could have typed into the chat themselves rather than as a control over them.

`GET /api/chat/conversations/{id}/files?path=&recursive=` lists the agent's working directory **without running a turn**. In `Local` mode the API reads its own disk. In `Foundry` mode that disk belongs to the sandbox, so the API posts to the same Invocations endpoint with the session's `agent_session_id`, and names what it wants in the **`x-client-agent-operation: listFiles`** request header. The host reads that header before the body and answers straight from disk: no model request, no token usage, no history written. A request with no such header is a chat turn, which is what every earlier caller sends.

The header is the only thing that tells the two apart, so the deployment has to forward it. The hosted agent platform only forwards custom request headers prefixed `x-client-`; anything else is stripped before it reaches the container, which is why this header carries that prefix rather than a plainer name. A gateway that strips it would turn each listing into a turn with no `questionId`, which the host refuses with HTTP 400 rather than running anything. A conversation with no session yet returns `sandboxStarted: false` rather than an empty directory, because asking would make Foundry start a sandbox the conversation has never run in.

`GET /api/chat/conversations/{id}/files/content?path=&download=` returns one file the same way, under `x-client-agent-operation: readFile`. The sandbox works the content type out from the name and sends the bytes; `LocalWorkingDirectory:Downloads:MaxFileBytes` caps what can come back, so a reader can fetch anything the agent was able to fetch and nothing larger. The API serves inline only the types that cannot carry script — images other than SVG, PDF, plain text, Markdown, CSV, JSON — and hands everything else over as a download, with `X-Content-Type-Options: nosniff` either way, because these bytes are whatever an author or the agent put there and they are served from the API's own origin.

`GET /api/chat/conversations/{id}/session` reports the binding without running anything: the execution mode, whether a workspace or the conversation holds it, how many conversations share it, and the session ID. It also reports whether the recorded endpoint is the one configured now — a binding made against another endpoint is not sent back, so the next turn silently starts a fresh sandbox, and this is where that shows. The endpoints themselves are returned only to an administration reader; the session ID belongs to the conversation. The chat header's workspace badge opens onto the same information.

Two conversations in the same unbound workspace whose first turns run at once would each be handed a different sandbox. The second save is rejected rather than silently splitting the workspace's files, so that turn fails and can be retried against the sandbox the first one established. Apply `AddChatWorkspaces` with the other migrations before using this.

## Run in the API

Keep the existing API configuration and set:

```json
{ "ChatAgent": { "Mode": "Local" } }
```

No Foundry configuration or host process is needed. The API needs its existing model, SQL, search, Graph, and attachment settings.

## Run in Foundry

The repository's [deployment workflow](../../.github/workflows/infra.yml) now provisions Azure SQL and a Foundry project, builds this image, registers a hosted agent version, grants its SQL/resource access, and connects the API endpoint. See the [deployment guide](../../infra/README.md) for environment variables and instructions. The manual steps below remain useful for an existing Foundry project.

Apply `AddFoundrySessionBinding` through the existing API migration flow or your database deployment pipeline before enabling this mode. The host defaults to `SqlServer:AutoMigrate=false`; provisioning a sandbox should not run schema migrations.

Configure the API:

```json
{
  "ChatAgent": {
    "Mode": "Foundry",
    "Foundry": {
      "Endpoint": "https://ACCOUNT.services.ai.azure.com/api/projects/PROJECT/agents/AGENT/endpoint/protocols/invocations?api-version=v1",
      "TimeoutSeconds": 600,
      "AllowUnauthenticatedLocalhost": false,
      "ManagedIdentityClientId": null
    }
  }
}
```

The API uses `DefaultAzureCredential` and the `https://ai.azure.com/.default` scope. Grant its identity permission to invoke the endpoint. `ManagedIdentityClientId` is optional for a user-assigned identity. Use the default **Entra** endpoint authorization scheme; header-based isolation is not configured by this client. Session IDs are managed by the application and must not be included in the configured URL.

Configure the hosted container with environment variables or a secret-backed configuration provider:

| Settings | Purpose |
| --- | --- |
| `SqlServer__ConnectionString` | The **same database** used by the API, reachable from the sandbox. A developer's LocalDB is not reachable from Azure. |
| `SharePoint__TenantId`, `SharePoint__ClientId`, `SharePoint__ClientSecret`, `SharePoint__SiteHostname`, `SharePoint__SitePath`, `SharePoint__DocumentLibraryName` | Existing Graph document access settings. Webhook and Service Bus settings are not needed. |
| `AzureOpenAI__Endpoint`, `AzureOpenAI__EmbeddingDeployment`, `AzureOpenAI__ChatDeployment` | Existing model settings. Persisted agent definitions select the chat model. Use the resource root endpoint, not `/openai/v1` or the project URL. |
| `AzureSearch__Endpoint`, `AzureSearch__SharePointIndexName`, `AzureSearch__UploadIndexName`, `AzureSearch__VectorDimensions` | The same indexes and embedding dimensions as the API. |
| `Uploads__ServiceUri`, `Uploads__ContainerName` | Existing attachment blob storage. |
| `MarkItDown__Endpoint` | Existing attachment service dependency, reachable from the sandbox. |
| `LocalWorkingDirectory__Directory` | Optional. The agent's working directory; defaults to `$HOME/sharepoint-agent`, which the session keeps between turns. Set it only for another persistent path. Renamed from `Downloads__Directory`, with `MaxFileBytes` now at `LocalWorkingDirectory__Downloads__MaxFileBytes`. |
| `MarkItDown__ApiKey` | Converter authentication key; the deployment resolves this from its Foundry secret connection. |
| `ContentSafety__Enabled`, `ContentSafety__Endpoint`, `ContentSafety__UseManagedIdentity` | Content Safety configuration; the hosted agent identity needs Cognitive Services User on that resource. |

The host defaults Azure OpenAI, Search, and Blob Storage to managed identity. Grant the hosted identity access to those resources and SQL, or use the existing `UsedManagedIdentity=false` and API-key/connection-string options. Graph continues to use application credentials. `LocalWorkingDirectory:Directory` defaults to `$HOME/sharepoint-agent` in this host so Foundry can persist the agent's working directory between turns, with fetched files cached in its `Downloads/SharePoint` and `Downloads/Attachments` folders; leave it unset unless using another persistent session path.

Build from the `SharePointAgent` directory:

```powershell
docker build -f backend/SharePointAgent.AgentHost/Dockerfile -t YOUR_REGISTRY.azurecr.io/sharepoint-agent:YOUR_TAG .
docker push YOUR_REGISTRY.azurecr.io/sharepoint-agent:YOUR_TAG
```

Deploy the image as a Foundry Hosted Agent configured for `invocations`, protocol version `2.0.0`, and the environment settings above. The SDK supplies port **8088**, **GET /readiness**, **POST /invocations**, platform context, and the session response header. Keep this container behind Foundry's authenticated endpoint; the internal handler trusts its authorized caller's database identifiers. See Microsoft's [deployment guide](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent) and [Invocations session contract](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-sessions).

## Exercise the remote path locally

Configure the host with its own user secrets or environment variables for the same resources as the API. Environment variables use `__` instead of `:`. For locally unavailable managed identity endpoints, use the supported key/connection-string settings.

```powershell
dotnet run --project backend/SharePointAgent.AgentHost
```

In the API's environment, set:

```powershell
$env:ChatAgent__Mode = 'Foundry'
$env:ChatAgent__Foundry__Endpoint = 'http://localhost:8088/invocations'
$env:ChatAgent__Foundry__AllowUnauthenticatedLocalhost = 'true'
dotnet run --project backend/SharePointAgent.Api
```

The unauthenticated option is restricted to loopback. The same browser UI now exercises the network path. When testing multiple conversations in one local host process, files share the local directory; only Foundry deployment supplies per-session sandbox isolation.

## Verification

```powershell
dotnet build backend/SharePointAgent.slnx
dotnet test backend/SharePointAgent.Tests
```

Tests cover history boundaries and attachment references, workspace rules reaching the composed prompt and clearing again, migration/model consistency, mode selection, endpoint validation, authentication, sandbox reuse within and outside a workspace, moving a conversation between the two, workspace deletion releasing its conversations, concurrent writes, early status delivery through the real Invocations adapter, metadata, cancellation, errors, and truncated streams. They use fake model/tool execution and do not require Azure credentials or modify SQL data. A deployed smoke test should additionally verify cloud permissions, networking, model calls, and edit/upload continuity across two turns.
