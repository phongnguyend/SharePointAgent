# SharePoint Agent

A React front end for `SharePointAgent.Api`. It shows the worker's SQL Server state — the
`SharePointIndexedFiles` and `SharePointDeltaState` tables — and runs the three retrieval strategies
against the search index, side by side.

## API clients

API functions live in `src/api/` grouped by feature: `adminClient.ts`, `agentClient.ts`,
`attachmentClient.ts`, `chatClient.ts`, `searchClient.ts`, `sharePointClient.ts`,
`signatureClient.ts`, and `usageClient.ts`. Shared authenticated JSON requests, downloads,
query strings, and API errors live in `http.ts`; shared response types remain in `types.ts`.

Import from the feature client when adding code. `client.ts` re-exports the existing public
API so current imports continue to work. Feature clients should import shared helpers from
`http.ts`, not through `client.ts`.

## Stylesheets

Page and component styles live beside their TSX files, such as `pages/ChatPage.css`,
`components/Modal.css`, and `components/Toast.css`. Theme tokens, base elements, and
shared layout/control utilities live in `styles/shared.css`; application navigation
and shell rules live in `App.css`. Shared visual components also supply styles used
by their consumers (for example, usage reports share `EmbeddingUsageReport.css`).

`src/styles.css` is the central import entry point loaded by `main.tsx`. Keep its
shared-first import order stable so the cascade does not depend on route loading.
Add new feature styles to the relevant CSS file; register new files in this entry
point. The workspace playground keeps its existing local CSS import.

## Content safety and voice playgrounds

The **Content Safety** tab analyzes up to 10,000 characters using the configured
ContentSafety provider and thresholds. It shows severity scores for Hate, Sexual,
Violence, and Self-harm, plus the resulting policy decision. Analysis requires a
Global Admin and records usage with operation `Playground`; submitted text is not
stored in the usage record. `ContentSafety:Enabled` controls chat checks only;
the playground can analyze text while it is false, provided the HTTPS endpoint
and credentials are configured. Unavailable services are shown as errors, not
successful assessments.

The **Voice** tab uses the existing transcription service and quota/usage tracking.
Upload one WebM, Ogg, MP4, M4A, MP3, or WAV file up to 25 MB, preview it, and click
**Transcribe**, or use the microphone for up to five minutes and stop to transcribe.
Microphone recording requires browser permission and a secure context; switching
tabs or leaving the page releases the microphone. Transcripts can be copied and
are not sent as chat messages. Configure `AzureOpenAI:TranscriptionDeployment` to
enable voice. Both playgrounds support cancellation; a cancelled provider request
may already have incurred usage.

## Dialog components

All application popups use `src/components/Modal.tsx`, also re-exported from `components/ui`.
The component renders shared Header, Body, and Footer sections. Pass `title`, `open`,
and `onClose`; children fill the body, `footer` supplies action buttons, and
`headerActions` supplies preview controls. A Close footer is provided by default;
`footer={null}` explicitly hides it. `bodyClassName` supports specialized preview layouts.
The app styles every dialog; native dialog behavior supplies keyboard focus trapping,
Escape handling, and focus restoration. Browser `alert`, `confirm`, and `prompt` are
not used. Existing confirmations and file previews use the same modal component.

## Toast notifications

`ToastProvider` is mounted once around the application. Use `useToast()` from
`components/Toast` for `success`, `info`, `warning`, and `error` feedback. Toasts are
dismissible, deduplicated, and limited to five visible messages. Normal messages
expire after six seconds; errors remain until dismissed. Timers pause on hover,
keyboard focus, or a hidden browser tab. `{ duration: 0 }` keeps any message visible.
The viewport follows the active modal so notifications remain usable while a dialog
is open, and live regions announce messages without moving focus. Styling supports
light/dark themes, small screens, and reduced motion.

Use `useCopy()` from `lib/useCopy` for clipboard actions. Keep form errors and
persistent page state inline; transient operation feedback uses toasts.

## Pages

**Agents → Global skills** opens the skill library. Global Admins can upload a Zip
and delete uploaded global skills; Global Reader Admins can inspect them. Skills
deployed in the `skills` folder are default global skills and cannot be deleted or
overwritten through the application.

In an agent or sub-agent's **Edit → Skills → Manage skill files**, upload skills
available only to that specific agent. Save a new agent/sub-agent first. Parent
skills are not inherited by sub-agents. Global skills remain available for selection
in every scope. Uploading does not enable a skill for agents with explicit selections:
select it and save the agent. Uploads/deletions are immediate and are not undone by
cancelling the agent editor. Deletion also removes saved assignments.

Use the eye action to browse the extracted file paths and inspect `SKILL.md`, scripts,
and resources. Upload using the file picker or drag one Zip onto the upload area
(maximum 10 MB). Both methods use the same validation and are disabled while busy.
Read-only views do not show the upload area.

