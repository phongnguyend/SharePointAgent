# SharePointAgent

This .NET 10 solution keeps a permission-aware Azure AI Search vector index synchronized with a SharePoint document library.

## Components

- `SharePointAgent.Api` exposes `POST /api/sharepoint/webhook`, completes Microsoft Graph's validation handshake, validates `clientState`, and publishes change signals to an Azure Service Bus topic. It also serves search, state, and checkpoint management endpoints for the front end.
- `frontend` is a React and Vite app for viewing the worker's SQL Server state and running the three retrieval strategies against the index. See [frontend/README.md](frontend/README.md).
- `SharePointAgent.Background` consumes a topic subscription. It follows the Microsoft Graph drive delta feed and, for each file the feed returns, compares it against the metadata recorded for the last indexing run in SQL Server: an unchanged file is left alone, a renamed or re-shared file has its metadata refreshed in place, and only a file whose content actually changed is downloaded, extracted, chunked, embedded, and replaced. Deleted files have all chunks removed. Two more hosted services run alongside it: one creates the Graph subscription and renews it before expiration (`SharePoint:SubscriptionRenewalEnabled` to turn it off), and one runs the same delta synchronization every `Processor:ScheduledSyncMinutes` (5 by default, `ScheduledSyncEnabled` to turn it off) so missed notifications still get picked up. Either trigger can run without the other: disable the subscription to poll only, or disable the schedule to react only to notifications. All three triggers — notification, schedule, and startup sync — are serialized, so only one delta pass runs at a time.
- `SharePointAgent.AgentHost` runs the same chat agent as a Foundry Hosted Agent, behind an `/invocations` endpoint the API streams from. See [agent hosting options](backend/SharePointAgent.AgentHost/README.md).
- [Ollaya](backend/Ollaya/README.md) serves the `winnow:e4b` decision model on a serverless T4 GPU, in a Container Apps environment of its own so it can run in whichever region has GPUs. The application calls it over HTTPS through `OllayaClient`. It is deployed by its own template in [infra/Ollaya](infra/Ollaya/main.bicep).
- `SharePointAgent.SandboxHost` runs inside an isolated environment and gives future agent tools a file system and a script runner (PowerShell, Python, Node.js, Bash) over HTTP. The same image is deployed to an Azure Container Apps Dynamic Sessions pool and to Azure Container Apps sandboxes. It does not reference the rest of the backend. See the [sandbox host](backend/SharePointAgent.SandboxHost/README.md).
- `SharePointAgent.AspireAppHost` runs the API, the worker, and the agent host together against a containerized SQL Server, with the .NET Aspire dashboard over the three of them.

The work behind those hosts is split into four layers, each its own project:

| Project | Holds | References |
| --- | --- | --- |
| `SharePointAgent.Domain` | The records, enums, and exceptions the rest is written in terms of — drive items, chunks, conversations, agents, subscriptions — plus pure helpers such as `TextChunker` and `SearchChunkKey`. No package references. | — |
| `SharePointAgent.Application` | The contracts the outer layers implement. `Repositories.cs` holds the application's view of its own database — one interface per aggregate (`IChatRepository`, `IAgentRepository`, `IFileMetadataRepository`, `IDeltaStateRepository`, `IIndexStateRepository`, …); `IndexingAbstractions.cs` holds the abstractions over other people's services (`ISearchIndexStore`, `ISearchQueryStore`, `IChangeSignalPublisher`, `ISharePointChangeProcessor`, `IContentExtractor`). Plus the settings classes and orchestration that needs nothing more than those contracts. No package references. | Domain |
| `SharePointAgent.Persistence` | The Entity Framework Core model and its migrations, with one repository per contract in `Repositories/`. `AddPersistence` registers the pooled context and maps every repository interface onto its implementation, so no other project names an `Ef`-anything. | Application |
| `SharePointAgent.Infrastructure` | Everything outside the process: the Microsoft Graph .NET SDK for subscriptions, delta tracking, downloads, and permissions; Service Bus, Azure AI Search, Azure OpenAI and the chat agent, Blob Storage, Document Intelligence, and MarkItDown. Also the composition root the hosts call into. | Application, Persistence |

Embeddings go through `Microsoft.Extensions.AI`'s `IEmbeddingGenerator<string, Embedding<float>>`, backed by `AzureOpenAIClient` from the Azure OpenAI SDK, so the embedding model can be swapped without touching the indexing or query code.

