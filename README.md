# SharePointAgent

This .NET 10 solution keeps a permission-aware Azure AI Search vector index synchronized with a SharePoint document library.

## Components

- `SharePointAgent.Api` exposes `POST /api/sharepoint/webhook`, completes Microsoft Graph's validation handshake, validates `clientState`, and publishes change signals to an Azure Service Bus topic. It also serves search, state, and checkpoint management endpoints for the front end.
- `frontend` is a React and Vite app for viewing the worker's SQL Server state and running the three retrieval strategies against the index. See [frontend/README.md](frontend/README.md).
- `SharePointAgent.Background` consumes a topic subscription. It follows the Microsoft Graph drive delta feed and, for each file the feed returns, compares it against the metadata recorded for the last indexing run in SQL Server: an unchanged file is left alone, a renamed or re-shared file has its metadata refreshed in place, and only a file whose content actually changed is downloaded, extracted, chunked, embedded, and replaced. Deleted files have all chunks removed. Two more hosted services run alongside it: one creates the Graph subscription and renews it before expiration (`SharePoint:SubscriptionRenewalEnabled` to turn it off), and one runs the same delta synchronization every `Processor:ScheduledSyncMinutes` (5 by default, `ScheduledSyncEnabled` to turn it off) so missed notifications still get picked up. Either trigger can run without the other: disable the subscription to poll only, or disable the schedule to react only to notifications. All three triggers — notification, schedule, and startup sync — are serialized, so only one delta pass runs at a time.
- `SharePointAgent.AgentHost` runs the same chat agent as a Foundry Hosted Agent, behind an `/invocations` endpoint the API streams from. See [agent hosting options](backend/SharePointAgent.AgentHost/README.md).
- `SharePointAgent.AspireAppHost` runs the API, the worker, and the agent host together against a containerized SQL Server, with the .NET Aspire dashboard over the three of them.

The work behind those hosts is split into four layers, each its own project:

| Project | Holds | References |
| --- | --- | --- |
| `SharePointAgent.Domain` | The records, enums, and exceptions the rest is written in terms of — drive items, chunks, conversations, agents, subscriptions — plus pure helpers such as `TextChunker` and `SearchChunkKey`. No package references. | — |
| `SharePointAgent.Application` | The contracts the outer layers implement. `Repositories.cs` holds the application's view of its own database — one interface per aggregate (`IChatRepository`, `IAgentRepository`, `IFileMetadataRepository`, `IDeltaStateRepository`, `IIndexStateRepository`, …); `IndexingAbstractions.cs` holds the abstractions over other people's services (`ISearchIndexStore`, `ISearchQueryStore`, `IChangeSignalPublisher`, `ISharePointChangeProcessor`, `IContentExtractor`). Plus the settings classes and orchestration that needs nothing more than those contracts. No package references. | Domain |
| `SharePointAgent.Persistence` | The Entity Framework Core model and its migrations, with one repository per contract in `Repositories/`. `AddPersistence` registers the pooled context and maps every repository interface onto its implementation, so no other project names an `Ef`-anything. | Application |
| `SharePointAgent.Infrastructure` | Everything outside the process: the Microsoft Graph .NET SDK for subscriptions, delta tracking, downloads, and permissions; Service Bus, Azure AI Search, Azure OpenAI and the chat agent, Blob Storage, Document Intelligence, MarkItDown, and the officecli MCP server. Also the composition root the hosts call into. | Application, Persistence |

Embeddings go through `Microsoft.Extensions.AI`'s `IEmbeddingGenerator<string, Embedding<float>>`, backed by `AzureOpenAIClient` from the Azure OpenAI SDK, so the embedding model can be swapped without touching the indexing or query code.