Use the Update skill icon on an uploaded skill to open a separate popup. Choose or
drop its replacement Zip, then select Replace skill to apply it. Cancel leaves the
skill unchanged; the new-skill upload area always creates a new skill.
The `name` in `SKILL.md` must exactly match the existing name; renaming is rejected
by the server. Replacement preserves the skill ID, creation date, and agent assignments,
and replaces all files atomically. New turns load the updated content; running turns
retain their cached version. Bundled defaults cannot be replaced through the UI.

Each skill card shows its retained version count beside the skill name,
which refreshes after replacement or version deletion. Version history lists numbered versions with their creation time and active
status. Browse or download any retained version. Administrators can delete inactive
versions of uploaded skills in their owning scope, with confirmation; the active version
and bundled defaults are protected. Each changed replacement retains the previous files;
identical uploads do not create another version. Existing skills become version 1 when
the `AddAgentSkillVersions` database migration runs. Previously replaced files that were
already deleted cannot be recovered. Agents always select the active version; running
turns retain their cached files even if that older version is deleted from storage.

Use **Make active** in version history to roll back or promote any retained version
of an uploaded skill. Confirm the switch in the popup. The selected version's description
and content become active for new turns, without changing the skill name, assignments,
or version count. Running turns keep their loaded files. Only administrators in the
owning scope can switch versions; bundled defaults remain read-only. Switching is
reversible while the other version is retained. Apply `AddAgentSkillLastVersion` so
future uploads keep increasing version numbers after rollback and history deletion.

The backend uses `ActiveVersion` and returns `isActive` in version history. Activation
uses `PUT /api/agents/skills/{id}/versions/{version}/active`. Apply
`RenameAgentSkillCurrentVersionToActiveVersion` to rename the existing database column
without changing the selected version, and deploy the API and frontend together.

The Download Zip action on each skill card exports all files with their
relative paths preserved, including read-only default skills. Deploy/restart the API
to enable the `/api/agents/skills/{id}/archive` endpoint. Use the inspector for individual files
and previews. Text files up to 512 KB are previewed as source with syntax highlighting
for Markdown, PowerShell, Python, shell scripts, JavaScript/TypeScript, JSON, YAML,
HTML/XML, CSS, C#, and SQL. Other text formats use plain text; binary files are
listed without execution. Markdown files offer a Preview/Source toggle; rendered
previews support tables and lists and skip embedded HTML. Copy copies the source text.
Download saves loaded text directly as UTF-8, without another API request. Binary
files and files too large to preview download their original bytes through the API;
restart/redeploy the API after adding the `/api/agents/skills/{id}/download` endpoint.
View dialogs expose inspection only.

A Zip must contain exactly one `SKILL.md`, either at its root or within a single
skill folder, plus any scripts/resources under that folder. The YAML frontmatter
requires `name` (a single-line lowercase name with digits/single hyphens, up to 64
characters) and `description`. Names must be unique across default and uploaded
skills. ZIPs are limited to 10 MB, 20 MB extracted, and 200 entries; unsafe paths,
symlinks, duplicate paths, and multiple skill manifests are rejected.

Apply the `AddAgentSkillDefinitions` database migration before deploying the API and
AgentHost. `AgentSkillDefinitions` stores skill metadata and `AgentSkillFiles` stores
relative paths, sizes, and provider-independent `StorageKey` values. File contents use
the existing `Uploads` storage account/container under the `agent-skills/` prefix.
ZIPs are unpacked and validated once at upload, rather than retained in the database.
Browsing reads database metadata; previewing fetches only the selected file from storage.

The API synchronizes its bundled `skills` folder at startup, after database migrations,
creating/updating protected default definitions and files. Unchanged defaults are skipped
using a content hash. Defaults absent from a later deployment remain in the catalog.
AgentHost reads this same database/storage catalog; deploy/start the API before AgentHost.
Each host caches downloaded skill folders by skill ID and content hash. Every turn
checks the database for the active selected versions and downloads only cache misses
or changed skills. Concurrent turns share the same versioned cache folders directly;
there are no per-turn file copies. Reference-counted leases keep versions available
until every turn using them finishes. Superseded versions are removed after their last
lease is released. The process-local cache targets 256 MB with least-recently-used
eviction of unused folders; active versions can temporarily exceed that limit.
Eviction or a host restart requires downloading those files again. Scripts are reviewed
and whitelisted and must treat shared skill files as read-only; the cache does not enforce
filesystem isolation. Temporary files and outputs belong in the agent workspace.
Cache entries refresh after `AgentSkills:Cache:MaxAgeDays` (default 7, range 1–3650),
even if their content hash has not changed. Access does not extend the creation date.
The host cache root contains `cache-directory.json` with `CreatedAtUtc`, `ProcessId`,
and `MaxAgeDays`. Each version/generation directory has a UTC timestamp in its name
and `cache-entry.json` with skill identity, content hash, `CreatedAtUtc`, and
`ExpiresAtUtc`. Metadata lives outside the skill folder exposed to the agent.
Expired generations stay pinned until active turns finish. These metadata files support
a future cleanup job; this change does not implement crash-orphan cleanup or reuse
directories from previous processes. A future cleaner must check for active use before
deleting directories, rather than relying on creation dates alone.
Uploaded scripts use the existing workspace/skill runner and its execution policy.