The webhook is intentionally only a signal. Microsoft Graph drive notifications do not contain a complete, durable list of item-level changes. A delta link is checkpointed in SQL Server only after every returned page is indexed successfully, making retries idempotent and allowing expired delta tokens to trigger a full reconciliation. Those reconciliations are why file metadata is tracked in the same database: the delta feed then returns every file in the library, and without a record of what was already indexed each one would be extracted and embedded again. See [Worker state in SQL Server](#worker-state-in-sql-server).

## Shared organization signing

The configuration section is `DocumentSigning`. When upgrading from `Signing`, rename existing user-secret keys from `Signing:...` to `DocumentSigning:...` and deployment environment variables from `Signing__...` to `DocumentSigning__...`; the old section is no longer read.

You can first upload PDFs directly on **Attachment files** using the file picker or drag-and-drop. These uploads are stored as orphan files without indexing, so signing does not require Document Intelligence or embedding calls. The storage-only API is `POST /api/attachment-files?index=false` with multipart `file`; omitting the flag preserves the existing chat upload/indexing behavior. Storage quotas and upload validation apply in either mode.

On **Attachment files**, choose **Signatures** on a PDF (also available in its preview header). Select DocuSign or Adobe Acrobat Sign, enter a subject/message and 1–20 signers in signing order, then choose **Create draft**. This uploads the original PDF without sending invitations. **Open preparation screen** opens a new tab where the sender places fields, reviews recipients, and sends. Users do not connect personal provider accounts.

The API uses one dedicated shared sender per provider. Existing attachment ownership and application roles apply: users manage their own requests; Global Admin manages all; Global Reader Admin can read records and download completed files. Records retain the initiating application user, provider ID, original recipient input, and timestamps. Changes made in the provider's preparation screen remain authoritative in its audit trail; local recipient input is not a live mirror. The preparation session acts as the shared sender, so use a dedicated account and grant application sending access only to people trusted to act as that sender. Hiding provider navigation is not an account isolation boundary.

Configure **SharePointAgent.Api only**, using environment variables, local user secrets, or Container Apps secret references. Never commit keys or tokens. Both providers default to disabled.

See the [GitHub environment variable/secret mapping](infra/README.md#document-signing-configuration-mapping) for storage types, example values, and corresponding API runtime keys. Release API forwards these settings to the API Container App, including when selected through Release services; credentials use Container Apps secret references.

| Setting | Value |
| --- | --- |
| `DocumentSigning__ReturnUrl` | Frontend `/attachment-files` URL for the DocuSign return redirect; HTTPS in production |
| `DocumentSigning__DocuSign__Enabled` | `true` after configuration |
| `DocumentSigning__DocuSign__Demo` | `true` for developer accounts, `false` for production |
| `DocumentSigning__DocuSign__ApiBaseUrl` | Account REST base URL ending `/restapi/v2.1/`; demo: `https://demo.docusign.net/restapi/v2.1/` |
| `DocumentSigning__DocuSign__AccountId` | Shared sender's API account ID |
| `DocumentSigning__DocuSign__ClientId` | Integration key/client ID |
| `DocumentSigning__DocuSign__SenderUserId` | Dedicated sender's API user GUID |
| `DocumentSigning__DocuSign__PrivateKeyPem` | Registered RSA private key PEM |
| `DocumentSigning__AdobeSign__Enabled` | `true` after configuration |
| `DocumentSigning__AdobeSign__OAuthRedirectUri` | Optional HTTPS frontend `/adobe-sign-callback.html` URL, registered in Adobe for the Global Admin token page |
| `DocumentSigning__AdobeSign__AuthUrl` | Adobe authorization URL; default `https://secure.adobesign.com/public/oauth/v2`. For Singapore use `https://secure.sg1.adobesign.com/public/oauth/v2`. |
| `DocumentSigning__AdobeSign__AccessTokenUrl` | Required for OAuth authorization: initial token exchange URL, e.g. `https://api.sg1.adobesign.com/oauth/v2/token`. No callback endpoint fallback. Signing and token refresh still use `ApiAccessPoint`. |
| `DocumentSigning__AdobeSign__ApiAccessPoint` | Regional API origin from OAuth, e.g. `https://api.na1.adobesign.com`, without `/api/rest/v6` |
| `DocumentSigning__AdobeSign__ClientId` | OAuth application client ID |
| `DocumentSigning__AdobeSign__ClientSecret` | OAuth client secret |
| `DocumentSigning__AdobeSign__RefreshToken` | Refresh token authorized by the dedicated shared sender |

For DocuSign, register an integration and RSA key, then obtain the sender's consent to `signature` and `impersonation`. The backend exchanges signed JWT assertions for access tokens. After production activation, configure the account-specific production API URI; changing `Demo` alone does not change that URI. See [shared-system-user authentication](https://www.docusign.com/blog/developers/the-trenches-authenticate-without-user-interaction-system-user) and [embedded sender views](https://www.docusign.com/blog/developers/esignature-embedded-views-update).

For Adobe, authorize the dedicated sender using an OAuth application with `agreement_write:self`, `agreement_send:self`, `agreement_read:self`, and `user_login:self` scopes. Store the resulting refresh token and regional API access point in API secrets. The backend renews access tokens using `/oauth/v2/refresh`; tokens are cached in memory until shortly before expiry. Adobe refresh tokens expire after 60 days of inactivity; reauthorize after a long shutdown. Verify account/API entitlements and auto-login permissions with Adobe. See [OAuth setup and renewal](https://developer.adobe.com/acrobat-sign/docs/overview/developer_guide/oauth) and [agreement views](https://github.com/adobe-sign/AdobeSign-OpenAPI/blob/master/json/agreements.json).

### In-app signing

**In-App Signature** lets the current user sign a PDF themselves without an external provider. It is enabled by default; set `DocumentSigning__InApp__Enabled=false` to hide it. An optional message and up to 20 signers (name and email, unique) can be entered as for the other providers; they are recorded on the request, shown in the request list, and included in the audit record, but nobody is notified yet and the requesting user remains the one who signs. Choosing **Open signing editor** creates a local `Draft` request and opens the editor: drag **Signature**, **Initials**, **Date**, and **Text** fields onto any page (or click one to add it to the visible page), move them, resize them from the corner handle, and choose **Save fields**. **Templates** (in Place fields) saves the current layout as a new named template or overwrites an existing one, and loads a template by replacing or adding to the current fields. Templates are personal (`/api/signing-templates`, scoped to the signed-in user, at most 100 each with unique names), store field positions and the source page count only, never drawn signatures or typed values, and skip fields on pages the current document does not have. Loaded fields are unsaved until **Save fields**. In **Sign** mode, click each signature or initials field to draw on the signature pad or upload, drag and drop, or paste an image with Ctrl+V / Cmd+V (PNG, JPEG, or WebP, up to 5 MB). Preview the image before applying it, optionally reuse the previous signature, or apply it to every unsigned field of that type. Fill in the date and text fields, then choose **Finish**.

Field layouts and drawn or imported signatures are saved on the request (`GET`/`PUT /api/attachment-files/{id}/signatures/{requestId}/fields`) as page-relative coordinates, so the layout does not depend on zoom. On **Finish**, the browser draws every value onto the original PDF with pdf-lib (correcting for rotated pages) and uploads it to `POST .../complete` as `application/pdf`. The server checks the saved fields are complete (at least one signature or initials field, every field filled), stores the signed PDF in the uploads container under `signed-documents/`, and records SHA-256 hashes of the original and signed documents. The audit record is a server-generated PDF listing the signer account, timestamps, hashes, and fields. Only the request's creator can edit or finish it, a finished request cannot be changed, and an unfinished draft can be discarded. Encrypted or password-protected PDFs cannot be signed in the app. In-app signatures are drawn or imported images with an audit record; they are not certificate-based digital signatures.

The `AddSignatureRequests` EF migration runs through the existing migration startup flow. `AddInAppSigning` adds the in-app field, hash, and signed-document columns. `AddSignatureRequestMessage` stores the request message for every provider. `AddSigningTemplates` adds the personal field-template table. IDs are database-generated. Attachments referenced by signing requests cannot be deleted. Deployment does not send or modify existing files.

Global Admins can obtain Adobe tokens without Postman on **Admin → Document signing** (`/admin?tab=document-signing-configuration`). Configure Adobe `ClientId`, `ClientSecret`, and `OAuthRedirectUri` first; signing may remain disabled during initial setup. Register the exact HTTPS callback URL in Adobe and enable `agreement_read:self agreement_write:self agreement_send:self user_login:self`. Open the page on the callback's frontend origin, allow the authorization popup, sign in as the shared sender, and approve access. The page displays the access token, refresh token, regional API origin, and access-token lifetime, with reveal/copy/clear controls. Copy the refresh token and API origin to configuration yourself, enable Adobe, then restart or release API. Tokens exist only in the response and page memory, are cleared when leaving, and are never written to the database, browser storage, or application settings. Clipboard contents remain until replaced. Global Reader Admins and ordinary users cannot access these endpoints. OAuth attempts expire after ten minutes and are bound to the initiating administrator; Adobe codes can be exchanged only once. No new database migration is needed for this page.

After sending, return and choose **Refresh status** on the request. Once completed, download **Signed PDF** and **Audit record**. **Reload list** reads local records only. This version shows the latest 100 requests per attachment, uses manual status refresh, and downloads artifacts from the provider. Webhook synchronization and automatic archiving/indexing of signed copies are not implemented. Manage cancellations and recipient corrections in the provider account.

An interrupted create may leave `NeedsReview` or `Creating`. Retries with the same client request ID never automatically create another draft. An administrator must inspect the provider account before creating a replacement. The local client request ID is sent as DocuSign's transaction ID or Adobe's external ID for reconciliation. Preparation links are generated on demand and not stored. If Adobe is still processing a PDF, retry **Prepare and send** on the existing request.

Automated tests mock provider HTTP calls. Before production, verify draft creation, field placement, sending, completion, and both downloads in each provider's test account. No live requests have been sent by implementing this feature.

## Prerequisites

PDF attachments support shared-organization DocuSign and Adobe Acrobat Sign requests, with provider preparation screens for placing fields. Both integrations are disabled by default. **In-App Signature** lets a user place fields and draw their own signature without an external provider. See [signing setup](#shared-organization-signing) for credentials and operational limits.

Create these resources before deploying:

1. An Azure Service Bus namespace with the configured topic and subscription.
2. A SQL Server database reachable at `SqlServer:ConnectionString`, holding the worker's delta checkpoint and indexed-file metadata. Azure SQL Database, SQL Server, or SQL Server in a container all work; the schema is applied by an Entity Framework Core migration as the application starts.
3. Azure AI Search and an Azure OpenAI embedding deployment. The search index is created or updated automatically.
4. Azure Blob Storage for chat attachments. The Bicep templates create a private `chat-uploads` container.
5. A MarkItDown service reachable at `MarkItDown:Endpoint`, which converts SharePoint Office files and non-text chat attachments to Markdown.

An optional [PageIndex FastAPI service](backend/PageIndex/README.md) builds JSON document
trees from Markdown or text-based PDFs. It runs independently and is not required by
the current indexing worker or chat agent.
6. Azure AI Document Intelligence for PDF indexing (SharePoint documents and attachments), and OCR for supported images. PDFs require a configured endpoint; other unsupported SharePoint formats still use metadata-only indexing when it is absent.
7. An Entra application or managed identity with Microsoft Graph application access to the target site/drive. Prefer `Sites.Selected` with an explicit grant to the site; `Sites.Read.All` is the broader alternative. Admin consent is required. The Browse page's create, upload, rename, delete, copy, and move operations and the chat assistant's [`upload_sharepoint_file`](#uploading-a-file-back) tool need a `write` grant (or `Sites.ReadWrite.All`).

Global Admins can configure site grants at **Admin → Site access** (`/admin?tab=site-permissions`). Enter the tenant ID, a privileged application's client ID and secret value, the SharePoint site URL, and the target application's client ID and display name. Select **Read** or **Write**, then **Save site permission**. This creates a grant or replaces the target application's existing site role; it leaves other applications' grants unchanged. Multiple existing grants for the target are rejected for manual resolution. Public-cloud `https://<tenant>.sharepoint.com/...` site URLs are supported.

The privileged application requires admin-consented Microsoft Graph `Sites.FullControl.All`; the target needs admin-consented Microsoft Graph `Sites.Selected` in that tenant. The page does not assign Entra API permissions or grant tenant-wide consent. See Microsoft's [selected permissions overview](https://learn.microsoft.com/en-us/graph/permissions-selected-overview). Privileged credentials are sent to the backend in the request body and used only for that operation, with no database, configuration, or browser-storage persistence. The form clears the secret on submission and unmount. Use the deployed HTTPS frontend; do not enable request-body logging for this endpoint. Global Reader Admins and ordinary users cannot save grants. No database migration or additional server credentials are required.
8. A public HTTPS URL for the API. Microsoft Graph must be able to call it during subscription creation. Not needed when `SharePoint:SubscriptionRenewalEnabled` is `false` and the worker polls on its schedule alone.

Assign Azure RBAC appropriate to each process: Service Bus Data Sender, Storage Blob Data Contributor, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User to the API; Service Bus Data Receiver, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User to the worker. Add Cognitive Services User when Document Intelligence is enabled. The Bicep templates create these assignments. SQL Server permissions are granted inside the database rather than through RBAC: see [Worker state in SQL Server](#worker-state-in-sql-server).

The GitHub Actions infrastructure workflows provision the shared Azure resources, then the Container Apps environment with default hello images for the ACA apps. Use **Actions → Release services → Run workflow** (`release.yml`) to select `dev` or `test` and check any combination of Database migrations, MarkItDown, PageIndex, Ollaya, Dynamic Sessions, Sandboxes, AgentHost, API, Background, and Frontend. Selected components release in that order from the same commit; a failure stops later releases. The individual release workflows remain available. For the first deployment, follow the [infrastructure deployment guide](infra/README.md#github-actions-deployment), including manual SQL access setup between releases. Deployment uses PowerShell and Azure CLI.

See [the infrastructure deployment guide](infra/README.md) for settings and the application deployment sequence. The frontend is hosted separately.

Configured capacity is **2 vCPU / 4 GiB per AgentHost session** and **2 vCPU / 4 GiB per API replica** (1–3 replicas). Background uses 1 vCPU / 2 GiB with one replica, and MarkItDown uses 0.5 vCPU / 1 GiB per replica (1–3 replicas). See [current configured capacity](infra/README.md#current-configured-capacity) for allocation sources and how to apply changes to existing deployments.

To inspect site access, open **Admin → Site access**, enter the privileged credentials and site URL, then select **View permissions**. Target application fields are not required for viewing. The table lists explicit application grants with application name, client ID, roles, and permission ID across all result pages; it does not list user/group permissions or broader tenant-wide application access. The secret clears after viewing, so re-enter it before saving or refreshing. Changing the site or tenant hides the previous results, and saving clears them so they can be reloaded.

## Configure and run

The Site access form pre-fills Tenant ID, Site URL, and Target client ID from the API's `SharePoint:TenantId`, `SiteHostname`/`SitePath`, and `ClientId` settings. These defaults remain editable, and Clear form restores them while clearing privileged credentials. Target application name is an optional display label; the client ID identifies the application. A blank name uses the target client ID as the label when creating a grant. Privileged client ID and secret must still be entered manually.

To revoke an explicit site grant, use its trash icon in **Admin → Site access → View permissions**. Confirm the application and site, re-enter the privileged client secret, and select **Delete permission**. The backend checks that the permission belongs exclusively to that application before calling Graph; successful deletion removes the row. Other grants and tenant-wide permissions can still provide access. Only Global Admins can perform this action.

Background also publishes a SQL heartbeat every 30 seconds. **Admin → Service health** shows its last heartbeat, last successful synchronization, last failure time, and earliest subscription expiry observed during a successful renewal check. A heartbeat at least two minutes old is **Unhealthy**; no record or an unreadable database is **Unknown**. A current heartbeat with an unresolved synchronization, Service Bus processing, or subscription-renewal failure (or an expired observed subscription) is **Degraded**. Successful work clears only that component's failure. An idle worker can be healthy without processing new documents; a current heartbeat alone does not prove an in-progress sync is making progress.

Apply migration `20261006004518_AddWorkerHeartbeat` through the database release workflow before releasing API and Background when automatic migrations are disabled. The worker updates one database-generated record per process lifetime; a restart creates another record, and the API reads the latest heartbeat. Timestamps describe that process's work; older records remain for inspection. API and worker must use the same environment database. Heartbeat write failures are logged and retried without stopping indexing. Only component names and timestamps are persisted, not exception bodies. An intentionally stopped worker becomes unhealthy after two minutes, and an old overlapping replica can remain visible until its heartbeat expires. No HTTP ingress is needed on Background.

**Admin → Service health** (`/admin?tab=health`) lets Global Admins and Global Reader Admins monitor MarkItDown and PageIndex. Checks run from the API server using each service's `Endpoint` and `HealthPath` settings (default `/health`), with a ten-second timeout per service. The tab shows status, response time, and the last check time, with manual refresh and optional automatic refresh every 30 seconds while open. Blank endpoints show **Not configured**. Health endpoints are anonymous; API keys and upstream response bodies are not exposed. A healthy result confirms the service returns `{"status":"ok"}`; it does not validate conversion, indexing, or model credentials.

The frontend's **Browse → Recycle bin** provides a preview, read-only listing of deleted items in the configured SharePoint site. It uses Graph beta and may require broader Graph application permissions than `Sites.Selected`; see the [frontend setup and limitations](frontend/README.md#running-it). Restore remains available through the view's link to SharePoint.

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
LocalWorkingDirectory__Directory
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

### Attachment storage limits

Administrators also see **System attachment storage** under **Attachment files → Storage usage**: total stored bytes, file count, unlinked-file bytes, and storage without an assigned creator. These totals include all users, legacy files, and every indexing state, independent of table filters or pagination. Unlinked and unassigned bytes are subsets of total usage and may overlap. The admin-only `GET /api/storage/attachments` endpoint supplies this summary; it measures application attachment records, not Azure account capacity or billing. Use **Refresh storage** to fetch current totals.

The **Users** page shows each user's attachment storage usage and limit. **Global Admin** can choose a user's **Manage storage** action to configure the limit in a dedicated popup; **Global Reader Admin** can view it. Enter a size in whole **GB** increments (the UI uses 1 GB = 1,073,741,824 bytes, consistent with its storage display), leave it blank for **Unlimited**, or enter **0** to block new uploads. Existing and newly provisioned accounts default to unlimited. Create a user first, then use Manage storage to set a quota. Editing account details preserves the existing storage limit. Each user can view their own usage under **Attachment files → Storage usage → My attachment storage**; Refresh reloads the current usage and limit.

Usage is the sum of retained attachment-file sizes belonging to the user's `CreatedById`, including files whose indexing failed. A file shared across branched conversations counts once. Legacy files without a creator are not attributed to a user. Deleting an orphan attachment releases its usage; deleting a conversation alone does not remove its stored files. Lowering a limit below current usage keeps existing files available and blocks new uploads until sufficient space is available. Reindexing and downloading do not consume additional quota. This quota covers original attachment bytes, not SharePoint files, search-index data, or total Azure storage billing.

The backend enforces quotas for every uploader, including Global Admin, in addition to the existing per-file upload size limit. It serializes concurrent uploads for the same user with a SQL Server user-row lock until the file write and metadata transaction commit. Rejected uploads return HTTP **409** with a storage-limit message before writing the blob. Failed uploads roll back their metadata and attempt blob cleanup; failures during indexing retain the uploaded file and its usage.

The API user responses expose `attachmentStorageLimitBytes` (`null` means unlimited) and `attachmentStorageUsedBytes`. **Global Admin** updates a quota through `PUT /api/users/{id}/storage`, sending only `attachmentStorageLimitBytes` and the user's current `concurrencyStamp`. Stale updates return HTTP 409. Account create/update payloads do not include storage settings; account edits preserve the existing quota and new users start unlimited. The `AddAttachmentStorageLimit` migration adds the nullable limit column to `AspNetUsers`. Restart the API to apply it when `SqlServer:AutoMigrate` is enabled, or apply the migration before startup when migrations are managed separately.

**Monthly chat token limits:** the Users page shows each user's current-month usage, today's usage, and a daily breakdown of input/output/total tokens. **Global Admin** can use **Manage tokens** to set a monthly limit in whole millions (1 = 1,000,000 tokens); **Global Reader Admin** can view usage. Blank means unlimited (the default); 0 blocks new chat turns. This setting has its own endpoint, `PUT /api/users/{id}/tokens`, accepting `{ monthlyTokenLimit, concurrencyStamp }`; the API stores the actual token count, and the UI converts millions to tokens. Account and storage edits preserve the token limit; changing it does not reset usage.

Users see **My chat token usage this month** in Chat, refreshed after a turn, on window focus, every minute, or manually. Usage counts chat model input and output tokens across tool-call rounds, excluding embeddings, indexing, and search. The authenticated user sending the turn is charged, including administrators chatting in someone else's conversation. Turns are assigned to the UTC day and month in which they start, and a failed or cancelled turn is charged for the model requests it made. At 00:00 UTC on the first day of each month, the new month's allowance starts at zero automatically; historical daily records remain stored without a reset job. The API profile exposes `monthlyTokenLimit`, `monthlyTokensUsed`, `tokenUsageResetsAtUtc`, and `dailyTokenUsage` for the current month.

New turns are refused with HTTP 429 once usage reaches the limit. A running turn may exceed its remaining allowance because actual usage is known after generation; later turns are blocked. A SQL Server session application lock permits only one active chat turn per user across API instances, preventing simultaneous requests from passing the same quota check. Readers can still fetch profiles and usage while a turn runs.

**Chat usage ledger:** `ChatTokenUsage` holds **one row per model request**, not one per turn. A turn is a tool-calling loop, so a turn that calls two tools is three rows sharing a `QuestionId`. Each row carries `Sequence` (the request's position in the turn, from zero), `ModelId`, provider-reported input/output/total tokens, and the `ToolNames` that response asked for. Skill calls also record `SkillNames` and `ScriptNames`, because the skill tools are named after the operation — `load_skill`, `read_skill_resource`, `run_skill_script` — and which skill a call is for lives in its `skillName` argument, with the script in `run_skill_script`'s `scriptName`. Those two argument names come from the agent framework and a rename there would quietly empty both columns, so `SkillToolArgumentNamesAreUnchanged` pins them against the schemas the tools publish. Missing provider usage stays null rather than being counted as a free request. This table is what monthly quotas and the Chat Usage report sum; deleting or branching conversations does not alter it, and repeat accounting for the same question does not double-count. Accounting begins when the feature is deployed; existing conversation totals are not backfilled, because branched histories can duplicate them. These totals are application usage, not an Azure billing report.

Rows are inserted as each response completes, in their own `DbContext` and on an independent 30-second timeout, rather than buffered until the turn ends — so **a turn that is cancelled or fails part way is still billed for the requests it made**, which the earlier per-turn ledger could not record. Every row of a turn carries the turn's *start* day and month, the period whose allowance was checked, so a turn running across midnight UTC is not split. The authenticated sender is charged, including an administrator chatting in someone else's conversation; only a request arriving with no user falls back to the conversation's creator. Both `ChatAgent:Mode` values write these rows, but from different processes: in `Local` the API writes them, and in `Foundry` the AgentHost writes them from inside the sandbox over its own SQL connection.

A per-request write failure is logged at error level and loses that request's tokens; it never fails the turn, since the request is answered and paid for either way. As a safety net the API writes the turn's reported total as a single row with `Sequence` −1 when the turn left **no** row at all — a sandbox that could not reach SQL, for instance — and does nothing when rows already exist, which is also what keeps a repeated turn from double-counting. A turn where only *some* writes failed keeps its partial rows and is under-billed by the difference.

Tokens are billed per request, not per tool, so a tool's real cost is split across two rows: the output tokens of the request that asked for it, and the input tokens of the next request, which had to read its result back. `load_skill` is the clearest case — the SKILL.md body it injects is paid for on the following request. Join on `QuestionId` and order by `Sequence` to see a whole turn. Image-description calls keep their own ledger and do not appear here.

The `ReplaceTurnUsageWithChatTokenUsage` migration replaces the old per-turn `UserTokenUsage` table with this one, carrying every existing row across as a single `Sequence` −1 row so month-to-date quotas are unchanged by the upgrade; the per-request breakdown cannot be reconstructed for historical turns. A turn whose message has since been deleted keeps its usage with an empty conversation ID. `RenameScriptPathsToScriptNames` then renames that one column in place with `sp_rename`, keeping the values. Apply both before serving requests, or restart with `SqlServer:AutoMigrate` enabled, and deploy the API and AgentHost together.

**Embedding usage ledger:** `EmbeddingTokenUsage` stores one row per successful embedding provider call across SharePoint and attachment indexing/reindexing, vector search, and hybrid search. Each row captures `EmbeddingModelId` (provider model, falling back to the requested model or configured deployment), `DeploymentId`, UTC `CreatedAtUtc`, operation, and provider-reported input/total tokens. Missing token usage stays null rather than being estimated. Available attribution includes application `UserId`, `ConversationId`, `QuestionId`, SharePoint `DriveId`/`FileId`, attachment-file `AttachmentId`, `ScanId`, chunk number, and trace ID. Hosted chat forwards the initiating application user ID; background work may have no user. Attachment indexing before a message is created has no question ID; shared attachments are not assigned an arbitrary question.

Rows are saved immediately after generation, before search/index writes, and retained independently of source deletion. Reindexing records additional usage rather than replacing earlier rows. A failed provider call without a response cannot be counted; database recording failures propagate rather than silently losing usage. Embedding usage does not affect chat-token quotas. Apply `AddEmbeddingTokenUsage` before serving embedding requests, or restart with `SqlServer:AutoMigrate` enabled. Deploy API, worker, and AgentHost together.

**Token usage page:** Global Admin and Global Reader Admin can open **Token usage** (`/token-usage`) with chat, embedding, and Content Safety tabs. **Chat Usage** reports chat input, output, and total tokens, recorded turns and the model requests they took, per-turn averages, active users, daily trends, and model/user breakdowns. Turns are counted as distinct question IDs and requests as ledger rows, so the two differ whenever tools were called. Filter by an inclusive UTC date range (up to 366 days), model (including unknown historical models), user name/email/ID, or question ID. Chat reports use the ledger's UTC usage day, matching monthly quotas, rather than the time the record was saved. The request log is paginated and has **Tools**, **Skills**, and **Scripts** columns alongside the token counts, so a skill run is visible as the row that called it. The headings are plural because one response can ask for several calls at once. A request that answered instead of calling a tool shows "Answered", and a whole-turn fallback row shows "Whole turn"; a dash means the row called nothing of that kind. Several calls in one response appear comma-separated, with the full value on hover. Details opens every field with copyable IDs, including `Sequence`, which orders the requests within a turn. A row with no recorded user counts toward the totals but is left out of the per-user breakdown. Embedding tokens remain excluded from chat totals.

**Embedding Usage** retains token totals, call counts, daily consumption, model/activity breakdowns, and the top 100 users by tokens. Its filters also include operation, unattributed activity, and exact source/reference ID. Charts support filtering by day, model, and activity; individual calls have detailed attribution popups. Unreported usage is counted separately and excluded from token sums. `GET /api/usage/tokens` and `GET /api/usage/embeddings` filter, aggregate, and paginate on the server; ordinary User accounts cannot access either report. The former `/embedding-usage` URL redirects to `/token-usage?tab=embeddings`.

### Azure Content Safety

The infrastructure templates now provision Content Safety by default (`deployContentSafety=true`), expose `contentSafetyEndpoint` and `contentSafetyResourceId`, and configure managed-identity access for API and AgentHost. See [Content Safety deployment](infra/README.md#content-safety) for existing apps and separate AgentHost setup. Set `ContentSafety:Enabled` to `true` and `ContentSafety:Endpoint` to your Azure Content Safety resource's HTTPS endpoint. The feature defaults to disabled until configured. For managed identity, keep `UseManagedIdentity: true`, optionally set `ManagedIdentityClientId`, and grant that identity **Cognitive Services User** on the Content Safety resource. For local development, set `UseManagedIdentity: false` and supply `ContentSafety:ApiKey` through user secrets or `ContentSafety__ApiKey`; do not commit keys. Apply `AddContentSafetyUsage` (or restart with automatic migrations enabled) before using the report. Configure API and AgentHost consistently.

The [text analysis API](https://learn.microsoft.com/en-us/rest/api/contentsafety/text-operations/analyze-text?view=rest-contentsafety-2024-09-01) checks user messages before they enter chat history, assistant responses before display/storage, and extracted attachment text before embeddings/indexing on upload or reindex. Assistant output and model-generated status messages are withheld until the answer passes; live answer streaming is disabled while safety is enabled. Blocked answers still incur chat model usage, which remains recorded. Reindex existing attachments to assess content uploaded before enabling this feature. This integration checks extracted text, not images, and does not implement Prompt Shields or groundedness detection.

`HateThreshold`, `SexualThreshold`, `ViolenceThreshold`, and `SelfHarmThreshold` default to 4 on the 0–7 scale; scores at or above the configured threshold block the content. Provider errors, timeouts, or malformed responses stop processing rather than bypassing the check. `TimeoutSeconds` defaults to 15 per HTTP request. Text exceeding the [10,000-character request limit](https://learn.microsoft.com/en-us/azure/ai-services/content-safety/language-support) is checked in consecutive chunks without splitting surrogate pairs; this loses cross-chunk context. A block stops further chunks.

`ContentSafetyUsage` records each request's UTC timestamp, assessment ID, available user/conversation/question/attachment IDs, outcome, character count, estimated text records, category scores, thresholds, duration, HTTP status, and trace ID. It does not store submitted text, response bodies, or credentials. Checks made before a question exists are linked after the allowed question is saved; blocked prompts have no question ID. Records survive source deletion. The **Content Safety** tab under **Token usage** is available to Global Admin and Global Reader Admin, with date/activity/outcome filters, daily totals and a detailed paginated log. Its endpoint is `GET /api/usage/content-safety`.

Content Safety is tracked separately from chat/embedding tokens and quotas. Completed allowed/blocked checks contribute estimated text records using one record per 1,000 UTF-16 code units, rounded up per request; see [Azure pricing](https://azure.microsoft.com/en-us/pricing/details/content-safety/). These are application estimates, not billing reconciliation; failed/cancelled requests may still have reached Azure. Interrupted processes can leave `Started` rows.

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

See [OpenTelemetry configuration](docs/telemetry.md) for local Aspire dashboard
export, dev/test Azure Monitor export, and finding a chat turn by its trace ID.

Chat messages store the original chat request's W3C `TraceId` on both the question
and response. The message API and streamed `started`/`completed` events expose it
as `traceId`. Use that value to search your tracing backend for the turn and its
instrumented downstream requests. Exporters, sampling, and cross-service context
propagation must be configured for those spans to be available. Historical messages
have no trace ID; branching preserves the source messages' original IDs. Apply the
`AddChatMessageTraceId` migration before running this version against an existing database.

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

Chat responses also expose a separate `embeddingTokenCount`: the provider-reported query embedding tokens used by SharePoint and attachment search during that response. Chat displays this alongside each answer and shows the accumulated embedding count in the conversation list. These counts survive reloads and are copied with the selected history when branching. They do not include file-indexing embeddings and are **not added to chat total tokens or monthly token quotas**. Historical chat rows start at zero; missing provider usage contributes zero rather than an estimate. Apply `AddChatEmbeddingTokenCounts` (or restart with automatic migrations enabled) to add the message and conversation columns. Deploy the API and AgentHost together for hosted execution.

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

An agent built with the [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) (`Microsoft.Agents.AI.OpenAI`) answers questions about the indexed library. It runs on `AzureOpenAI:ChatDeployment` — `gpt-5-mini` by default, on the same resource and endpoint as the embedding deployment — and is given tools of its own: `search_sharepoint_documents`, a hybrid search over the SharePoint index; `search_attachments`, a hybrid search limited to indexed files attached to the current conversation; `download_sharepoint_file`, which copies a SharePoint search result onto the local file system; `upload_sharepoint_file`, which sends the local copy back over the document; and a set of [local file tools](#local-file-tools) for the working directory those downloads land in. Reading a whole document or editing one is left to the deployed [agent skills](#chat-agent-skills), whose tools are added to those. Its instructions tell it to search before answering questions about document or attachment content.

Conversations and messages are stored in the same SQL Server database, in `ChatConversations` and `ChatMessages`, which the same migration creates as the worker's tables. Deleting a conversation cascades to its messages through the foreign key. Each turn replays the stored history — the last 40 messages — so the agent needs no state of its own between requests, and the documents the tool retrieved are saved with the answer as citations.

The chat composer also accepts up to ten attachments per message. An attachment is first stored in the configured Azure Blob container and recorded in `ChatMessageAttachmentFiles`, then converted to Markdown, chunked, embedded, and written to the separate `AzureSearch:UploadIndexName` index. Only successfully indexed attachment-file IDs are sent with a chat turn. `ChatMessageAttachments` joins those IDs to the stored user message; after that row is saved, its generated ID is written back to `ChatMessageAttachmentFiles.ChatMessageAttachmentId`. The agent receives attachment names as context and retrieves indexed excerpts with `search_attachments` when needed, including on later turns. The tool derives allowed file IDs from message links in the current conversation and applies them as a search-index filter. The Attachment Files page shows `NotStarted`, `Indexing`, `Indexed`, and `Failed` states, links attached files to their conversation, and supports download, reindex, and orphan deletion. Failed or unattached files remain available for retry or cleanup.

Each replayed message includes the IDs and names of its attachments. `search_attachments` accepts an optional `attachmentId` to target one file when names repeat; the API only searches it if that ID belongs to the current conversation.

The agent uses `download_attachment(attachmentId)` to download original conversation attachments. Use `convert_to_markdown(path)` for a downloaded SharePoint file, attachment, or generated document in the sandbox. It returns `localPath` for `read_text(path, startLine, endLine)` and other file tools. Conversion saves a new Markdown file under `Converted/<unique folder>/`, preserving originals and indexed Markdown blobs. Configured text files should be read directly; images use `describe_image`. Paths are restricted to the working directory, with existing file size and symbolic-link checks. Conversion runs on demand through MarkItDown and does not reuse indexed Markdown.

`convert_to_markdown(path, destinationPath?, overwrite=false)` optionally saves to a specified `.md` file inside the sandbox, creating parent folders as needed. Without a destination it uses a unique folder under `Converted/`. Existing destinations are rejected unless `overwrite` is explicitly enabled. Invalid destinations are rejected before calling the conversion service. Use `move_file` to relocate the result afterward if needed.

Use `read_text(path, startLine?, endLine?)` with a path returned by download or conversion tools, or any path inside the [working directory](#local-file-tools). On later turns, call the download tool again to reuse its cache, or read the file by its path in the working directory. Paths outside it and symbolic links are rejected. Reads support UTF-8 and Unicode BOMs, a 50 MB file limit, and one-based inclusive line ranges: omitted ranges return up to 200 lines, explicit ranges up to 500, and `nextLine` indicates where to continue. Use `convert_to_markdown` for binary Office files before reading them.

`Uploads:TextFileExtensions` defines formats that bypass MarkItDown during indexing and reindexing (defaults: `.txt`, `.md`, `.json`, `.csv`, case-insensitive). The Attachment Files page reads this same list from `/api/attachment-files/options` to hide View Markdown for text files. Their text is read directly, preserving formatting and line breaks, using UTF-8 by default or a Unicode BOM when present. The same text is indexed and saved in the Markdown blob; JSON and CSV are not reformatted or wrapped in Markdown. Other formats still use MarkItDown. Adding a text format does not allow it for upload automatically; also add it to `Uploads:AllowedFileExtensions`.

Chat uploads allow `.pdf`, `.docx`, `.pptx`, `.xlsx`, `.txt`, `.md`, `.json`, `.csv`, `.png`, `.jpg`, `.jpeg`, `.gif`, and `.webp` by default. Configure `Uploads:AllowedFileExtensions` independently of the SharePoint indexing allowlist. Extension checks are case-insensitive and enforced before storing a new upload; an empty list blocks all new uploads. The chat file picker loads the same list from the API. Existing attachments remain readable and reindexable. Non-image files must still extract and index successfully before being attached to a message.

PDF indexing uses Azure Document Intelligence for both SharePoint documents and attachments, including OCR for scanned pages. Configure `DocumentIntelligence:Endpoint` and either managed identity access or an API key on the API and Background services. The default model is `prebuilt-read`; extracted text enters the existing chunking, embedding, and search pipeline. PDF attachments retain their original download and store extracted text for **View indexed text**. Missing configuration or extraction errors fail PDF indexing rather than reporting metadata-only success. Include `.pdf` in custom `Processor:AllowedFileExtensions` and `Uploads:AllowedFileExtensions` overrides. Run a full synchronization to discover previously excluded SharePoint PDFs, or reindex previously tracked PDFs; reindex existing PDF attachments to regenerate their content.

Paste a clipboard image into the chat message box with Ctrl+V (Cmd+V on macOS) to upload it as an attachment. Normal text paste is unchanged. Pasted images use the same extension restrictions, storage quota, and ten-attachment limit as the file picker. Images identified by `Uploads:ImageFileExtensions` retain their original binary download and are indexed on upload and reindex. PNG, JPEG, WebP, and GIF receive an image description from the API's configured Azure OpenAI chat deployment. PNG and JPEG also use Document Intelligence OCR when its endpoint is configured; configured BMP/TIFF attachments require OCR. The combined description and extracted text pass through the normal text safety checks, chunking, embeddings, and search indexing, with a stored Markdown representation available through **View indexed text**. Image-description and embedding usage are recorded separately. Processing failures mark indexing as failed rather than silently reporting zero chunks. Use **Reindex** for existing image attachments previously indexed with zero chunks. Sandbox `describe_image` remains an independent, on-demand operation using the conversation's selected model.

Attachment indexing reads text files directly or converts other formats once, uses the resulting text for search chunks, then stores that exact text in `markdown-cache/{attachmentId:N}/content.md` in `Uploads:ContainerName`. Successful reindexing repeats extraction and overwrites that same Markdown blob before marking the attachment Indexed. Agent reads and View Markdown use the stored blob without reconverting. Existing attachments without a Markdown blob require one reindex; files still indexing or whose indexing failed cannot serve Markdown. Search and Blob Storage writes are separate operations; a failure marks the attachment Failed and requires retrying reindexing.

Original downloads reuse a local cache under `Uploads:CacheDirectory` (default: the process temporary directory plus `SharePointAgent/attachments`). Local Markdown copies are validated against the shared blob ETag on each read, so a replacement is picked up by other hosts. Deleting an orphan attachment removes its original blob, Markdown blob, and the deleting host's local cache. Storage quotas count original attachment bytes only, excluding derived Markdown blobs and local copies.

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

### Local file tools

The chat UI's **Workspace** tab shows workspace details and an inline **Files in the sandbox** browser supporting directory creation, picker/drop uploads, and rename, copy, move, and delete for files and directories. These operations work in both Local and Foundry mode without a model call. See [sandbox file management](frontend/README.md#sandbox-file-management) for limits, permissions, and destination paths. This changes sandbox files only; it does not write changes back to SharePoint.

Everything the agent puts on disk lands in one directory — `LocalWorkingDirectory:Directory`, which in the Foundry host is the per-session sandbox a [workspace](backend/SharePointAgent.AgentHost/README.md#workspaces-one-sandbox-for-several-conversations) shares. Anything fetched from elsewhere goes under `Downloads`: library documents in `Downloads/SharePoint/<item id>/`, conversation attachments and their converted Markdown in `Downloads/Attachments/<attachment id>/`. That leaves the top level for what the agent writes itself, so a listing tells its own work from copies of other people's documents. These tools work anywhere inside the directory, on paths relative to its top:

| Tool | What it does |
| --- | --- |
| `list_files(path?, recursive?)` | Lists the directory, or one inside it, with size and modified time for each entry. At most 500 entries, and it says when it truncated. Where the agent starts when a file is mentioned without a path |
| `read_text(path, startLine?, endLine?)` | Reads a text file, in line ranges. See [reading text](#chat-assistant) for its limits |
| `write_text_file(path, content, overwrite?)` | Writes a text file, creating the directories it needs. Refuses to replace an existing file unless asked. 5 MB limit |
| `create_directory(path)` | Creates a directory and its parents |
| `move_file(source, destination, overwrite?)` | Moves or renames a file or directory. A destination that is an existing directory means "into it" |
| `copy_file(source, destination, overwrite?)` | Copies a file, for working on a copy while keeping the downloaded original |
| `delete_file(path, recursive?)` | Deletes a file, or a directory when `recursive` is passed |
| `zip_files(paths, destination, overwrite?)` | Packs files and directories into a `.zip`, with entry names relative to the top of the directory. An archive written inside a directory being zipped does not include itself |
| `unzip_file(path, destination, overwrite?)` | Extracts a `.zip` into a directory. The whole archive is refused if any entry is absolute, uses `..`, names a reserved `.agent-*` file, or would replace an existing file without `overwrite`, or if it holds more than 10,000 entries or expands past 1 GB. Extraction counts the bytes it actually writes rather than trusting the archive's declared sizes |
| `execute_script(language, code? \| scriptPath?, arguments?, directory?, timeoutSeconds?)` | Runs PowerShell, Python, Node.js, or Bash in the working directory and returns the exit code, stdout, and stderr (each cut at 20,000 characters). **Registered only when `AgentWorkspace:Mode` is `DynamicSessions` or `Sandboxes`**, where the code runs in the [isolated session or sandbox](infra/README.md#isolated-code-execution) without the application's credentials. With the Local working directory, and in the Foundry host, the tool is never offered, because the code would run on the host itself. The timeout defaults to, and is capped at, `AgentWorkspace:ScriptTimeoutSeconds` |

You can read that directory yourself, without asking the agent: `GET /api/chat/conversations/{id}/files?path=&recursive=` to list it and `…/files/content?path=` to fetch one file, or **Browse files** in the chat header's workspace panel, which gives you a breadcrumb explorer with preview and download. It spends no tokens and leaves the conversation untouched in either execution mode — see [inspecting a sandbox](backend/SharePointAgent.AgentHost/README.md#workspace-rules) for how the Foundry side works.

**The directory is the whole of the file system the agent can reach.** Every path argument goes through one resolver, which takes a relative path from the root and accepts an absolute one only when it is already inside — so the paths the download tools return can be handed straight back. A path that resolves outside is refused, whatever spelling it arrives in: `..`, an absolute path elsewhere, or a directory whose name merely begins with the root's. Symbolic links are refused anywhere along the chain, since a link inside the directory can still point out of it. The root itself cannot be deleted.

Nothing here touches SharePoint. Writing, moving, and deleting change local copies only, and `upload_sharepoint_file` remains the only tool other people see the effect of. Deleting is not recoverable the way an upload is: a downloaded file must be downloaded again, and anything written locally and not uploaded is gone, so the instructions tell the agent to delete only what it was asked to and to say what it removed. Binary Office files are still the [skills'](#chat-agent-skills) business — `write_text_file` cannot produce a `.docx`, `.xlsx`, or `.pptx`.

These tools are described to the model by their own tool definitions, so they work with any agent. The wording in `AgentDefaults.Instructions` that introduces them reaches **new** agents only; an agent already in the database keeps the instructions it was saved with, so add a line to it in the Agents editor if you want it to mention them.

### Downloading and refreshing a file

The instructions call for `download_sharepoint_file` when the user asks for a local copy of a document, and also when they ask to edit, change, or update one — a local copy is where editing starts, so the agent fetches the file and reports where it went. Neither download nor refresh writes anything back; [`upload_sharepoint_file`](#uploading-a-file-back) is the only tool that does.

`download_sharepoint_file` takes a fileId returned by search_sharepoint_documents and always streams fresh content from Microsoft Graph. By default it creates `<LocalWorkingDirectory:Directory>/Downloads/SharePoint/<unique folder>/<file name>`. It checks protection and returns a readable localPath. Earlier downloads are not reused or overwritten.

Microsoft Information Protection (MIP) inspects the file contents, so a sensitivity label without encryption leaves the file unchanged. For an encrypted file, the tool authenticates with the existing `SharePoint:TenantId`, `ClientId`, and `ClientSecret` and requires `EXTRACT` rights before decrypting. It retains the encrypted original beside the local copy with a `.mip-protected` suffix. Both the downloaded and decrypted sizes are limited by `LocalWorkingDirectory:Downloads:MaxFileBytes`. A failed conversion to plaintext does not publish a partial download or replace a previous local copy.

The app registration also needs Azure Rights Management authorization; Graph file permissions alone do not grant decryption rights. This implementation uses the application's identity, not the conversation user's identity. App-only access to tenant-protected content can require the administrator-approved `Content.SuperUser` application permission, which grants broad access; user-delegated decryption is not implemented. See Microsoft's [MIP permission reference](https://learn.microsoft.com/en-us/information-protection/develop/concept-api-permissions). No tenant permissions or document labels are changed by this application.

Windows uses the MIP native libraries supplied by NuGet and requires the matching Visual C++ runtime. Linux builds use the Ubuntu 24.04 MIP package; the API, background worker, and agent-host containers install its native dependencies. See [MIP platform setup](https://learn.microsoft.com/en-us/information-protection/develop/setup-configure-mip). This check applies to the agent's `download_sharepoint_file` tools and the shared indexing pipeline, including manual reindexing and delta synchronization. Indexing downloads the current SharePoint version into an isolated temporary folder, decrypts before extraction, and deletes temporary plaintext and protected originals on success or failure. `Processor:MaxFileBytes` limits both downloaded and decrypted content. View Markdown and Office previews for indexed SharePoint files use the same readable-download path: authorized decryption happens in a temporary folder, `LocalWorkingDirectory:Downloads:MaxFileBytes` bounds both downloaded and decrypted content, and temporary files are cleaned up on success or failure. Preview responses use `Cache-Control: no-store`; missing extraction rights return HTTP 403 with an actionable error. Chat attachment conversion still uses its existing download path.

`download_sharepoint_file(fileId, destinationPath?, overwrite=false)` optionally saves to an explicit sandbox file path, creating parent folders as needed. Existing destinations are rejected unless overwrite=true. Every successful call fetches fresh content, regardless of overwrite. Downloads are staged and published only when complete, preserving the previous file if downloading or decryption fails. Indexing, reindexing, and application previews continue downloading directly; there is no SharePoint file cache.

| Setting | Default | Effect |
| --- | --- | --- |
| `LocalWorkingDirectory:Directory` | empty | The one directory the agent works in, and everything its [file tools](#local-file-tools) can reach. Downloads are saved inside it, in `Downloads/SharePoint` and `Downloads/Attachments`, which are derived rather than configured. A relative path resolves against the process working directory; empty means `sharepoint-agent` under the system temporary directory |
| `LocalWorkingDirectory:Downloads:MaxFileBytes` | 20971520 | Largest file `download_sharepoint_file` will fetch, and the largest `upload_sharepoint_file` will send back. A larger file is refused, and the model reports that instead of a path |

### Uploading a file back

`upload_sharepoint_file` refuses a local copy with a retained `.mip-protected` original: uploading plaintext would remove the original document's protection. Protection-preserving upload is not implemented; use a protection-aware Office application to save edits to these documents.

`upload_sharepoint_file(fileId, sourcePath)` requires the exact sandbox file to upload and the target fileId from search results. There is no automatic cache fallback. Relative and absolute sandbox paths are accepted; empty, missing, directory, out-of-root, and symbolic-link paths are rejected. Microsoft Graph takes it in one request up to 4 MB and through an upload session in slices above that, so a large file is streamed rather than held in memory, and `LocalWorkingDirectory:Downloads:MaxFileBytes` caps it either way. **SharePoint keeps the previous file as a version rather than losing it**, so an unwanted upload is recoverable from the document's version history. The source and other downloaded files are left in place. A subsequent download always fetches the current SharePoint version.

The new version reaches the index the ordinary way, with no special case for it: SharePoint notifies the webhook, the next delta pass sees a `cTag` that does not match the one recorded for the file, and the worker extracts, chunks, and embeds it again.

Uploading changes what other people see, so the instructions hold the agent to an explicit request — "save it back", "upload it", "publish it" — and tell it to stop after an edit and offer, rather than upload because an edit finished, and to ask when the request is ambiguous. The tool accepts only a `fileId` from one of the same turn's searches, so the model cannot name an arbitrary drive item. The source may be any explicitly specified file inside the sandbox, including a generated document.

Two requirements that indexing alone does not give you:

- **Write access for the Entra application.** Indexing needs only read — `Sites.Selected` with a read grant, or `Sites.Read.All`. Uploading needs write: a `Sites.Selected` grant of `write`, or `Sites.ReadWrite.All`. Without it Graph rejects the upload and the model reports the rejection.
- **A deliberate decision about who may trigger it.** The permission filter behind `search_sharepoint_documents` is a *read* filter: it says the user may see the document, not that they may change it. Any file a conversation can search, it can overwrite. With no `userId` on the conversation that is the whole index. Restrict enterprise application assignment to trusted operators before granting the application write access.

### Editing a downloaded file

For running the same agent inside the API or in a Foundry Hosted Agent sandbox, see [agent hosting options](backend/SharePointAgent.AgentHost/README.md). Both modes retain SQL conversation history, streamed answers, and tool-status reports; `ChatAgent:Mode` defaults to `Local`.

Downloaded and edited files live in the sandbox, and a conversation has one of its own. Grouping conversations into a **workspace** gives them one sandbox between them, so a document downloaded and edited in one conversation is already on disk in the next — the way to work on the same set of documents across several chats instead of downloading them again in each. Membership is optional, and it is chosen when the conversation is started rather than changed later, so a conversation keeps the same files for its whole life. A workspace can also carry **rules** — instructions added to the agent's own for every conversation in it, such as how to cite or when not to upload — which are read each turn, so editing them reaches conversations already under way. See [workspaces](backend/SharePointAgent.AgentHost/README.md#workspaces-one-sandbox-for-several-conversations) and [workspace rules](backend/SharePointAgent.AgentHost/README.md#workspace-rules).

The agent has no built-in tool that reads or writes the contents of a `.docx`, `.xlsx`, or `.pptx` file; the [local file tools](#local-file-tools) move, copy, and delete such a file but cannot look inside it. Reading a document in full or editing one comes from a deployed [agent skill](#chat-agent-skills) for that format, which runs a script on this host. The pairing is `download_sharepoint_file` first, then the skill on the `localPath` it returned; the instructions say as much, and that an edit to the local copy is not a change in SharePoint until `upload_sharepoint_file` sends it back. With no such skill deployed, the assistant can search, download, refresh, and upload but not read into or change a document, and its instructions tell it to say so rather than guess at the contents.

The instructions also require added or changed content to match the style of the document around it: read the neighbouring paragraphs, rows, or shapes and their properties first, reuse the style, font, spacing, list format, table formatting, and slide layout they use rather than leaving default-formatted content behind, follow the document's own wording conventions, and read the result back before reporting the edit as done.

The download tool only accepts a `fileId` that one of the same turn's searches returned, so the permission filter that trims those results also bounds what can be downloaded — the model cannot reach a file by inventing an ID. That still means **any file a conversation can search, it can also write to the host's file system**, and with no `userId` on the conversation that is the whole index. The file name is sanitized and the resolved path is checked to be inside the working directory's `Downloads/SharePoint` folder, so a name coming back from SharePoint cannot write outside it. Failures — a file over the limit, a rejection from Graph, a disk error — come back to the model as a message rather than failing the turn.

## Front end

### Continuous integration

GitHub Actions runs these workflows on pushes and pull requests affecting their respective directories. Both can also be started manually from the **Actions** tab and require no Azure credentials:

| Workflow | Checks | Artifacts |
| --- | --- | --- |
| [backend-ci.yml](.github/workflows/backend-ci.yml) | Restore and Release-build the .NET 10 solution, then run backend tests on Ubuntu 24.04 with MIP native dependencies; in parallel, build the `api`, `agenthost`, `background` and `markitdown` container images with Buildx | TRX test results |
| [frontend-ci.yml](.github/workflows/frontend-ci.yml) | Install locked dependencies with Node.js 22 and `npm ci`, then type-check and build with Vite | Production `dist` files |

Artifacts are retained for 14 days. These workflows validate builds only; they do not deploy the application. The container images are built but never pushed or loaded, so no registry credentials are needed; layers are cached per image in the GitHub Actions cache. The release workflows build the same Dockerfiles again through `az acr build`.

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
- Only files whose extension is in `Processor:AllowedFileExtensions` are indexed; `appsettings.json` ships with `.pdf`, `.docx`, `.pptx`, `.xlsx`, `.txt`, and `.md`. Entries match case-insensitively, with or without a leading dot, and the worker refuses to start on an empty list rather than silently indexing nothing.
- A file outside the allow list has any previously indexed chunks removed, so narrowing the list or renaming a file to a disallowed extension cleans the index on the next pass rather than leaving stale documents behind. The removal is unconditional rather than driven by the metadata table, so it also cleans up documents indexed before metadata tracking was enabled.
- DOCX, PPTX, and XLSX are converted to markdown by the MarkItDown service at `MarkItDown:Endpoint`, which keeps headings, lists, and tables in the indexed text. There is no local fallback: a conversion that fails leaves the file unindexed and the Service Bus message unsettled, so the normal retry path applies, and the worker refuses to start without an endpoint.
- The worker probes `MarkItDown:HealthPath` (`/health`) as it starts and every `MarkItDown:HealthCheckMinutes` afterwards, with a 10 second timeout of its own rather than the conversion timeout. Only transitions are logged, so a healthy service is reported once and an outage logs one warning until it recovers. The probe reports and nothing more — indexing is not gated on it, and `MarkItDown:HealthCheckEnabled` turns it off.

### Voice dictation

The chat composer has a microphone button when `AzureOpenAI:TranscriptionDeployment` names a speech-to-text deployment (for example `gpt-4o-transcribe`) on the same Azure OpenAI resource; leave it empty to hide the button. Choosing it records up to five minutes in the browser (stop, cancel, or the time limit ends the recording and releases the microphone), sends the recording to `POST /api/chat/transcriptions`, and adds the transcript to the message box for the user to review and edit. Nothing is sent to the agent until the user chooses **Send**, so the normal content-safety check applies to the final text. `GET /api/chat/transcriptions/options` reports whether dictation is enabled and its limits.

The API calls `/openai/deployments/{TranscriptionDeployment}/audio/transcriptions?api-version={TranscriptionApiVersion}` (default `2025-03-01-preview`, the minimum for gpt-4o transcription models) directly over REST, using the same managed identity or API key as the other Azure OpenAI calls. Transcription models are offered in fewer regions than chat models; when the deployment is on a different resource, set `AzureOpenAI:TranscriptionEndpoint` to that resource (and `AzureOpenAI:TranscriptionApiKey` when using keys, otherwise grant the API's managed identity the Cognitive Services OpenAI User role on it). Both default to `Endpoint` and `ApiKey`. Recordings (WebM, Ogg, MP4, MP3, or WAV, up to 25 MB) and transcripts are not stored. Each transcription records the user, deployment, audio size, and provider-reported tokens in `TranscriptionTokenUsage`, plus the audio duration: Whisper-style deployments report it, and otherwise the recording length measured by the browser (sent as `?durationSeconds=`, clamped to the five-minute limit and used for reporting only) is stored; the tokens count toward the user's monthly limit and daily usage, and a user over the limit cannot dictate. The `AddTranscriptionTokenUsage` migration creates the table. The **Token usage → Voice** tab (`/token-usage?tab=voice`, API `GET /api/usage/transcriptions`) reports this ledger for Global Admin and Global Reader Admin users, with UTC date, model, and user (name, email, or ID) filters, users shown by full name with the email on hover; calls, input/output/total tokens, audio received, and unreported-usage counts; a daily chart and totals; a per-model breakdown; and a paginated call log. Browsers ask for microphone permission the first time; the page must be served over HTTPS (or localhost).

### Image attachment descriptions

The agent can call `recognize_text(filePath)` to OCR a PNG, JPEG, BMP, or TIFF image inside the sandbox. Download remote images first, then pass their `localPath`. The tool uses the existing `DocumentIntelligence` endpoint, credentials, API version, and model (`prebuilt-read` by default) in both API and AgentHost. It returns `{ filePath, text }`; text is empty if no text is detected. Sandbox boundaries and file-read size limits apply. OCR does not call the chat model or consume chat model tokens. Use `describe_image` for visual interpretation.

The agent can call `describe_image(filePath, focus?)` when it needs to understand a sandbox image. Download attachments first and pass the returned localPath. The tool reads the specified file inside the sandbox and uses the conversation's selected Azure OpenAI chat deployment, which must support image input. No Markdown or embeddings are generated. Image descriptions are treated as untrusted document content.

Each completed vision call records the configured agent model name as `ModelId` (matching chat token usage), user, conversation, question, attachment, timestamp, and provider-reported input/output/total tokens in `ImageDescriptionTokenUsage`. Missing provider usage is stored as null. Recording uses an independent timeout so a completed call can be accounted for after request cancellation; the ledger survives subsequent turn failures and conversation deletion. New image-description calls are excluded from chat turn, conversation, and Chat Usage report totals. Their independent ledger counts toward the user monthly quota and daily/model quota summaries, including completed image calls whose parent turn fails or is cancelled. Embeddings remain excluded. Monthly accounting sums the chat and image-description ledgers independently, without a per-call inclusion flag. The consolidated `AddImageDescriptionTokenUsage` migration creates the table with `ModelId` only and is applied by the existing startup migration service. The **Token usage → Image Description** tab reports this ledger for Global Admin and Global Reader Admin users. It provides UTC date, model, user (name, email, or ID), and attachment ID filters, shows users by full name with the email on hover; input/output/total tokens and unreported-usage counts; daily charts and totals; model breakdowns; and a paginated call log with attribution details. The report API is `GET /api/usage/image-descriptions`. New image-description usage is independent of chat totals. Legacy image calls may still overlap historical chat totals.

Image-description usage records also retain the exact system prompt, user text sent to the model, and returned description. The Image Description details popup displays these as plain text with preserved line breaks and copy controls. Historical records show Not recorded; image bytes are not duplicated in the usage ledger. The `AddImageDescriptionText` migration adds these nullable fields through the existing startup migration service.

### Chat agent skills

The shared chat agent uses `UseToolApproval` with `AgentSkillsProvider.AllToolsAutoApprovalRule`, matching the reference. This automatically handles approval requests from skill tools in both API and AgentHost execution; no interactive approval prompt is required.

Both local API execution and AgentHost execution load the shared `backend/skills` folder through `AgentSkillsProvider`, following the Practical.MicrosoftAgentFramework reference. Build and publish copy it to `skills` beside each executable; discovery uses `AppContext.BaseDirectory`, independent of the working directory. The AgentHost Docker build includes the shared folder as well. Add skills under `backend/skills/<name>/SKILL.md` and redeploy both hosts.

The bundled `dns-lookup` skill follows the reference provider/script pattern, with a cross-platform `scripts/resolve-dns.ps1`. The provider discovers the skill and invokes its PowerShell script through the supplied runner. Arguments are passed as an array through `ProcessStartInfo.ArgumentList`; output and script errors are captured, with cancellation and a 30-second timeout. The runner supports `.ps1`, `.py` and Node.js (`.js`, `.mjs`, `.cjs`) scripts. Python scripts use Python 3 (`python.exe` on Windows, `python3` on Linux) with UTF-8 output and the same argument handling, cancellation, and timeout. Node.js scripts run through `node` (`node.exe` on Windows) with UTF-8 output and read their arguments from `process.argv.slice(2)`; `.mjs` is treated as an ES module and `.cjs` as CommonJS, while `.js` follows the nearest `package.json` `type` field. Both Docker images include Python 3 and Node.js; Windows hosts require Python 3 and Node.js on PATH. Additional Python packages or npm modules required by a skill must be installed separately. No separate DNS function tool or DNS library is required.

DNS execution uses Windows PowerShell and `Resolve-DnsName` on Windows, or PowerShell 7 (`pwsh`) and `dig` on Linux. Both API and AgentHost Docker images copy PowerShell from the .NET SDK build stage and install `dnsutils`. Linux installations outside Docker need PowerShell 7 and `dnsutils` or `bind-utils`. The script supports A, AAAA, MX, TXT, NS and CNAME, optional IPv4/IPv6 resolvers, and always emits a JSON array on success. DNS results reflect that host's network/resolver. Only deploy trusted skills and scripts.