The webhook is intentionally only a signal. Microsoft Graph drive notifications do not contain a complete, durable list of item-level changes. A delta link is checkpointed in SQL Server only after every returned page is indexed successfully, making retries idempotent and allowing expired delta tokens to trigger a full reconciliation. Those reconciliations are why file metadata is tracked in the same database: the delta feed then returns every file in the library, and without a record of what was already indexed each one would be extracted and embedded again. See [Worker state in SQL Server](#worker-state-in-sql-server).

## Prerequisites

Create these resources before deploying:

1. An Azure Service Bus namespace with the configured topic and subscription.
2. A SQL Server database reachable at `SqlServer:ConnectionString`, holding the worker's delta checkpoint and indexed-file metadata. Azure SQL Database, SQL Server, or SQL Server in a container all work; the schema is applied by an Entity Framework Core migration as the application starts.
3. Azure AI Search and an Azure OpenAI embedding deployment. The search index is created or updated automatically.
4. Azure Blob Storage for chat attachments. The Bicep templates create a private `chat-uploads` container.
5. A MarkItDown service reachable at `MarkItDown:Endpoint`, which converts SharePoint Office files and all chat attachments to markdown.
6. Azure AI Document Intelligence, unless the SharePoint allow list stays within the formats MarkItDown handles directly. Every other SharePoint format — PDF and images, for example — needs Document Intelligence, or it is indexed using metadata text only.
7. An Entra application or managed identity with Microsoft Graph application access to the target site/drive. Prefer `Sites.Selected` with an explicit grant to the site; `Sites.Read.All` is the broader alternative. Admin consent is required. Read access covers everything but the chat assistant's [`upload_file`](#uploading-a-file-back) tool, which needs a `write` grant (or `Sites.ReadWrite.All`) — grant it only if the assistant should be able to replace documents.
8. A public HTTPS URL for the API. Microsoft Graph must be able to call it during subscription creation. Not needed when `SharePoint:SubscriptionRenewalEnabled` is `false` and the worker polls on its schedule alone.
9. `@officecli/officecli` on the API's host, only for the chat assistant's editing tools. See [Editing a file with officecli](#editing-a-file-with-officecli); set `OfficeCli:Enabled` to `false` where it is not installed.

Assign Azure RBAC appropriate to each process: Service Bus Data Sender, Storage Blob Data Contributor, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User to the API; Service Bus Data Receiver, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User to the worker. Add Cognitive Services User when Document Intelligence is enabled. The Bicep templates create these assignments. SQL Server permissions are granted inside the database rather than through RBAC: see [Worker state in SQL Server](#worker-state-in-sql-server).

Neither the SQL Server nor the MarkItDown service is deployed by the Bicep templates. Provision the database separately and pass its connection string to the worker as a secret.

Environment parameter files for dev, test, and local Azure resources are available in [`infra/`](infra/README.md), with deployment commands and local setup notes.

The manual [Deploy infrastructure workflow](.github/workflows/infra.yml) deploys these environments using Azure OIDC authentication. See the [infrastructure guide](infra/README.md) for GitHub environment secrets, variables, and optional Container App bootstrapping.

Infrastructure is split into two deployments. `main.bicep` deploys the shared Azure services, Azure Container Registry, Log Analytics, and the Container Apps environment. It does not deploy the SQL Server:

```powershell
$resourceGroup = '<resource-group>'
$location = '<azure-region>'
$workloadName = 'sharepointagent'
$environmentName = 'dev'
$deployDocumentIntelligence = 'false'
$imageTag = 'v1'

$apiImageRepository = 'sharepoint-api'
$workerImageRepository = 'sharepoint-worker'
$serviceBusTopicName = 'sharepoint-changes'
$serviceBusSubscriptionName = 'search-indexer'
$sharePointIndexName = 'sharepoint-files'
$vectorDimensions = 1536
$embeddingDeploymentName = 'text-embedding-3-small'

$apiTargetPort = 8080
$apiCpu = 0.5
$apiMemory = '1Gi'
$apiMinReplicas = 1
$apiMaxReplicas = 3
$workerCpu = 1.0
$workerMemory = '2Gi'
$workerMinReplicas = 1
$workerMaxReplicas = 1

$deployment = az deployment group create `
  --resource-group $resourceGroup `
  --template-file infra/main.bicep `
  --parameters `
    workloadName=$workloadName `
    environmentName=$environmentName `
    location=$location `
    deployDocumentIntelligence=$deployDocumentIntelligence `
    serviceBusTopicName=$serviceBusTopicName `
    serviceBusSubscriptionName=$serviceBusSubscriptionName `
    embeddingDeploymentName=$embeddingDeploymentName | ConvertFrom-Json

$registry = $deployment.properties.outputs.containerRegistryName.value
```

After that deployment succeeds, run `container-apps.bicep` once to create the Container App shells, managed identities, ACR pull access, and service role assignments against the existing resources:

```powershell
$appsDeployment = az deployment group create `
  --resource-group $resourceGroup `
  --template-file infra/container-apps.bicep `
  --parameters `
    workloadName=$workloadName `
    environmentName=$environmentName `
    location=$location `
    deployDocumentIntelligence=$deployDocumentIntelligence | ConvertFrom-Json

$apiApp = $appsDeployment.properties.outputs.apiContainerAppName.value
$workerApp = $appsDeployment.properties.outputs.workerContainerAppName.value
```

Pass the same `deployDocumentIntelligence=true` value to both deployments when Document Intelligence is required. The Container Apps are created at zero scale with Microsoft's public quickstart placeholder. Do not routinely rerun `container-apps.bicep` after releasing application revisions because it declares the placeholder as its initial desired image. `main.bicep` can be rerun independently without changing the Container Apps.

Bicep does not deploy this project's images, SharePoint settings, or application secrets. Build the application images separately:

```powershell
az acr build --registry $registry --image "${apiImageRepository}:$imageTag" `
  --file backend/SharePointAgent.Api/Dockerfile .

az acr build --registry $registry --image "${workerImageRepository}:$imageTag" `
  --file backend/SharePointAgent.Background/Dockerfile .
```

Configure the application secrets and environment variables first, either in the same release pipeline or with `az containerapp secret set` and `az containerapp update --set-env-vars`. Then deploy the newly built images and activate the application replicas:

```powershell
$registryServer = $deployment.properties.outputs.containerRegistryLoginServer.value

az containerapp update `
  --resource-group $resourceGroup `
  --name $apiApp `
  --image "${registryServer}/${apiImageRepository}:$imageTag" `
  --set-env-vars `
    "ServiceBus__TopicName=$serviceBusTopicName" `
    "ServiceBus__SubscriptionName=$serviceBusSubscriptionName" `
  --cpu $apiCpu `
  --memory $apiMemory `
  --min-replicas $apiMinReplicas `
  --max-replicas $apiMaxReplicas

az containerapp ingress update `
  --resource-group $resourceGroup `
  --name $apiApp `
  --target-port $apiTargetPort

az containerapp update `
  --resource-group $resourceGroup `
  --name $workerApp `
  --image "${registryServer}/${workerImageRepository}:$imageTag" `
  --set-env-vars `
    "ServiceBus__TopicName=$serviceBusTopicName" `
    "ServiceBus__SubscriptionName=$serviceBusSubscriptionName" `
    "AzureSearch__SharePointIndexName=$sharePointIndexName" `
    "AzureSearch__VectorDimensions=$vectorDimensions" `
    "AzureOpenAI__EmbeddingDeployment=$embeddingDeploymentName" `
  --cpu $workerCpu `
  --memory $workerMemory `
  --min-replicas $workerMinReplicas `
  --max-replicas $workerMaxReplicas
```

`container-apps.bicep` configures `UsedManagedIdentity=true` and discoverable Azure service endpoints. The release pipeline supplies topic, subscription, index, vector-dimension, and model-deployment settings alongside the SharePoint settings, the `SqlServer__ConnectionString` secret, and the other application secrets.

The API and worker receive separate system-assigned identities. Bicep grants the API Service Bus Data Sender and grants the worker Service Bus Data Receiver, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User. SQL Server access is not an RBAC grant; the worker's identity is added inside the database instead. A separate user-assigned identity has only `AcrPull` and is attached to both Container Apps for private image retrieval. Set `deployDocumentIntelligence=true` to include Document Intelligence and its worker role assignment.

Local/key authentication is enabled by default so services can still use their `UsedManagedIdentity: false` fallback. Disable the corresponding `allow*LocalAuth` or `allow*ApiKeyAuth` parameters for managed-identity-only deployments. The templates output endpoints, app URLs, registry details, and identity object IDs, but deliberately do not output connection strings or keys.

## Configure and run

Replace the placeholders in both `appsettings.json` files or use environment variables (recommended in deployments), for example:

```text
SharePoint__SiteHostname
SharePoint__SitePath
SharePoint__DocumentLibraryName
SharePoint__TenantId
SharePoint__ClientId
SharePoint__ClientSecret
SharePoint__NotificationUrl
SharePoint__ClientState
ServiceBus__Enabled
ServiceBus__UsedManagedIdentity
ServiceBus__FullyQualifiedNamespace
ServiceBus__ConnectionString
SqlServer__ConnectionString
SqlServer__AutoMigrate
AzureSearch__UsedManagedIdentity
AzureSearch__Endpoint
AzureSearch__ApiKey
AzureSearch__SharePointIndexName
AzureSearch__UploadIndexName
AzureOpenAI__UsedManagedIdentity
AzureOpenAI__Endpoint
AzureOpenAI__EmbeddingDeployment
DocumentIntelligence__UsedManagedIdentity
MarkItDown__Endpoint
Uploads__UsedManagedIdentity
Uploads__ConnectionString
Uploads__ServiceUri
Uploads__ContainerName
Downloads__Directory
OfficeCli__Enabled
OfficeCli__Command
```

Microsoft Graph authentication uses the SharePoint `TenantId`, `ClientId`, and `ClientSecret` settings. Store `ClientSecret` in user secrets, environment variables, or a secret store rather than committing a real value to `appsettings.json`. The Entra application needs Microsoft Graph application permissions for the target SharePoint site or drive, with admin consent.

### Entra ID sign-in

The frontend and API reuse **`SharePoint:TenantId` and `SharePoint:ClientId`**. No separate frontend client ID or tenant setting is needed: `/api/auth/config` returns those public identifiers and the API scope. The client secret stays on the server for existing app-only Graph operations and is never returned to the browser.

Configure the existing app registration before signing in:

1. In **Microsoft Entra ID → App registrations**, open the application matching `SharePoint:ClientId`. Use **Accounts in this organizational directory only**.
2. Under **Authentication → Add a platform → Single-page application**, register `http://localhost:5173/auth-redirect.html` for development and `https://<frontend-host>/auth-redirect.html` for production. If using Vite preview, also register `http://localhost:4173/auth-redirect.html`. Use the **SPA** platform, not Web. Leave implicit grants disabled; MSAL uses authorization code with PKCE.
3. Under **Expose an API**, set the Application ID URI to `api://<ClientId>` and add the enabled delegated scope **`access_as_user`** (admin consent). Authorize this same client application to request the scope and grant tenant admin consent. This is the app's own API scope, separate from its Microsoft Graph application permissions.
4. In the app manifest, set **`api.requestedAccessTokenVersion` to `2`**. The API validates the tenant's v2 issuer, client-ID audience, token signature and expiry, tenant, calling client, user object ID, and exact `access_as_user` scope. Graph tokens and ID tokens cannot be used as API credentials.
5. Optionally restrict tenant sign-in under **Enterprise applications → this application → Properties → Assignment required? → Yes**, then assign approved users under **Users and groups**. Application roles are managed separately in the app's **Users** page.
6. Under **API permissions → Add a permission → Microsoft Graph → Application permissions**, select **`User.Read.All`**, then **Add permissions**. Have a tenant administrator select **Grant admin consent** and confirm the status shows **Granted for your tenant**. The backend uses its client credentials to look up the signed-in user's directory email for account linking; delegated `User.Read` or `User.Read.All` does not authorize this app-only call.
7. Restart the API to obtain a fresh Graph token, then start or reload the frontend. Choose **Sign in with your organization**; the header shows the signed-in account and **Sign out**.

All UI API routes require a bearer access token. Only `/health`, `/api/auth/config`, and `/api/sharepoint/webhook` are anonymous; the webhook retains its existing subscription `clientState` checks. MSAL obtains and renews tokens, including for uploads, downloads, previews, and streamed chat. Expired sessions requiring interaction return to the sign-in screen. Browser tokens use session storage. Serve both apps over HTTPS outside localhost and deploy `auth-redirect.html` with the frontend assets; it is a dedicated MSAL redirect bridge, not a React route.

Application users and roles are stored through EF Core Identity in SQL Server. These are **in-app roles**, independent of Entra directory roles. Entra remains the sign-in provider; no application passwords are created.

| Application role | Access |
| --- | --- |
| Global Admin | Manage users, roles assigned to users, and all application settings/content. |
| Global Reader Admin | Read all administration pages, conversations, and uploads; search, without making changes. |
| User | Search with their own SharePoint principals, chat, and manage their own conversations/uploads. |

Before first sign-in, configure at least one initial administrator using the API's `AppIdentity:BootstrapAdminEmails` array. For local development:

```powershell
dotnet user-secrets set "AppIdentity:BootstrapAdminEmails:0" "admin@your-tenant.example" --project backend/SharePointAgent.Api
```

Use the account's primary Microsoft Graph `mail` address (or `userPrincipalName` when `mail` is empty). Grant **Microsoft Graph → Application → `User.Read.All`** with admin consent so the API can verify this address. The API matches a pre-created email case-insensitively on first sign-in, preserves its assigned role, and binds it to the immutable Entra tenant/object IDs. An unknown email creates a **User** account. Bootstrap configuration creates missing admins only; it does not promote or reactivate existing accounts. Configure it before users start signing in.

The **Users** page lets Global Admin create accounts ahead of time, select one or more application roles using checkboxes, change display names, and disable access. Permissions from selected roles are combined: **Global Reader Admin + User** can read administration pages and manage their own conversations/uploads, while **Global Admin** grants full management access. Reader access never grants permission to modify another user's content. At least one role is required. The API accepts and returns a `roles` array; existing assignments remain in Identity's user-role table without a schema migration. Linked email addresses cannot be changed there, and the last active Global Admin cannot be disabled or have that role removed. No invitation is sent. Roles and active status are checked on each API request; changing Entra roles does not change application roles.

Startup migrations add the Identity tables and nullable `CreatedById` references on conversations and attachment files. Existing records without a creator remain visible to administrators only. When `SqlServer:AutoMigrate` is disabled, apply the EF migrations before starting the API.

Admin roles retain the operator-selected `userId` search filter. For **User**, the API forces the signed-in Entra object ID for search/chat, checks conversation and upload ownership using `CreatedById`, and checks SharePoint principals before serving indexed-file previews. Background indexing and Graph decryption continue using the app identity. See Microsoft's [SPA/API registration guidance](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-web-api-dotnet-protect-app) and [access token version guidance](https://learn.microsoft.com/en-us/entra/identity-platform/access-tokens).

### Application service permissions

For sign-in and application account linking, grant **Microsoft Graph → Application permissions → `User.Read.All`** with tenant admin consent on the app registration matching `SharePoint:ClientId`. The API reads the user's directory profile before linking a pre-created application account or creating a new one. See Microsoft's [app-only Graph authentication guidance](https://learn.microsoft.com/en-us/graph/auth-v2-service?tabs=http).

For live sensitivity label names in the UI, add **API permissions → Add a permission → Microsoft Graph → Application permissions → SensitivityLabels.Read.All** to the same client application and select **Grant admin consent**. This permission reads the tenant label catalog; it does not authorize document decryption. See the [Microsoft Graph sensitivity label permissions](https://learn.microsoft.com/en-us/graph/api/tenantdatasecurityandgovernance-list-sensitivitylabels?view=graph-rest-1.0).

To process documents encrypted by sensitivity labels, configure the same client application with Azure Rights Management permission as well:

1. In **Microsoft Entra ID → App registrations**, open the application matching `SharePoint:ClientId`.
2. Select **API permissions → Add a permission → APIs my organization uses → Azure Rights Management Service**.
3. Choose **Application permissions → Content.SuperUser**, then **Add permissions**.
4. Have a tenant administrator select **Grant admin consent** for the tenant protecting the documents.
5. Restart the API, background worker, and agent host to refresh their credentials before retrying.

| API | Permission | Type | Scope | Admin consent |
| --- | --- | --- | --- | --- |
| Microsoft Graph | `User.Read.All` | Application | Verify directory email for account linking and resolve user profiles for permission-aware queries | Required |
| Microsoft Graph | `SensitivityLabels.Read.All` | Application | Read the tenant sensitivity label catalog for live UI display names | Required |
| Azure Rights Management Service | `Content.SuperUser` | Application | Read all protected content for this tenant | Required |

`Content.SuperUser` grants tenant-wide access to protected content, beyond the application's Graph site permissions. Grant it only to an application approved for that scope. It enables the current app-only decryption flow for indexing/reindexing, View Markdown, and agent downloads; Graph permissions alone do not authorize decryption. Documents without encryption do not require this additional permission. See the [Microsoft MIP permission reference](https://learn.microsoft.com/en-us/information-protection/develop/concept-api-permissions).

**Troubleshooting: “Cannot verify the user with Microsoft Graph. Configure User.Read.All application permission with admin consent.”**

This message means Graph returned HTTP 401 or 403 during the directory lookup. Check that `User.Read.All` is listed as **Application**, with consent granted in the tenant configured by `SharePoint:TenantId`, on the application matching the API's effective `SharePoint:ClientId`. Check environment variables and user secrets if they override `appsettings.json`. After granting consent, restart the API to refresh its cached Graph token and retry sign-in. An in-app **Global Admin** role does not grant Microsoft Graph permissions. If the error persists after these checks, inspect the Graph authorization failure; the message alone does not prove that missing consent is the cause.

`AzureOpenAI:Endpoint` takes the resource endpoint with no API path, such as `https://<resource>.openai.azure.com` or `https://<resource>.services.ai.azure.com`. The SDK appends `/openai/deployments/<deployment>/embeddings` itself, so the OpenAI-compatible base URL that the Foundry portal also offers — the same host with `/openai/v1` appended — would be doubled into a path that returns 404 on every embedding request. Startup validation rejects an endpoint that carries a path rather than letting it fail per request.

Each Azure service has its own `UsedManagedIdentity` setting. Set it to `true` to use the host's system-assigned managed identity. Set it to `false` to use `ConnectionString` for Service Bus, or `ApiKey` for Azure AI Search, Azure OpenAI, and Document Intelligence. SQL Server is the exception: it has no such flag, because the choice belongs in `SqlServer:ConnectionString` itself. Store connection strings and keys in user secrets, environment variables, or a secret store rather than in `appsettings.json`.

```powershell
dotnet restore
dotnet run --project backend/SharePointAgent.Api
dotnet run --project backend/SharePointAgent.Background
```

Or start the API, the worker, and the agent host together behind the .NET Aspire dashboard. The AppHost also runs SQL Server in a container and hands each process its connection string as `SqlServer__ConnectionString`, so no local database has to be provisioned first; every other setting still comes from each project's `appsettings.json` and user secrets. It needs a container runtime.

```powershell
dotnet run --project backend/SharePointAgent.AspireAppHost
```

The SQL Server container is declared with a persistent lifetime, so the indexed library survives between debugging sessions rather than being rebuilt on every run.

`Processor:SyncOnStartup` defaults to `true`, so existing documents are indexed immediately rather than waiting for the next webhook or scheduled tick. Both the change signal listener and the scheduled synchronization honour it, so the startup pass happens whichever trigger is enabled. When both are enabled the second request is a no-op: passes are serialized, and the first one has already advanced the delta checkpoint. Service Bus notifications after that advance the checkpoint further.

`Processor:ChangeSignalListenerEnabled` defaults to `true`. Set it to `false` to stop the worker from consuming change signals from the Service Bus subscription, leaving `Processor:ScheduledSyncEnabled` as the only trigger for delta synchronization.

`ServiceBus:Enabled` defaults to `true` and controls whether the application uses Service Bus at all. When it is `false` no Service Bus client is created and `ServiceBus:FullyQualifiedNamespace`/`ServiceBus:ConnectionString` are not validated, so a polling-only worker can be deployed with no Service Bus settings; every feature that depends on Service Bus is switched off with it, including the change signal listener regardless of `Processor:ChangeSignalListenerEnabled`. The API requires `ServiceBus:Enabled` to be `true` and refuses to start otherwise, because its webhook endpoint publishes the change signals.

## Search index schema

The worker owns the index definition and applies it with `CreateOrUpdateIndex`, so `AzureSearch:SharePointIndexName` is created if missing and updated in place otherwise. Both the change signal listener and the scheduled synchronization do this as they start, so the index is prepared whichever trigger is enabled — a worker with both disabled indexes nothing and expects the index to exist already.

One document is one chunk of one file: a file indexed as three chunks becomes three documents that share `driveId`, `itemId`, and the same file and permission metadata.

| Field | Type | Attributes | Content |
| --- | --- | --- | --- |
| `id` | `Edm.String` | key, filterable | Base64url of `<driveId>:<itemId>:<chunkNumber>` |
| `driveId` | `Edm.String` | filterable | Graph drive ID of the document library |
| `itemId` | `Edm.String` | filterable | Graph `driveItem` ID of the file |
| `name` | `Edm.String` | searchable, filterable | File name including extension |
| `path` | `Edm.String` | searchable, filterable | Parent folder path of the file |
| `webUrl` | `Edm.String` | retrievable | Browser URL of the file in SharePoint |
| `mimeType` | `Edm.String` | filterable | Content type reported by Graph |
| `size` | `Edm.Int64` | filterable, sortable | File size in bytes |
| `lastModifiedUtc` | `Edm.DateTimeOffset` | filterable, sortable | Last modification timestamp from Graph |
| `eTag` | `Edm.String` | filterable | Graph ETag of the file version that was indexed |
| `chunkNumber` | `Edm.Int32` | sortable | Zero-based position of the chunk within the file |
| `content` | `Edm.String` | searchable | Extracted text of this chunk |
| `contentVector` | `Collection(Edm.Single)` | vector-searchable | Embedding of `content` |
| `allowedPrincipals` | `Collection(Edm.String)` | filterable | Principals granted access to the file |
| `permissionRoles` | `Collection(Edm.String)` | filterable | Graph permission roles on the file, such as `read` or `write` |
| `hasAnonymousAccess` | `Edm.Boolean` | filterable | True when an anonymous sharing link exists |

Every field is retrievable, and the file-level fields are copied onto each chunk so a single query can filter and render results without a second lookup. The search endpoints project a narrower set: `eTag`, `contentVector`, `allowedPrincipals`, `permissionRoles`, and `hasAnonymousAccess` back the filters but are never returned to callers.

`contentVector` uses an HNSW configuration named `content-hnsw` through the `content-vector-profile` profile, with default HNSW parameters and `AzureSearch:VectorDimensions` dimensions. No analyzers, scoring profiles, suggesters, or semantic configuration are defined; fields use the default analyzer.

`allowedPrincipals` holds prefixed tokens rather than raw IDs — `user:<id>`, `group:<id>`, `siteGroup:<id>`, `siteUser:<id>`, `application:<id>`, `email:<address>` (lowercased), and `anonymous` for anonymously shared files. Query-time principals are built in the same shape, so they compare directly in a filter. See [Permission-aware queries](#permission-aware-queries).

Azure AI Search rejects breaking field changes on an existing index, including a change to a vector field's dimensions. Changing `AzureSearch:VectorDimensions` — or the embedding model behind it — therefore means pointing `AzureSearch:SharePointIndexName` at a new index and re-indexing from scratch rather than editing the live one.

## Worker state in SQL Server

All of the worker's own state lives in one SQL Server database, configured by the `SqlServer` section, and is reached through Entity Framework Core. `SharePointIndexDbContext` in `SharePointAgent.Persistence` defines every table — the two below plus the chat assistant's — and the migrations in `SharePointAgent.Persistence/Migrations` are generated from it, so the model is the source of truth for the schema and table names are fixed rather than configured. See [Schema and migrations](#schema-and-migrations).

### Delta checkpoint

`SharePointDeltaState` holds one row per drive, keyed by `DriveId`:

| Column | Type | Content |
| --- | --- | --- |
| `DriveId` | `NVARCHAR(200)` | Graph drive ID of the document library, the primary key |
| `DeltaLink` | `NVARCHAR(MAX)` | Delta link the next pass resumes from |
| `ScanId` | `UNIQUEIDENTIFIER` | Reconciliation round the checkpoint belongs to |
| `SweptScanId` | `UNIQUEIDENTIFIER` | Round whose orphan sweep has already run; `NULL` until it has |
| `UpdatedAtUtc` | `DATETIMEOFFSET(7)` | When the checkpoint was last advanced |

The link is written only after every item on a delta page has been indexed, so a pass that fails is repeated from the last successful checkpoint. An expired delta token deletes the row and reconciles the whole drive.

### Reconciliation rounds

A pass that starts with no delta link walks the entire drive: the first pass ever, or the one that follows an expired delta token, which clears the checkpoint. Each of those opens a new round with a fresh `ScanId`, logged as `Walking the whole drive as reconciliation round <id>`. Every incremental pass that follows resumes from the stored link and keeps that round's id, so a round spans one full scan plus all the incremental passes built on top of it.

The round id is stamped on each file's metadata row as the pass reaches it — whether the file was rebuilt, refreshed, or skipped as unchanged. A file skipped during an incremental pass already carries the current round, so nothing is written; during a full scan it costs one narrow `UPDATE` of `ScanId` alone. After a full scan completes, `ScanId` therefore separates the files the scan reached from rows left behind by files it never returned.

### Orphan sweep

Once a round has walked the whole drive, any tracked file still carrying an older `ScanId` is one the drive no longer returns — most often a deletion that happened while the worker was down, whose notification nobody was listening for. The sweep removes those files' search documents and their metadata rows, and logs each one.

Deleting on the basis of "not seen this round" is only correct in a narrow window, so three conditions gate it:

- **The drive was walked end to end.** The sweep runs only after a checkpoint has been written, and a checkpoint is written only after the final delta page. Mid-walk, a file the pass has not reached yet is indistinguishable from a file that is gone.
- **The pass succeeded.** Any failure — a download, a conversion, an embedding, an index write — aborts the pass before the checkpoint, so a partial walk can never delete anything.
- **The round has not been swept already.** `SweptScanId` records the round whose sweep has finished. The incremental passes that follow share the round id and skip the sweep, so it runs once per full scan rather than on every tick.

The marker is written only after the sweep finishes, so a sweep interrupted halfway is resumed by the next pass in that round rather than being abandoned until the next full scan. Re-running it is harmless: the rows it would act on are gone.

Orphans are claimed in batches of 500 so a large clean-up does not read the whole backlog at once. The sweep is driven by metadata rows, so documents indexed before this table existed have no row and are not swept; re-indexing them once puts them under its care.

### Indexed file metadata

The worker also records what it last indexed for every file, and uses that record to do as little work as each change requires. Extraction and embedding are the expensive part of a pass — a MarkItDown conversion plus one Azure OpenAI request per chunk — so a file that has not changed is not fetched at all.

`SharePointIndexedFiles` is keyed by `(DriveId, ItemId)`:

| Column | Type | Content |
| --- | --- | --- |
| `DriveId` | `NVARCHAR(200)` | Graph drive ID of the document library, part of the primary key |
| `ItemId` | `NVARCHAR(200)` | Graph `driveItem` ID of the file, part of the primary key |
| `FileName` | `NVARCHAR(400)` | File name including extension |
| `ParentPath` | `NVARCHAR(1000)` | Parent folder path of the file |
| `WebUrl` | `NVARCHAR(2000)` | Browser URL of the file in SharePoint |
| `MimeType` | `NVARCHAR(200)` | Content type reported by Graph |
| `SizeBytes` | `BIGINT` | File size in bytes |
| `LastModifiedUtc` | `DATETIMEOFFSET(7)` | Last modification timestamp from Graph |
| `ETag` | `NVARCHAR(200)` | Graph ETag, which changes on a content **or** metadata change |
| `CTag` | `NVARCHAR(200)` | Graph CTag, which changes only on a content change |
| `PermissionsHash` | `CHAR(44)` | SHA-256 of the sharing snapshot stored on the chunks |
| `IndexFingerprint` | `NVARCHAR(200)` | Chunk size/overlap, embedding deployment, and vector dimensions used |
| `ChunkCount` | `INT` | Number of search documents the file was indexed as |
| `ScanId` | `UNIQUEIDENTIFIER` | Reconciliation round that last saw the file |
| `IndexedAtUtc` | `DATETIMEOFFSET(7)` | When the file was last indexed |
| `SensitivityLabelId` / `SensitivityLabelName` | `NVARCHAR(36)` / `NVARCHAR(255)` | Active label ID and name when available |
| `IsLabeled` / `IsEncrypted` | Nullable `BIT` | Label and encryption status of the original SharePoint file, before local decryption |
| `SensitivityCheckedAtUtc` | Nullable `DATETIMEOFFSET(7)` | When the source file's sensitivity was inspected |

Indexing and reindexing capture sensitivity through the shared readable-download path and save it after the search index write succeeds. The **Indexed files** UI shows the label beneath each filename; selecting the row shows its label ID, source encryption status, and inspection time. Existing rows show **Not checked** until reindexed or encountered by a delta/full scan. **Unlabeled** means the inspection found no label; unavailable label details are shown separately.

The protection-only MIP engine captures the file's label ID and any embedded name at indexing time. The UI resolves IDs against the live tenant catalog through `GET /api/sensitivity-labels`, which calls [Microsoft Graph's sensitivityLabels endpoint](https://learn.microsoft.com/en-us/graph/api/tenantdatasecurityandgovernance-list-sensitivitylabels?view=graph-rest-1.0). Each page open, Refresh, or successful reindex fetches the catalog again without a server-side name cache. Parent and child display names are combined, such as **Confidential · All Employees**. Renaming a label does not require reindexing; changing a file's assigned label still requires indexing/delta processing.

In the client application registration, add **Microsoft Graph → Application permissions → SensitivityLabels.Read.All**, then **Grant admin consent** for the tenant. This authorizes reading the tenant label catalog; `Content.SuperUser` authorizes decryption separately. No manual name mapping is used. If catalog access fails, the UI shows an actionable error and **Labeled**, with the original ID retained in row details, rather than displaying an unverified or stale stored name.

The `AddFileSensitivity` migration adds nullable columns without changing existing records. Restart the API and worker to apply it when `SqlServer:AutoMigrate` is enabled; otherwise deploy the migration before running the updated applications.

For each file the delta feed returns, the worker compares the item against its record and takes the cheapest sufficient action:

| Situation | Action |
| --- | --- |
| No record, no sensitivity snapshot, or `IndexFingerprint` differs from the current settings | Download, inspect sensitivity/decrypt, extract, chunk, embed, replace |
| `CTag` or `ETag` differs | Download, inspect sensitivity/decrypt, extract, chunk, embed, replace; ETag changes may include label-only changes |
| Both tags unchanged, but name, path, URL, MIME type, size, modification time, or permissions differ | Merge the changed metadata onto the existing `ChunkCount` chunks; retain the sensitivity snapshot |
| Everything matches | Nothing but the round stamp; logged as skipped |

Only permissions are read from Graph to make that decision, because a sharing change alters neither tag on the item. Every other comparison uses the delta response the worker already has.

The record is written only after the search index write succeeds, so a failed pass re-indexes the file on its retry. A file removed from the index — deleted, renamed to a disallowed extension, grown past `Processor:MaxFileBytes`, or gone from SharePoint — has its row deleted with it. Should the index have lost chunks the record still claims, the metadata merge fails, and the worker logs a warning and rebuilds the file in full.

`IndexFingerprint` is what makes a settings change safe: raising `Processor:ChunkSizeCharacters`, changing the overlap, or pointing at a different embedding deployment makes every existing row stale, so files are rebuilt rather than reported as unchanged.

### Schema and migrations

The schema is defined by `SharePointIndexDbContext` and applied by Entity Framework Core migrations. When `SqlServer:AutoMigrate` is true — the default — each application applies any pending migration as it starts, so a fresh deployment needs no separate schema step. The API and the worker both do this and may start together; applying a migration takes a SQL Server application lock, so whichever gets there second waits and then finds nothing to do.

Turn `AutoMigrate` off where the login has no DDL rights, and apply the schema from the pipeline instead:

```bash
dotnet ef migrations script --idempotent   --project backend/SharePointAgent.Persistence --output schema.sql
```

Changing a table, a column, or an index means changing the model and generating a migration for it, rather than editing the database by hand:

```bash
cd backend/SharePointAgent.Persistence
dotnet ef migrations add <Name>
dotnet ef database update      # or let AutoMigrate apply it on the next start
```

`dotnet ef` builds the context through `DesignTimeDbContextFactory`, so no application host has to start. Commands that reach the database — `database update`, `migrations script` without `--idempotent` — use `SqlServer__ConnectionString` from the environment, falling back to the LocalDB default.

#### An existing database created before migrations

Databases whose tables were created by the earlier auto-create code have no `__EFMigrationsHistory`, so the first migration would try to create tables that are already there. Both tables the worker keeps are a cache — emptying them costs one full re-extraction, nothing more — so unless there is chat history worth keeping, the simplest course is to drop the four tables and let the migration recreate them. To keep the data instead, baseline the database: add the foreign key and indexes the migration introduces, then record it as applied.

```sql
INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260910133933_InitialCreate', N'10.0.12');
```

### Connecting

`appsettings.json` ships pointing at SQL Server LocalDB — `Server=(localdb)\MSSQLLocalDB;Database=SharePointSearch;Integrated Security=true;TrustServerCertificate=true` — so a local run needs no SQL setup beyond creating the empty database once with `sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "CREATE DATABASE [SharePointSearch];"`. The tables themselves are created by the migration on first start. Deployments override the setting with `SqlServer__ConnectionString`.

The worker's SQL login needs `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on its tables, plus DDL rights while `SqlServer:AutoMigrate` is on. Managed identity is expressed in the connection string rather than a `UsedManagedIdentity` flag, because SQL Server access is granted inside the database:

```text
Server=<server>.database.windows.net;Database=<database>;Authentication=Active Directory Default;Encrypt=True
```

```sql
CREATE USER [<worker-container-app-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<worker-container-app-name>];
ALTER ROLE db_datawriter ADD MEMBER [<worker-container-app-name>];
ALTER ROLE db_ddladmin ADD MEMBER [<worker-container-app-name>];
```

Past the migration at startup, connections are opened when a pass needs them rather than held open, so a database that goes away fails that pass — retried on the next tick or left unsettled on the Service Bus — instead of stopping the worker. A database unreachable at startup fails startup while `SqlServer:AutoMigrate` is on, because the schema check cannot run. Emptying the tables is safe but not free: the drive is reconciled in full, and every file is re-extracted and re-embedded once, because a file with no record is treated as new.

## Search endpoints

The API exposes the same request body over three retrieval strategies:

| Endpoint | Strategy |
| --- | --- |
| `POST /api/search/fulltext` | Keyword search over the searchable fields |
| `POST /api/search/vector` | Pure k-nearest-neighbour search over `contentVector` |
| `POST /api/search/hybrid` | Keyword and vector search in one request, fused by reciprocal rank |

```jsonc
{
  "query": "quarterly revenue",
  "userId": "<entra-user-object-id-or-upn>", // optional
  "top": 10,                                  // 1-100, default 10
  "skip": 0
}
```

The response carries `totalCount` and the matching chunks with their relevance `score`. `contentVector` is never projected. Vector and hybrid requests embed `query` with the same Azure OpenAI deployment used at indexing time, so both apps must point at the same model and `AzureSearch:VectorDimensions`.

## State endpoints

Views over the worker's SQL Server state for the front end and operators. The GET endpoints read the database at `SqlServer:ConnectionString`; an empty table reads as an empty result before the worker's first pass. The checkpoint actions below update that database.

| Endpoint | Returns |
| --- | --- |
| `GET /api/state/summary` | Totals over `SharePointIndexedFiles` — files, chunks, source size, distinct drives, the files whose `ScanId` is not the round in the checkpoint, distinct index fingerprints, the indexing window, and a breakdown by content type |
| `GET /api/state/indexed-files` | A page of `SharePointIndexedFiles`, including each file's embedding token count from its latest successful indexing. `search` matches name, folder, URL, content type, or item ID; `driveId` filters exactly; `sort` is one of `name`, `path`, `mimeType`, `size`, `lastModifiedUtc`, `chunkCount`, `embeddingTokenCount`, `indexedAtUtc` with `desc`; `skip` and `top` (1-200, default 25) page it |
| `GET /api/state/indexed-files/{driveId}/{itemId}` | One row, or 404 |
| `POST /api/state/indexed-files/{driveId}/{itemId}/reindex` | Fetch the current SharePoint file and force extraction, embedding, and index replacement; return its updated record |
| `GET /api/state/delta` | Every `SharePointDeltaState` row, newest checkpoint first |
| `POST /api/state/delta/{driveId}/reset` | Clear that drive's delta link and sweep marker, retaining the row. The next sync starts a full scan. Returns 404 if the row is absent. |
| `DELETE /api/state/delta/{driveId}` | Remove that drive's checkpoint row. The next sync starts a full scan and writes a new row. Returns 404 if absent. |

`embeddingTokenCount` sums the token usage returned for every embedding request made during the file's latest successful indexing. It stays unchanged on metadata-only updates. It is `null` for files indexed before this field existed or when the embedding service does not report usage.

Chat attachment files record the same count in `ChatMessageAttachmentFiles`; the attachment upload, list, and reindex responses expose it, and the Attachment files page shows it. A failed indexing attempt clears the count along with its chunk count.

The indexed-file reindex endpoint uses the API's `Processor` and `DocumentIntelligence` settings. Keep the API's `Processor` chunk size, overlap, file-size limit, and allowed extensions aligned with the worker so a manual reindex produces the same chunks as a delta pass.

Like the search endpoints they require Entra sign-in and remain shared operator views. The GET responses expose indexed-file metadata and Graph delta tokens; the POST and DELETE endpoints change worker checkpoints. Run checkpoint actions while synchronization is idle, since an active pass can write a new checkpoint afterward.

## Subscription endpoints

Managing Microsoft Graph webhook subscriptions by hand, for when the renewal service is off or a subscription has to be replaced. Subscription names, Graph IDs, and each record's auto-renew setting are persisted in `WebhookSubscriptions`; the signed name is also carried in `clientState` so incoming notifications can still be authenticated. The renewal worker checks every enabled record when the global `SharePoint:SubscriptionRenewalEnabled` setting is on. The Default record starts enabled; new additional subscriptions start disabled.

| Endpoint | Effect |
| --- | --- |
| `GET /api/subscriptions` | One union of database-tracked records and subscriptions found on the application registration, merged by Graph subscription ID. A tracked record missing from Graph has a `databaseId`, null Graph `id`/`expirationUtc`, and `Missing` status; unmatched Graph entries are untracked. Saved names and the `isDefault` flag identify the Default record. |
| `POST /api/subscriptions` | Creates one over the configured resource. Body may include `name`, `days`, `notificationUrl`, and `clientState`; an omitted client state uses the signed value generated from the name and configured secret |
| `PUT /api/subscriptions/{id}` | Changes a subscription's lifetime, name, notification URL, or client state. Omit `clientState` to retain its saved value |
| `POST /api/subscriptions/{id}/renew` | Extends an existing subscription, body `{ "days": 28 }` |
| `PUT /api/subscriptions/{databaseId}/auto-renew` | Enables or disables automatic renewal for a tracked record, body `{ "enabled": true }` |
| `DELETE /api/subscriptions/{id}` | Removes it. Graph stops delivering notifications immediately |

Two rules are enforced across all of them:

- **The default subscription cannot be deleted or moved.** Its database record is reserved even if auto-renewal is disabled. Change `SharePoint:NotificationUrl` to move it.
- **Notification URLs are unique.** Creating or editing onto a URL another subscription already uses returns `409`.

Changing a subscription's client state creates a replacement because Microsoft Graph does not support changing that property with `PATCH`. The replacement is created before the original is deleted. The response reports `replaced` and includes the new subscription ID.

`PUT /api/subscriptions/{id}` accepts either a Graph ID or a tracked row's `databaseId`. Updating a tracked row that is missing from Graph creates and associates a new Graph subscription. Graph-only untracked rows cannot be updated.

Custom `clientState` values must be 16-128 characters and are saved in `WebhookSubscriptions.ClientState`. The webhook validates notifications against the saved value, and automatic recreation of the Default subscription reuses it. The value is never returned; each entry carries `clientStateMatches` and `hasCustomClientState` instead. A rejection from Graph comes back as `502` with Graph's message.

**These endpoints change tenant state and require Entra sign-in.** `DELETE` in particular stops change notifications, leaving the scheduled synchronization as the only trigger.

## Chat assistant

An agent built with the [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) (`Microsoft.Agents.AI.OpenAI`) answers questions about the indexed library. It runs on `AzureOpenAI:ChatDeployment` — `gpt-5-mini` by default, on the same resource and endpoint as the embedding deployment — and is given five tools of its own: `search_documents`, a hybrid search over the SharePoint index; `search_attachments`, a hybrid search limited to indexed files attached to the current conversation; `download_file`, which copies a SharePoint search result onto the local file system; `refresh_file`, which takes that file again as SharePoint holds it now; and `upload_file`, which sends the local copy back over the document. When [officecli](#editing-a-file-with-officecli) is configured its tools are added to those, which is what lets the assistant read a whole document or edit one. Its instructions tell it to search before answering questions about document or attachment content.

Conversations and messages are stored in the same SQL Server database, in `ChatConversations` and `ChatMessages`, which the same migration creates as the worker's tables. Deleting a conversation cascades to its messages through the foreign key. Each turn replays the stored history — the last 40 messages — so the agent needs no state of its own between requests, and the documents the tool retrieved are saved with the answer as citations.

The chat composer also accepts up to ten attachments per message. An attachment is first stored in the configured Azure Blob container and recorded in `ChatMessageAttachmentFiles`, then converted to Markdown, chunked, embedded, and written to the separate `AzureSearch:UploadIndexName` index. Only successfully indexed attachment-file IDs are sent with a chat turn. `ChatMessageAttachments` joins those IDs to the stored user message; after that row is saved, its generated ID is written back to `ChatMessageAttachmentFiles.ChatMessageAttachmentId`. The agent receives attachment names as context and retrieves indexed excerpts with `search_attachments` when needed, including on later turns. The tool derives allowed file IDs from message links in the current conversation and applies them as a search-index filter. The Attachment Files page shows `NotStarted`, `Indexing`, `Indexed`, and `Failed` states, links attached files to their conversation, and supports download, reindex, and orphan deletion. Failed or unattached files remain available for retry or cleanup.

Each replayed message includes the IDs and names of its attachments. `search_attachments` accepts an optional `attachmentId` to target one file when names repeat; the API only searches it if that ID belongs to the current conversation.

| Endpoint | Effect |
| --- | --- |
| `GET /api/chat/conversations` | Every conversation, most recently updated first |
| `POST /api/chat/conversations` | Starts one. Body `{ "title": …, "userId": … }`, both optional |
| `DELETE /api/chat/conversations/{id}` | Removes the conversation and its messages |
| `GET /api/chat/conversations/{id}/messages` | The conversation and its full thread |
| `POST /api/chat/conversations/{id}/messages` | Runs one turn. Body `{ "content": "…" }`; returns the stored question, the answer with its citations, and the conversation title |
| `POST /api/chat/messages/{id}/feedback` | Rates an answer. Body `{ "feedback": "Like" \| "Dislike" \| null }`, where null clears an earlier rating |
| `GET /api/chat/feedback` | Every rated answer, newest first, each with the question that prompted it and the documents it cited. `feedback` narrows to one rating, `search` matches the answer or the conversation title, `skip` and `top` (1-100, default 20) page it. The `liked` and `disliked` totals ignore the rating filter, so they hold still while it is toggled |

A conversation created with a `userId` passes it to every search the assistant runs in that conversation, so answers are restricted to what that user may view — the same filter the search endpoints apply. **Without one the assistant searches the whole index**. Conversations remain shared among signed-in operators.

The first question replaces the placeholder title, so conversations name themselves. The question is stored before the model runs, so a turn that fails still shows what was asked.

### Downloading and refreshing a file

The instructions call for `download_file` when the user asks for a local copy of a document, and also when they ask to edit, change, or update one — a local copy is where editing starts, so the agent fetches the file and reports where it went. Neither download nor refresh writes anything back; [`upload_file`](#uploading-a-file-back) is the only tool that does.

`download_file` takes the `fileId` of a search result — the drive item ID, which `search_documents` returns alongside each excerpt — streams the file out of Microsoft Graph into `Downloads:Directory/<item id>/<file name>`, checks its protection, and returns a readable local path. **A file already on disk is not downloaded again**; protected cached copies are still checked for extraction rights before their path is returned, and local edits are preserved.

Microsoft Information Protection (MIP) inspects the file contents, so a sensitivity label without encryption leaves the file unchanged. For an encrypted file, the tool authenticates with the existing `SharePoint:TenantId`, `ClientId`, and `ClientSecret` and requires `EXTRACT` rights before decrypting. It retains the encrypted original beside the local copy with a `.mip-protected` suffix. Both the downloaded and decrypted sizes are limited by `Downloads:MaxFileBytes`. A failed conversion to plaintext does not publish a partial download or replace a previous cached copy.

The app registration also needs Azure Rights Management authorization; Graph file permissions alone do not grant decryption rights. This implementation uses the application's identity, not the conversation user's identity. App-only access to tenant-protected content can require the administrator-approved `Content.SuperUser` application permission, which grants broad access; user-delegated decryption is not implemented. See Microsoft's [MIP permission reference](https://learn.microsoft.com/en-us/information-protection/develop/concept-api-permissions). No tenant permissions or document labels are changed by this application.

Windows uses the MIP native libraries supplied by NuGet and requires the matching Visual C++ runtime. Linux builds use the Ubuntu 24.04 MIP package; the API, background worker, and agent-host containers install its native dependencies. See [MIP platform setup](https://learn.microsoft.com/en-us/information-protection/develop/setup-configure-mip). This check applies to the agent's `download_file` and `refresh_file` tools and the shared indexing pipeline, including manual reindexing and delta synchronization. Indexing downloads the current SharePoint version into an isolated temporary folder, decrypts before extraction, and deletes temporary plaintext and protected originals on success or failure. `Processor:MaxFileBytes` limits both downloaded and decrypted content. View Markdown and Office previews for indexed SharePoint files use the same readable-download path: authorized decryption happens in a temporary folder, `Downloads:MaxFileBytes` bounds both downloaded and decrypted content, and temporary files are cleaned up on success or failure. Preview responses use `Cache-Control: no-store`; missing extraction rights return HTTP 403 with an actionable error. Chat attachment conversion still uses its existing download path.

`refresh_file` is the same download without that check: it always fetches, and replaces whatever is at the path. The cache is keyed by item ID and notices neither a new version in SharePoint nor an edit made locally, so this is how either is resolved — take the library's current version, at the cost of local changes that were not uploaded. The instructions have the agent say what would be lost and ask first when it is the one that made those changes. The new copy is streamed to a staging name and moved into place, so the copy being replaced survives a download that fails halfway.

| Setting | Default | Effect |
| --- | --- | --- |
| `Downloads:Directory` | empty | Root directory for downloaded files. A relative path resolves against the process working directory; empty means `sharepoint-downloads` under the system temporary directory |
| `Downloads:MaxFileBytes` | 20971520 | Largest file `download_file` and `refresh_file` will fetch, and the largest `upload_file` will send back. A larger file is refused, and the model reports that instead of a path |

### Uploading a file back

`upload_file` refuses a local copy with a retained `.mip-protected` original: uploading plaintext would remove the original document's protection. Protection-preserving upload is not implemented; use a protection-aware Office application to save edits to these documents.

`upload_file` is `download_file` reversed: it takes the same `fileId`, and sends whatever is on disk at that moment back over the document in SharePoint. Microsoft Graph takes it in one request up to 4 MB and through an upload session in slices above that, so a large file is streamed rather than held in memory, and `Downloads:MaxFileBytes` caps it either way. **SharePoint keeps the previous file as a version rather than losing it**, so an unwanted upload is recoverable from the document's version history. The local copy is left in place, which means a later `download_file` for that item still reuses it — the cache does not notice new versions, including the one just uploaded, so `refresh_file` is what takes the file back off the server.

The new version reaches the index the ordinary way, with no special case for it: SharePoint notifies the webhook, the next delta pass sees a `cTag` that does not match the one recorded for the file, and the worker extracts, chunks, and embeds it again.

Uploading changes what other people see, so the instructions hold the agent to an explicit request — "save it back", "upload it", "publish it" — and tell it to stop after an edit and offer, rather than upload because an edit finished, and to ask when the request is ambiguous. The tool accepts only a `fileId` from one of the same turn's searches, so the model cannot name an arbitrary drive item, and only a file that has been downloaded; there is nothing else to send.

Two requirements that indexing alone does not give you:

- **Write access for the Entra application.** Indexing needs only read — `Sites.Selected` with a read grant, or `Sites.Read.All`. Uploading needs write: a `Sites.Selected` grant of `write`, or `Sites.ReadWrite.All`. Without it Graph rejects the upload and the model reports the rejection.
- **A deliberate decision about who may trigger it.** The permission filter behind `search_documents` is a *read* filter: it says the user may see the document, not that they may change it. Any file a conversation can search, it can overwrite. With no `userId` on the conversation that is the whole index. Restrict enterprise application assignment to trusted operators before granting the application write access.

### Editing a file with officecli

For running the same agent inside the API or in a Foundry Hosted Agent sandbox, see [agent hosting options](backend/SharePointAgent.AgentHost/README.md). Both modes retain SQL conversation history, streamed answers, and tool-status reports; `ChatAgent:Mode` defaults to `Local`.

[officecli](https://www.npmjs.com/package/@officecli/officecli) is a command line over `.docx`, `.xlsx`, and `.pptx` files that also runs as an [MCP](https://modelcontextprotocol.io/) server. The assistant connects to it with the MCP C# SDK (`ModelContextProtocol.Core`) and adds whatever tools it publishes — one, `officecli`, which takes an officecli command line — to its own four. Since officecli only reaches files on this host, the pairing is `download_file` first, then officecli on the `localPath` it returned; the instructions say as much, and that an edit to the local copy is not a change in SharePoint until `upload_file` sends it back.

They also require added or changed content to match the style of the document around it: read the neighbouring paragraphs, rows, or shapes and their properties first, reuse the style, font, spacing, list format, table formatting, and slide layout they use rather than leaving default-formatted content behind, follow the document's own wording conventions, and check the result before reporting the edit as done.

| Setting | Default | Effect |
| --- | --- | --- |
| `OfficeCli:Enabled` | `true` | Whether the assistant gets officecli's tools. With `false` it can search, download, refresh, and upload but not edit, and no child process is started |
| `OfficeCli:Command` | `officecli` | The executable — a name on `PATH`, as the npm package's shim is, or a full path to it |
| `OfficeCli:Arguments` | empty, meaning `mcp` | Arguments that put officecli into MCP server mode. Configuring a list *adds* to what the options object holds — the configuration binder appends to collections rather than replacing them — which is why the default is empty rather than `[ "mcp" ]`: those two together would run `officecli mcp mcp`, and officecli reads the second `mcp` as the name of an editor to register itself with |
| `OfficeCli:StartupTimeoutSeconds` | 60 | How long the server has to start and list its tools |

Install it with `npm install -g @officecli/officecli`, which puts the shim `OfficeCli:Command` defaults to on `PATH`.

The server is one child process per application, started by the first turn that needs its tools and shared by every turn after — starting it per turn would add its startup to every answer — and shut down with the host. **A server that cannot be started costs one turn, not every turn**: the failure is logged, the assistant answers with its own four tools, and the next attempt is after a restart. Startup validation only requires that `OfficeCli:Command` is set when `OfficeCli:Enabled` is true; whether the command works is discovered on first use rather than at boot, so a missing officecli does not stop the API from starting.

**These tools read and write this host's file system as the API process.** officecli takes a path, and nothing constrains that path to `Downloads:Directory` — the child process runs there, so a bare file name lands among the downloaded files, but an absolute path elsewhere is the model's to pass. Signed-in operators can invoke this capability: restrict application assignment, run the API as an account with little else to reach, or set `OfficeCli:Enabled` to `false` where editing is not wanted.

The tool only accepts a `fileId` that one of the same turn's searches returned, so the permission filter that trims those results also bounds what can be downloaded — the model cannot reach a file by inventing an ID. That still means **any file a conversation can search, it can also write to the host's file system**, and with no `userId` on the conversation that is the whole index. The file name is sanitized and the resolved path is checked to be inside `Downloads:Directory`, so a name coming back from SharePoint cannot write outside it. Failures — a file over the limit, a rejection from Graph, a disk error — come back to the model as a message rather than failing the turn.

## Front end

### Continuous integration

GitHub Actions runs these workflows on pushes and pull requests affecting their respective directories. Both can also be started manually from the **Actions** tab and require no Azure credentials:

| Workflow | Checks | Artifacts |
| --- | --- | --- |
| [backend-ci.yml](.github/workflows/backend-ci.yml) | Restore and Release-build the .NET 10 solution, then run backend tests on Ubuntu 24.04 with MIP native dependencies | TRX test results |
| [frontend-ci.yml](.github/workflows/frontend-ci.yml) | Install locked dependencies with Node.js 22 and `npm ci`, then type-check and build with Vite | Production `dist` files |

Artifacts are retained for 14 days. These workflows validate builds only; they do not deploy the application.

### Running locally

`frontend/` is a React and Vite app over these endpoints: the two state tables, and the three retrieval strategies run one at a time or all three side by side. See [frontend/README.md](frontend/README.md).

```bash
cd frontend
npm install
npm run dev     # http://localhost:5173, proxying /api to http://localhost:5263
```

The API must be running as well. `Cors:AllowedOrigins` lists the origins allowed to call it directly, `http://localhost:5173` by default; the dev server's proxy means the browser makes same-origin requests and does not rely on it.

## Permission-aware queries

Every chunk stores `allowedPrincipals`, `permissionRoles`, and `hasAnonymousAccess`. When `userId` is supplied, the endpoints resolve that user through Microsoft Graph — object ID, mail addresses, and every transitive group membership — and filter results to what the user can view:

```text
hasAnonymousAccess eq true or allowedPrincipals/any(p: search.in(p, 'user:<object-id>,email:<address>,group:<group-id>', ','))
```

Principals are resolved server-side from the user ID and cached for 10 minutes; principal identifiers are never accepted directly from the request body. This needs `User.Read.All` and `GroupMember.Read.All` (or `Directory.Read.All`) Graph application permissions in addition to the site/drive permissions used for indexing.

**Omitting `userId` searches the whole index with no security filter.** The endpoints require Entra sign-in, but `userId` remains an operator-selected filter rather than the signed-in identity. A per-user portal would additionally need server-derived user IDs, ownership checks, and document authorization on every read/write path; the current UI is for assigned, trusted operators.

SharePoint site groups (`siteGroup:`/`siteUser:` principals) are not Entra groups and cannot be expanded from directory membership, so grants made only through a site group are not matched. Validate the permission model against your SharePoint inheritance and group-expansion requirements before production use.

## Operational behavior

- Duplicate webhook deliveries are safe: a delta call after the checkpoint returns no changes, and item replacement is idempotent.
- A content change rebuilds the file's chunks, content vectors, and permission fields.
- A rename, a move, or a permission-only change updates those fields on the existing chunks instead, leaving the content and vectors as they are. See [Indexed file metadata](#indexed-file-metadata) under [Worker state in SQL Server](#worker-state-in-sql-server).
- A file the delta feed returns with nothing changed is skipped without being downloaded, and only stamped with the current reconciliation round. This is what keeps a full reconciliation — after an expired delta token, or on the startup pass of a restarted worker — from re-extracting and re-embedding the whole library.
- A deleted file removes all documents matching its drive/item IDs, and its metadata row.
- A deletion that Microsoft Graph never reported — because the worker was down, or the notification was lost — is cleaned up by the orphan sweep at the end of the next full scan, not by the incremental passes in between. See [Orphan sweep](#orphan-sweep).
- Processing failures leave the Service Bus message unsettled, allowing normal retry/dead-letter behavior. The delta checkpoint is not advanced on failure, and neither is a file's metadata row.
- Files over `Processor:MaxFileBytes` are skipped. Increase the limit only after considering Graph, memory, extraction, and embedding costs.
- Only files whose extension is in `Processor:AllowedFileExtensions` are indexed; `appsettings.json` ships with `.docx`, `.pptx`, and `.xlsx`. Entries match case-insensitively, with or without a leading dot, and the worker refuses to start on an empty list rather than silently indexing nothing.
- A file outside the allow list has any previously indexed chunks removed, so narrowing the list or renaming a file to a disallowed extension cleans the index on the next pass rather than leaving stale documents behind. The removal is unconditional rather than driven by the metadata table, so it also cleans up documents indexed before metadata tracking was enabled.
- DOCX, PPTX, and XLSX are converted to markdown by the MarkItDown service at `MarkItDown:Endpoint`, which keeps headings, lists, and tables in the indexed text. There is no local fallback: a conversion that fails leaves the file unindexed and the Service Bus message unsettled, so the normal retry path applies, and the worker refuses to start without an endpoint.
- The worker probes `MarkItDown:HealthPath` (`/health`) as it starts and every `MarkItDown:HealthCheckMinutes` afterwards, with a 10 second timeout of its own rather than the conversion timeout. Only transitions are logged, so a healthy service is reported once and an outage logs one warning until it recovers. The probe reports and nothing more — indexing is not gated on it, and `MarkItDown:HealthCheckEnabled` turns it off.