**Agents → New/Edit** provides General, Tools, and Skills tabs with selection counts
and Select all / Unselect all actions. Selections are saved together and take effect
on the next chat turn. Empty lists disable that capability category. Existing agents
retain all available tools/skills (including future additions) until their selection
is changed; explicit selections include only saved names. The View dialog displays
the assigned capabilities. Sub-agents retain their own independent selections and
remain available as delegation tools to their parent.

Apply the `AddAgentCapabilities` migration before deploying the updated API and AgentHost.
Nullable Tools/Skills columns preserve existing agents' behavior; empty JSON arrays mean none.

**Agents → Edit → Sub-agents** lets Global Admins create, edit, and delete specialists.
**Agents → View → Sub-agents** is read-only for everyone, with only a View action for each specialist.
Create the parent agent first before adding sub-agents. Sub-agent changes are saved
immediately; cancelling the parent editor does not undo them. Each specialist has
a name, invocation description, model deployment ID, instructions, and explicit tool/skill
selections. Empty selections give it no tools or skills. Selected skills include their
deployed scripts and resources; optional tools still require the corresponding service
and workspace mode at execution time.

The editor uses General, Tools, and Skills tabs that preserve edits when switching.
Tools and Skills each provide Select all and Unselect all actions; Save sub-agent saves all tabs together.
Creating, editing, and viewing sub-agents open a separate dialog with General, Tools,
and Skills tabs. Create/Save and Cancel actions appear in that dialog's footer; read-only
viewers see Close. Saving or cancelling returns to the parent agent's Sub-agents list.

The parent sees each specialist as a callable tool and supplies its task and context.
Sub-agents never appear in the chat agent selector, cannot be invoked through a public
execution endpoint, and cannot call other agents. They share the parent's conversation
workspace and tool authorization, including workspace instructions, but receive no chat
history automatically. Retrieved citations and model token usage are included in the
parent turn; individual requests retain the specialist model ID in Chat Usage and run
under the same distributed trace. Each turn permits eight delegations, with a two-minute
timeout per invocation. Changes apply on the next turn. The API and AgentHost use the
same skill catalog and file storage when using Foundry.

Apply the `AddSubAgentDefinitions` database migration before deploying the updated API
and AgentHost. It adds a separate table with a parent foreign key and unique names per
parent; existing top-level agents are unchanged.

The shared `http_get` tool fetches unauthenticated public HTTP/HTTPS URLs on standard
ports, including query parameters. Select it in an agent's Tools tab to enable it
(existing agents using all capabilities include it automatically). It returns status, content type, up to
64 KiB of UTF-8 text/JSON/XML, and a truncation flag, with a 30-second deadline.
Redirects are not followed, binary responses are not returned, and private/local/cloud
metadata destinations are blocked at connection time. No application credentials,
cookies, or custom headers are sent. Returned content is treated as untrusted data.



**Admin → Playgrounds** is a Global Admin-only tab with **Jev**, **Microsoft Decision**,
**Image Description**, **Image Text Extraction**, **MarkItDown**, **PageIndex**, **PDF**, **DOCX**, **XLSX**, and **PPTX** sub-tabs
(`/admin?tab=playgrounds&provider=jev` and `/admin?tab=playgrounds&provider=microsoft-decision`).
Switching between providers preserves each one's input, results, and pending evaluation.
Leaving Playgrounds or reloading the page resets this in-memory state. Previous playground URLs redirect to the new sub-tabs. Edit the
sample request JSON and run choice, score, or noul questions. Results show typed answers,
model, token counts, elapsed time, and trace ID. Credentials and deployment selection
come from the API host configuration. Both providers record usage attributed to the current admin in **Token usage → SystemOne Usage**. Cancelling stops waiting but cannot undo
an evaluation already processed by the provider.

The image tabs accept one image up to 10 MB by drag/drop or file selection. Click
**Describe** to use the API host's `OpenAi:ChatDeployment` vision model, or **Extract**
to run OCR using `DocumentIntelligence`. Description supports PNG, JPEG, WebP, and GIF;
OCR supports PNG, JPEG, BMP, and TIFF. Each tab preserves its file and result when
switching tabs. Results include copy, model, elapsed time, and trace ID; description
also shows reported token usage and records it under the admin user in Image Description
usage. Images are processed directly without creating attachment records. The routes
are separate multipart POST endpoints: `/api/admin/image-playground/describe` and
`/api/admin/image-playground/extract`, both accepting field `file`.

