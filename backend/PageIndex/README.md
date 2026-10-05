# PageIndex API

Standalone FastAPI indexing service, following the MarkItDown service pattern.
Uses the pinned `pageindex==0.2.10` package from [VectifyAI/PageIndex](https://github.com/VectifyAI/PageIndex).
Accepts Markdown from DocumentParsers/MarkItDown or text-based PDFs and returns
a JSON document tree. This service performs indexing only: it does not persist
indexes, expose chat/retrieval, or enforce SharePoint document permissions. The
caller stores results with document versions and permissions for later retrieval.

## Run locally

From `backend/PageIndex` (Python 3.12 recommended):

```powershell
python -m venv .venv
.venv/Scripts/python.exe -m pip install -r requirements-dev.txt
$env:PAGEINDEX_SERVICE_API_KEY = "your-service-key"
.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8001
```

On Linux use `.venv/bin/python`. OpenAPI is at `/openapi.json`, Swagger at `/docs`.
When authentication is enabled, these routes also require `X-Api-Key`.

```powershell
curl.exe http://localhost:8001/health
curl.exe -X POST http://localhost:8001/index -H "X-Api-Key: your-service-key" -F "file=@C:/Documents/report.md" -F "include_text=true" -F "include_summaries=false"
```

## Endpoints

| Route | Behavior |
| --- | --- |
| `GET /health` | Public liveness probe; does not call an LLM |
| `POST /index` | Multipart upload: `file`, `include_text` (default true), `include_summaries` (default false) |

Supported extensions: `.md`, `.markdown`, `.pdf`. Markdown must be UTF-8 (BOM accepted).
Responses contain upstream `structure` nodes and `doc_name` based on the filename.
Markdown nodes use `line_num`; PDF nodes use one-based `start_index`/`end_index`.
PDF `text`, when requested, contains the complete inclusive page range for each
node, so parent/child text may overlap. Page text uses PDFium extraction order.

Markdown preambles and headingless files receive a synthetic `# Document` heading
in a temporary copy. `source_line_offset` is 2 for these files, otherwise 0.
Subtract it from node line numbers to locate original text; synthetic nodes have
no source heading. Page/slide HTML comments remain text, not automatic citation metadata.

PDFs use local Flash indexing with tree optimization disabled. Scanned PDFs need
OCR first: send Markdown produced by your parser/OCR pipeline. No OCR is run here.
When Flash finds no sections in a text PDF, the response contains one document
node and a warning; that fallback does not generate a summary.

### Token usage

Successful `/index` responses include aggregate model token usage:

```json
"usage": {
  "model_id": "azure/pageindex-summary",
  "input_tokens": 1200,
  "output_tokens": 240,
  "prompt_tokens": 1200,
  "completion_tokens": 240,
  "total_tokens": 1440,
  "model_calls": 3,
  "calls_without_usage": 0
}
```

`input_tokens` and `output_tokens` are request-wide totals, matching the input/output
terminology used by the other usage tracking. `prompt_tokens` and `completion_tokens`
remain equivalent aliases for compatibility; do not add the aliases to the totals.
In C#, read `result.Usage.ModelId`, `InputTokens`, `OutputTokens`, and `TotalTokens`.
`model_id` is the configured `PAGEINDEX_INDEX_MODEL`, including the provider prefix.
For Azure it identifies the deployment, not the underlying model version. It is
returned even when no model calls occur.
This is response-only reporting; no database usage record is written.

Counts are summed from completed LiteLLM responses across the document's model
calls, for both Markdown and PDF. See the [LiteLLM response format](https://docs.litellm.ai/docs/).
Requests with no model calls return zero for every counter, including indexing with
summaries disabled. `model_calls` counts completed responses; `calls_without_usage`
counts responses missing any token totals, so nonzero means the totals are partial.
Failed attempts without a response are not included, and failed `/index` requests
retain their existing error response. These counters are response usage, not a
billing reconciliation or the token count of the uploaded document.
The C# client exposes them through `PageIndexResult.Usage` (null for older servers).

## Configuration

### Use a `.env` file

From `backend/PageIndex`, create your local configuration:

```powershell
Copy-Item .env.example .env
```

Edit `.env` with your generated service key and Azure OpenAI settings. The API
automatically loads this file at startup from the PageIndex folder, regardless
of the working directory. Existing environment variables take precedence,
including an explicitly empty value. Worker processes inherit the loaded settings.
The file is optional; environment-only configuration still works.

For local use, start Uvicorn after saving `.env`. Omit the example
`$env:PAGEINDEX_SERVICE_API_KEY = "your-service-key"` assignment above when using
the key from `.env`, since that assignment would override it. Restart the service
after changing configuration. `.env` is excluded from Git and Docker builds;
commit only the placeholder `.env.example` template.

Use plain `NAME=value` entries as shown in `.env.example` so the file also works
with Docker's `--env-file`. See [Container](#container) for Docker commands.

### Environment variables

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `PAGEINDEX_SERVICE_API_KEY` | empty | Require `X-Api-Key` except on `/health`; empty permits local unauthenticated use |
| `PAGEINDEX_MAX_FILE_BYTES` | 26214400 | Maximum uploaded file bytes |
| `PAGEINDEX_TIMEOUT_SECONDS` | 300 | Worker execution deadline |
| `PAGEINDEX_MAX_CONCURRENCY` | 2 | Concurrent indexing processes per API process |
| `PAGEINDEX_INDEX_MODEL` | `gpt-4.1-mini` | Server-selected summary model; for Azure use `azure/<deployment-name>` |
| `AZURE_API_BASE` | unset | Azure OpenAI resource endpoint, such as `https://YOUR-RESOURCE.openai.azure.com/` |
| `AZURE_API_KEY` | unset | Azure OpenAI resource key for optional summaries |
| `AZURE_API_VERSION` | unset | API version supported by your Azure OpenAI deployment |
| `OPENAI_API_KEY` | unset | OpenAI credentials for optional summaries using an OpenAI model |

PageIndex uses LiteLLM for model calls and supports
[Azure OpenAI configuration](https://docs.litellm.ai/docs/providers/azure).
Set `PAGEINDEX_INDEX_MODEL=azure/<deployment-name>` to select Azure; the bare default
`gpt-4.1-mini` selects OpenAI. Azure does not require `OPENAI_API_KEY`.

Use the Azure **deployment name** after `azure/`, which may differ from the model
name. For example, a deployment named `pageindex-summary` uses
`PAGEINDEX_INDEX_MODEL=azure/pageindex-summary`. Set `AZURE_API_BASE` to the resource
root endpoint, without `/openai/deployments/...` or `/chat/completions`.
Choose an API version supported by that deployment.

With summaries disabled, Markdown and PDF tree generation make
no LLM calls. Enabling summaries can send document text to the configured model.
The service key above is distinct from model-provider and PageIndex Cloud keys.
This wrapper does not call PageIndex Cloud. Keep secrets in environment variables.

Each request runs in a separate process with a temporary working directory.
Timeouts terminate the worker; files are removed after success or failure.
Authentication runs before upload parsing. Multipart uploads may spool to disk
before the endpoint enforces the file limit; configure a request-body limit at
the reverse proxy too. The timeout covers worker execution, excluding upload and
waiting for a concurrency slot. Set client/proxy timeouts accordingly.

Errors: 401 authentication, 413 size limit, 415 extension, 422 validation,
502 upstream indexing failure, 504 indexing timeout. Upstream stdout/stderr is
discarded to avoid returning/logging source content or credentials.

## Generate the service API key

Create `PAGEINDEX_SERVICE_API_KEY` yourself as a random shared secret for this
service. No Azure or PageIndex account is needed to generate it.

Use the same PowerShell/.NET approach as
[Generate the MarkItDown API key](../../infra/README.md#generate-the-markitdown-api-key).
This requires no Python installation:

```powershell
$keyBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $rng.GetBytes($keyBytes)
    $env:PAGEINDEX_SERVICE_API_KEY = [Convert]::ToBase64String($keyBytes)
    $env:PAGEINDEX_SERVICE_API_KEY
}
finally {
    $rng.Dispose()
}
```

This generates a cryptographically random 32-byte key encoded as Base64, sets it
in the current shell, and prints it. Store the output in your secret manager so you can
reuse it for the service and its callers. Generate it once per environment, not
for every request.

Save the generated value as `PAGEINDEX_SERVICE_API_KEY` in your `.env` file for
the container example below. For local execution, you can also start Uvicorn
directly from this shell using the generated environment variable.
Call the service with the same value in the `X-Api-Key` header:

```powershell
curl.exe -X POST http://localhost:8001/index `
  -H "X-Api-Key: $env:PAGEINDEX_SERVICE_API_KEY" `
  -F "file=@C:/Documents/report.md" `
  -F "include_summaries=false"
```

If you use another terminal, load the saved key into its
`PAGEINDEX_SERVICE_API_KEY` environment variable first; shell environment changes
are not shared with other terminals. To rotate the key, generate a replacement,
restart the service or recreate the container with it, and update every caller.

## Container

For Azure deployment, see [PageIndex Container App](../../infra/README.md#pageindex-api).

Prepare `backend/PageIndex/.env` using [Configuration](#configuration), then run
from the repository root:

```powershell
docker build -t sharepoint-pageindex backend/PageIndex
docker run -p 8001:8000 --env-file backend/PageIndex/.env sharepoint-pageindex
```

Docker reads `.env` on the host and supplies the variables to the container.
An explicit `-e NAME=value` overrides the same key from `--env-file`; `-e NAME`
passes a value from your current shell. For environment-only configuration,
replace `--env-file` with an `-e NAME` argument for each configured variable.
Recreate the container after changing configuration; no image rebuild is required.

From another terminal, request summaries explicitly:

```powershell
curl.exe -X POST http://localhost:8001/index `
  -H "X-Api-Key: your-service-key" `
  -F "file=@C:/Documents/report.md" `
  -F "include_text=true" `
  -F "include_summaries=true"
```

Replace the example service key with the same value supplied to the container.
Summaries are disabled by default. With `include_summaries=false`, indexing needs
no model-provider key; you can omit the Azure variables and `PAGEINDEX_INDEX_MODEL`.
Keep actual keys out of the Dockerfile and committed files.

No infrastructure deployment or existing agent wiring is changed by this service.

## C# client

`SharePointAgent.Infrastructure.PageIndexClient` provides `IndexAsync` and
`CheckHealthAsync`, following the MarkItDown client pattern. Register it explicitly
in the host that needs indexing:

```csharp
builder.Services.AddPageIndexClient(builder.Configuration);
```

Configure the host's `appsettings.json`:

```json
{
  "PageIndex": {
    "Endpoint": "http://localhost:8001",
    "IndexPath": "/index",
    "HealthPath": "/health",
    "TimeoutSeconds": 360
  }
}
```

Set `PageIndex:ApiKey` through user secrets or `PageIndex__ApiKey` in the host
environment to the same value as the server's `PAGEINDEX_SERVICE_API_KEY`.
The C# host does not load the Python service's `.env`. Azure credentials stay on
the PageIndex server. The client timeout should allow for the server's worker
deadline plus upload and queue time.

Inject `PageIndexClient` and call it with Markdown or PDF bytes:

```csharp
var tree = await pageIndex.IndexAsync(
    "report.md", markdownBytes, "text/markdown", cancellationToken,
    includeText: true, includeSummaries: false);
```

The result contains `DocumentName`, recursive `Structure` nodes, optional
`SourceLineOffset`, and `Warnings`. Nodes expose text, summaries, Markdown line
numbers, and PDF page ranges when present. Non-success indexing responses throw
`PageIndexIndexingException` with the HTTP status and response detail. Registration
does not automatically change the SharePoint indexing pipeline.

## Tests

```powershell
.venv/Scripts/python.exe -m unittest discover -s tests -v
```

Tests cover authentication, limits, invalid inputs, option forwarding, cleanup,
worker timeout, and real local Markdown/PDF indexing without model calls.
