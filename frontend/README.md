# SharePoint Agent

A React front end for `SharePointAgent.Api`. It shows the worker's SQL Server state — the
`SharePointIndexedFiles` and `SharePointDeltaState` tables — and runs the three retrieval strategies
against the search index, side by side.

## Pages

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

### Sandbox file management

Open a conversation's **Workspace** tab, placed before **Chat**. It shows the workspace name, instructions, sandbox session information, and an inline **Files in the sandbox** browser. Switching tabs preserves the chat draft. Use **New folder**, **Upload files**, or drag files onto the browser. Each file/folder has a **…** menu with Rename, Copy, Move, and Delete. Copy and Move include a destination folder browser with breadcrumbs, an Up button, and an optional folder address. Browse to the destination, then keep or edit the item name. The destination folder must already exist. Existing items are never overwritten. Delete requires confirmation and is permanent, including all contents of a directory.

Global Admin and conversation owners with the User role can make changes; Global Reader Admin is read-only. Operations run without calling the model in both Local and Foundry modes. Foundry requires an existing session (send a question first). Changes are shared by conversations using the same sandbox; Local mode shares one process directory. Uploads are limited to 5 MB per file, copies to 100 MB, and directory operations to 5,000 entries. Folder drag/drop uploads are not supported. The root, paths outside it, and symbolic links are rejected. Sandbox edits do not update the source SharePoint library or attachment storage.

The management endpoint is `POST /api/chat/conversations/{id}/files/manage` with `operation` (`mkdir`, `upload`, `rename`, `copy`, `move`, `delete`), relative `path`, optional complete `destination`, and base64 `content` for upload. API request bodies are limited to 8 MB. Deploy the API and AgentHost together when using Foundry.

### Frontend startup

### Sandbox file management

Open a conversation's **Workspace** tab, placed before **Chat**. It shows the workspace name, instructions, sandbox session information, and an inline **Files in the sandbox** browser. Switching tabs preserves the chat draft. Use **New folder**, **Upload files**, or drag files onto the browser. Each file/folder has a **…** menu with Rename, Copy, Move, and Delete. Copy and Move include a destination folder browser with breadcrumbs, an Up button, and an optional folder address. Browse to the destination, then keep or edit the item name. The destination folder must already exist. Existing items are never overwritten. Delete requires confirmation and is permanent, including all contents of a directory.

Global Admin and conversation owners with the User role can make changes; Global Reader Admin is read-only. Operations run without calling the model in both Local and Foundry modes. Foundry requires an existing session (send a question first). Changes are shared by conversations using the same sandbox; Local mode shares one process directory. Uploads are limited to 5 MB per file, copies to 100 MB, and directory operations to 5,000 entries. Folder drag/drop uploads are not supported. The root, paths outside it, and symbolic links are rejected. Sandbox edits do not update the source SharePoint library or attachment storage.

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