**MarkItDown** accepts a document by drag/drop or file selection (up to 25 MB). Click
Convert to view highlighted Markdown source or a rendered preview, copy it, or download
the `.md` result. Supported formats depend on the configured MarkItDown service.
**PageIndex** accepts PDF or UTF-8 Markdown up to 25 MB. Choose Include text (on by
default) and Include summaries (off by default), then click Index. The result shows
highlighted tree JSON with copy/download actions, model/token totals when reported,
duration, and trace ID. PageIndex usage is returned only, not persisted to token reports.
Both tabs retain their file/options/results when switching playground tabs. API routes
are `POST /api/admin/markitdown-playground/convert` and
`POST /api/admin/pageindex-playground/index`; they use server-side `MarkItDown` and
`PageIndex` configuration and require Global Admin. No SharePoint indexing or attachment
records are created by these playgrounds. Restart/deploy the API to enable these routes.

The **PDF**, **DOCX**, **XLSX**, and **PPTX** tabs run the corresponding `DocumentParsers`
library in the API host. Drag/drop or select one matching file up to 25 MB and click
Convert. Each tab preserves its file, options, and results when switching. Results offer
highlighted Markdown, preview, copy/download, parser warnings, elapsed time, and trace ID.
Skip images omits image details but retains placeholder comments. With Skip images off,
enable Enrich images (off by default) to run
image description with the API host's configured vision model and image OCR with
DocumentIntelligence before rendering Markdown. Description usage is recorded for the
admin user. Provider failures appear as parser warnings while other content is retained.
PDF uses PdfPig native text/layout; full-page OCR fallback remains disabled here.
The Global Admin-only routes are `POST /api/admin/{format}-playground/convert`, where
`format` is `pdf`, `docx`, `xlsx`, or `pptx`. Multipart fields are `file` and optional
`skip_images` (default `true`) and `enrich_images` (default `false`).
Skip images takes precedence: enrichment never runs when images are skipped.
Native parsing needs no external credentials; image
analysis requires the API host's OpenAi and DocumentIntelligence configuration.

**Chat** shows a microphone button in the composer when the API reports dictation as enabled and the browser supports recording. `DictationButton` records with `MediaRecorder` (WebM/Opus, or MP4 on Safari), shows a timer with stop and cancel, stops at the server's time limit, and appends the returned transcript to the draft for review; it never sends the message itself.

**Admin** groups **Delta state**, **Subscriptions**, and **Document signing** into URL-addressable tabs (`/admin?tab=delta`, `/admin?tab=subscriptions`, `/admin?tab=document-signing-configuration`). Global Reader Admins can access the first two; Document signing requires Global Admin. The old `/delta`, `/subscriptions`, and `/document-signing` links redirect to the corresponding tabs. Switching tabs unmounts the previous page, clearing any displayed Adobe tokens.

**Admin → Document signing** (`/admin?tab=document-signing-configuration`) is available only to Global Admins. It opens Adobe authorization in a popup and displays access/refresh tokens for manual configuration, with reveal, copy, and clear buttons. Token details include token type, API/web access points, expiry, the actual token exchange URL, client ID/secret, receipt timestamp, authorization URL, redirect URI, and requested scopes. **Complete Adobe response** preserves every provider field, including additional fields. Tokens and the client secret are masked until revealed; missing optional response fields are marked as not returned. Exchange details and the receipt timestamp are supplied by this application. These values are held only in page memory and are not saved or applied to configuration. Register the frontend's HTTPS `/adobe-sign-callback.html` in Adobe and configure `DocumentSigning:AdobeSign:OAuthRedirectUri` on the API. The callback must share this page's origin. Adobe sign-in and consent are still required. See [setup and release settings](../infra/README.md#document-signing-configuration-mapping).

The **Token usage** page also includes a **Content Safety** tab for requests, outcomes, daily totals, estimated text records, and per-request scores/attribution. Configure the backend's `ContentSafety` section to enable checks of user messages, assistant responses, and extracted attachment text. Enabled checks buffer assistant output until approved.

| Page | What it shows |
| --- | --- |
| **Browse** | Live SharePoint folders with breadcrumbs, filtering, and links to open items in SharePoint. Global Admin can create folders, upload files by picker or drag/drop, rename, delete, copy, and move files and folders. Global Reader Admin has read-only access. |
| **Browse → Recycle bin** | Preview, read-only view of the configured site's deleted items, across its libraries. Shows original location, deletion date, and size, with filtering and pagination. Available to Global Admin and Global Reader Admin. Open SharePoint from this view to restore items or manage its second-stage bin. |
| **Token usage** | Two report tabs: **Token Usage** for chat input/output/total tokens, turns, per-turn averages, daily trends, model/user breakdowns and a paginated turn log; **Embedding Usage** for indexing/search tokens, activities and attribution. Both support filters and detail popups. Available to Global Admin and Global Reader Admin. |
| **Overview** | Totals over the indexed-file table: files, chunks, source size, drives, files outside the current reconciliation round, and how many distinct index fingerprints are in play. Plus files by content type, the most recently indexed files, and the delta checkpoints. |
| **Indexed files** | The `SharePointIndexedFiles` table, filterable and sortable, with per-file embedding token usage and a Reindex action on each row. Select a row to see every recorded column — ETag, CTag, permissions hash, index fingerprint, scan ID, and the drive and item IDs. |
| **Attachment files** | Chat attachment files with conversion/index status, conversation links, chunk counts, embedding token usage, errors, download, reindex, and orphan deletion actions. |
| **Delta state** | The `SharePointDeltaState` table, one card per drive: the scan ID, sweep status, timestamp, and delta link. Reset clears a drive's cursor while retaining its row; Delete removes the row. Either action starts a full drive scan on the next sync. |
| **Subscriptions** | The Microsoft Graph webhook subscriptions on the application registration. Shows each tracked subscription's auto-renew setting and whether its resource, notification URL, and client state match this deployment. Create, edit, renew, enable or disable auto-renew, and delete; deleting takes two clicks. The **Default** record has a fixed URL and cannot be deleted. Any other subscription can be edited freely as long as its notification URL is not already taken. |
| **Chat** | Conversations with an agent that searches the index when a question needs it. Attach up to ten files, create or delete chats, and read Markdown answers with a collapsible list of sources. Conversations name themselves from the first question and are stored in SQL Server, so they survive a restart. |
| **Feedback** | Every answer someone rated in the chat: the question, the answer, its sources, and a link that opens that conversation. Filter by rating or by text; tiles show how many were liked, disliked, and the liked share. The link jumps straight to that answer in the thread and highlights it, which matters once a conversation is long. |
| **Search** | Full-text, vector, and hybrid over the same request body. **Compare all three** issues them together and reports each one's round trip, plus how much the three agree — distinct chunks returned, how many every strategy found, and how many only one strategy found. |

Attachment files distinguish **View indexed text** (reads the saved text used by the last successful index; available after indexing) from **Convert to Markdown** (freshly converts the original document without changing its saved indexed text, chunks, or status). Conversion is available for non-text, non-image documents to users with write access. Images have their own description and OCR actions. The Indexed files page labels its existing on-demand conversion **Convert to Markdown**. Popup titles identify the operation and provide plain text and rendered Markdown tabs.

Image attachments are indexed on upload and reindex using a vision description and, where supported and configured, Document Intelligence OCR. Their derived text follows the normal safety, chunking, embedding, and search pipeline. **View indexed text** shows the saved indexed description/OCR content while **Download** preserves the original image. Reindex older images that show zero chunks to make them searchable. Image-description token usage and embedding token usage are recorded separately.

PDF attachments are allowed by default and use Document Intelligence text extraction/OCR for indexing. **Preview** opens the original PDF in a large popup with **In-app** (default) and **Browser** viewer options. In-app rendering avoids automatic downloads caused by native browser PDF settings; explicitly choosing Browser may still download the file depending on those settings. The in-app viewer lazy-loads React-PDF/PDF.js and supports a collapsible left thumbnail panel, previous/next page navigation, zoom, and text selection while rendering one full-size page at a time. Clicking a thumbnail jumps to that page. The thumbnail list is virtualized: only visible thumbnails and a small buffer render at low resolution, without text or annotation layers, to limit rendering and memory costs for large PDFs. Switching viewers reuses the downloaded file. PDF.js workers, fonts, character maps, and WASM assets are served locally. Download remains available in either mode. **View indexed text** shows their saved extracted text. **Extract text** runs Document Intelligence on demand and displays the result in the same copyable popup used for image OCR, without changing the index. **Convert to Markdown** always uses MarkItDownClient, including for PDFs, without changing the index. **Download** returns the original PDF. SharePoint PDF indexing also uses Document Intelligence. Configure the endpoint and credentials on the API and Background services, and include `.pdf` in any custom upload/indexing allowlists.

Attachment files also offer **Describe image** for PNG, JPEG, WebP, and GIF, and **Extract text** for PNG, JPEG, BMP, and TIFF, intersected with the configured image extensions. Results open in a popup with a copy action, loading state, and errors. Description uses the API's configured Azure OpenAI chat deployment and records image-description token usage against the requesting user and attachment; text extraction uses Document Intelligence and requires its endpoint. These actions use the existing attachment ownership and write-role checks, and do not reindex the file or send a chat message.

Browse (`/browse?folder=…`) reads the configured document library directly through `/api/browse`; changes reach the search index through the existing background synchronization. Uploads accept multiple files, up to 100 MB each, and reject duplicate names without replacing existing content. Reverse proxies must also allow the desired request size. Uploads are staged on disk and larger files use Graph upload sessions. Directory uploads are not supported; use New folder instead. Rename, delete, and move detect stale items using ETags. Delete asks for confirmation and includes a folder's descendants. Copy and move use a folder picker and allow a new destination name. Copy is asynchronous: an accepted message means SharePoint has started the request, not that it has completed; refresh the destination to check the result. Browse mutations require the application's SharePoint write grant described in the root README.

Searches are kept in the URL (`/search?q=…&mode=compare&top=10`), so a result is a link and the
back button steps through searches. So is the open conversation
(`/chat?conversation=…&message=…`), which is how the Feedback page links to a particular answer.

## Running it

The **Attachment files** page supports multi-file uploads through **Upload files** or its highlighted drag-and-drop area. These uploads are stored as owned orphan attachments with **Not started** status and are **not indexed**: no extraction, OCR, vision description, embeddings, or search indexing runs. Existing file-type, size, and user storage limits still apply. Per-file errors do not discard successful uploads. Use **Reindex** explicitly when indexing is wanted. Chat uploads keep their existing automatic indexing behavior.

### Shared-account signing

PDF rows and PDF preview headers include **Signatures**. Choose a configured provider, enter recipients in signing order, and create a draft. Open the preparation link in a new tab to place fields and send. Return and use **Refresh status**; completed requests offer signed PDF and audit-record downloads. Creating the draft uploads the PDF without emailing recipients.

**In-App Signature** opens a full-screen editor instead (`InAppSigningEditor`, lazy-loaded with pdf-lib). In **Place fields**, drag fields from the palette onto pages or click one to add it to the visible page; drag to move, use the corner handle to resize, arrow keys to nudge, and Delete to remove. **Save fields** stores the layout. **Templates** (`SigningTemplatesDialog`) saves the current layout as a new or existing personal template, or loads one by replacing or adding to the current fields; values are never stored in templates. In **Sign**, signature and initials fields open `SignaturePad`; date fields default to today. **Preview** builds the same flattened PDF from the current fields, including unsaved changes and leaving out empty fields, and opens it without saving or uploading anything. **Finish** flattens the values into the PDF in the browser, uploads it, and opens the signed preview. Unfinished drafts appear in **Signing requests** with **Continue signing** and **Discard draft**.

DocuSign and Adobe Acrobat Sign use administrator-configured company senders. See [backend setup and limitations](../README.md#shared-organization-signing). Status synchronization is manual and completed copies are downloaded from the provider. Read-only users cannot create or prepare requests. Signing requests preserve their source attachment by preventing its deletion.

### Workspace file management

Open a conversation's **Workspace** tab, placed before **Chat**. It shows the workspace name, instructions, sandbox session information, and an inline **Workspace files** browser. Switching tabs preserves the chat draft. Use **New folder**, **Upload files**, or drag files onto the browser. Each file/folder has a **…** menu with Rename, Copy, Move, and Delete. Copy and Move include a destination folder browser with breadcrumbs, an Up button, and an optional folder address. Browse to the destination, then keep or edit the item name. The destination folder must already exist. Existing items are never overwritten. Delete requires confirmation and is permanent, including all contents of a directory.

Global Admin and conversation owners with the User role can make changes; Global Reader Admin is read-only. Operations run without calling the model in both Local and Foundry modes. Foundry requires an existing session (send a question first). When the API runs the agent, the browser works on the workspace's chosen mode (**Files live in** in **Workspace details**): uploading a file or creating a folder starts a dynamic session or sandbox that does not exist yet, and **This server (shared)** is one directory shared by every conversation using it. Changes are shared by the conversations in the same workspace. Uploads are limited to 5 MB per file, copies to 100 MB, and directory operations to 5,000 entries. Folder drag/drop uploads are not supported. The root, paths outside it, and symbolic links are rejected. Workspace file edits do not update the source SharePoint library or attachment storage.

The management endpoint is `POST /api/chat/conversations/{id}/files/manage` with `operation` (`mkdir`, `upload`, `rename`, `copy`, `move`, `delete`), relative `path`, optional complete `destination`, and base64 `content` for upload. API request bodies are limited to 8 MB. Deploy the API and AgentHost together when using Foundry.

### Frontend startup

The Recycle bin view (`/browse?view=recycle-bin`, API `GET /api/browse/recycle-bin`) uses the Microsoft Graph **beta** [site recycle-bin listing API](https://learn.microsoft.com/en-us/graph/api/recyclebin-list-items?view=graph-rest-beta). Microsoft does not support beta APIs for production use; availability and contracts can change. This endpoint documents `Files.Read.All` or `Sites.Read.All` application permission (or their ReadWrite equivalents) with admin consent; `Sites.Selected` alone is not documented as supported. Existing credentials are reused, and the application does not change tenant permissions. Access failures are shown explicitly. The Graph v1.0 recycle-bin APIs for SharePoint Embedded containers are not interchangeable with this site's API. This view lists metadata only; in-app restore, permanent deletion, content preview, and separate first/second-stage selection are not implemented.

Configure [Entra ID sign-in](../README.md#entra-id-sign-in) on the existing SharePoint app registration first. The frontend reads the tenant/client IDs from `/api/auth/config`; no client secret belongs in frontend configuration. Register `http://localhost:5173/auth-redirect.html` as a **Single-page application** redirect URI and expose the delegated `api://<ClientId>/access_as_user` scope with v2 access tokens.

The backend also requires **Microsoft Graph → Application permissions → `User.Read.All`** with **admin consent** to verify the user's directory email and link their application account. After granting consent, restart the API before retrying sign-in. If the UI shows **Cannot verify the user with Microsoft Graph**, follow the [service permission setup and troubleshooting](../README.md#application-service-permissions). Application roles such as Global Admin do not grant Graph permissions.

The app shows **Sign in with your organization** before loading its pages. The account and **Sign out** appear in the header. API calls, including streaming chat and attachment downloads, carry access tokens acquired by MSAL. Deploy the generated `auth-redirect.html` alongside `index.html`; do not rewrite that file to the React entry point.

DOCX, XLSX, and PPTX files can be opened in the app from Search results, the Indexed files detail panel,
the Attachment files page, or a chat message attachment. The preview downloads the file bytes to the browser
and offers a save button. Indexed-file previews fetch the current SharePoint version and are limited by
`LocalWorkingDirectory:Downloads:MaxFileBytes` (20 MB by default). XLSX sheets show 50 rows and 26 columns at a time, with
tabs to select worksheets and controls to move through larger sheets. The grid displays common cell
formatting, number formats, column widths, row heights, and merged cells.

The API must be running first. From `backend/SharePointAgent.Api`:

```bash
dotnet run
```

Then here:

```bash
npm install
npm run dev
```

Open <http://localhost:5173>. The dev server proxies `/api` to `http://localhost:5263`, so the browser
makes same-origin requests and never needs the API's CORS policy.

### Configuration

Copy `.env.example` to `.env` to change either of:

- `VITE_API_PROXY_TARGET` — where `npm run dev` proxies `/api`. Set it when the API is not on 5263;
  the `https` launch profile, for example, is `https://localhost:7104`.
- `VITE_API_BASE_URL` — only for a production build served from a different origin than the API. When
  set, add that origin to `Cors:AllowedOrigins` in the API's configuration.

### Other scripts

Deploy with [release-frontend.yml](../.github/workflows/release-frontend.yml), which builds the frontend with the API URL from the saved infrastructure outputs and uploads `dist/` to Azure Static Web Apps. Configure the environment's deployment token, frontend origin and Entra SPA redirect URI as described in the [release setup](../infra/README.md#github-actions-deployment). The build includes SPA navigation fallback configuration and `auth-redirect.html`.

```bash
npm run build      # typecheck, then build to dist/
npm run preview    # serve the built output
npm run typecheck
```

## What it needs from the API

Index state endpoints added alongside the existing search ones:

- `GET /api/state/summary`
- `GET /api/state/indexed-files?search=&driveId=&sort=&desc=&skip=&top=`
- `GET /api/state/indexed-files/{driveId}/{itemId}`
- `POST /api/state/indexed-files/{driveId}/{itemId}/reindex`
- `GET /api/state/indexed-files/{driveId}/{itemId}/content` (current Office bytes from SharePoint)
- `GET /api/state/delta`

These GET endpoints read the database at `SqlServer:ConnectionString` and never write to it. The content endpoint also
reads the configured SharePoint library through Microsoft Graph. A table that does not
exist yet reads as empty, so the viewer works before the worker's first pass.

The Delta state page also uses `POST /api/state/delta/{driveId}/reset` and
`DELETE /api/state/delta/{driveId}`. Both write to the checkpoint table after confirmation.

The Chat page uses, and these **write to the database and call Azure OpenAI**:

- `GET|POST /api/chat/conversations`, `DELETE /api/chat/conversations/{id}`
- `GET|POST /api/chat/conversations/{id}/messages`
- `POST /api/chat/messages/{id}/feedback` and `GET /api/chat/feedback`
- `POST|GET /api/attachment-files`, `GET /api/attachment-files/{id}/download`, `POST /api/attachment-files/{id}/reindex`, and orphan-only `DELETE /api/attachment-files/{id}`

The Subscriptions page additionally uses, and these **change tenant state**:

- `GET /api/subscriptions`
- `POST /api/subscriptions` — body may include `name`, `days`, `notificationUrl`, and `clientState`
- `PUT /api/subscriptions/{id}` — a changed name, URL, or client state replaces the subscription
- `POST /api/subscriptions/{id}/renew` — optional body `{ "days": 28 }`
- `DELETE /api/subscriptions/{id}` — refused for the default subscription

**These endpoints require Entra sign-in and provide shared operator access.** Assign only trusted operators to the enterprise application: indexed files, conversations, uploads, and subscription administration are not isolated per signed-in user. Health checks, public sign-in configuration, and the validated Graph webhook are the only anonymous API endpoints.

## Foundry session workspace playground

For HTTP 403, check the **API caller's** Azure identity, not the hosted agent's identity. The API release grants Foundry User (`53ca6127-db72-4b80-b1b0-d745d6d5456d`, formerly Azure AI User) to the deployed API managed identity. Local API runs use the identity selected by `DefaultAzureCredential` (for example, Azure CLI or Visual Studio), which needs its own project-scoped role assignment. In the project's **Access control (IAM)**, check that identity's access and assign Foundry User when cross-user session administration is intended. Restart the local API after correcting credentials and allow role propagation before retrying. Application GlobalAdmin access is separate from Azure RBAC; deploying AgentHost does not grant the developer account session access. If the role is already effective, inspect the Foundry response/network restrictions and confirm the configured endpoint belongs to that project.

Under **Playgrounds → Workspace → Foundry Sessions**, global administrators can list, create, stop, and delete sessions for the agent configured by `ChatAgent:Foundry:Endpoint`. Refresh after creating a session until it becomes active, then select it to browse its working directory. The shared file actions support folders, upload/download, text editing, rename/move/copy/delete, Zip/unzip, and script execution with arguments and a selected working directory. Execution is an individual command/script, with copyable stdout and stderr.

Deploy the updated **API and AgentHost** first. Existing sessions remain bound to their original agent version; create a new session to use the new workspace handler. File operations use the hosted agent's working directory, not all of `$HOME`, and never invoke an LLM. Scripts run with AgentHost's identity and access, so access is limited to global administrators. Stopping retains the session's files; deleting removes them and can affect chats using that session. Only active sessions expose workspace actions in this playground.

Authentication uses `DefaultAzureCredential`. Session visibility depends on the API identity's Foundry permissions; see Microsoft's [hosted session management documentation](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-sessions). Configure a deployed HTTPS invocation endpoint; the localhost AgentHost endpoint does not provide Foundry lifecycle APIs. No additional environment setting or database migration is required.

## Graph search

Graph extraction and query planning record each LLM request in **Chat usage**, including indexing, search, evaluation, playgrounds, and extraction retries. The Operation column distinguishes graph requests from chat turns. Interactive requests are attributed to the signed-in user; background requests without a user appear as System. Graph requests contribute tokens and request counts but do not create conversation turns. Embeddings remain in their own usage report. Apply the `TrackGraphChatUsage` database migration before running this version.

Both graph playground results show the model/deployment, input/output/total tokens, and request count across chunks and retries. Missing provider usage is shown as **Not reported**. Usage recording is best-effort and logs persistence failures.

**Playgrounds → Knowledge Graph → Entity and Relationship Extraction** tests the indexing extractor on pasted document text (up to 20,000 characters). It uses `KnowledgeGraph:Extraction:Deployment`, processor chunk size/overlap, neighboring context, and ontology grounding. Each output chunk includes raw model output, accepted entities and relationships, and rejection counts. It does not resolve canonical IDs or save graph data. The separate **Query Plan** mode tests interpreting search questions.

Under **Playgrounds → Query Plan**, administrators can extract entity names and relationship types from a question without running document search or graph traversal. The playground uses `AzureOpenAI:ChatDeployment` and the configured ontology, preserves input when switching tabs, and supports cancellation, reset, and copying the plan JSON. Each extraction makes one AI request through `POST /api/admin/graph-query-playground`.

Each Graph page search first calls the configured `AzureOpenAI:ChatDeployment` with structured output to identify entity names and relationship types. This adds one model request per search; it does not extract or modify stored graph data. Names are resolved through the alias index, and relationship types must exist in the configured ontology. Empty relationship types explore all configured types within the normal traversal limits. Invalid or incomplete model output returns a retryable error before document search begins.

The agent's `find_relationships` tool accepts `question`, `entities`, optional `relationshipTypes`, and `top`. Its description lists the configured relationships, so the agent supplies the query plan directly without a second model call. Both entry points bypass keyword routing when supplying a plan and retain evidence authorization checks. All retrieval callers must supply a query plan; admin evaluation uses the same AI planner.

The **Graph** navigation item opens `/graph`. Ask a relationship question (ownership, dependencies, impact, or connections), choose the search breadth, and inspect matching evidence. Filter by relationship, select a result to read/copy its source chunk, or open the source document. Hop counts and disputed flags describe how graph retrieval found that evidence.

`POST /api/graph/search` accepts `{ query, top }` with a query up to 2,000 characters and 1–20 seed chunks. It always uses the signed-in Entra object ID, including for administrators. Hybrid search supplies permission-trimmed seeds, then the existing graph retrieval verifies current evidence and authorized paths. Raw canonical entity names and unverified edges are not exposed. Results are bounded and are not an exhaustive graph listing.

Configure knowledge graph storage and populate the graph through the indexing pipeline. Assign `find_relationships` to an agent to allow graph retrieval, or unassign it to disable graph use by that agent. The Graph page is independently available for allowed tenants. The page distinguishes unsupported relationship questions, missing seeds, missing authorized evidence, and retrieval failures.

## Voice playgrounds

Under **Playgrounds → Voice**, use **Speech to text** for the existing Azure OpenAI transcription playground or **Text to speech** for Azure AI Speech synthesis. Switching tabs preserves inputs and stops microphone recording/audio playback.

Text to speech requires a configured `Speech` section on the API and GlobalAdmin access. Choose a voice supplied by the configured resource, enter up to 3,000 characters of plain text, and generate MP3 or WAV audio to preview/download. The application does not save the input or generated audio. Azure charges apply; synthesis is limited to 10 minutes by the provider. Cancel stops waiting but may not stop provider processing.
