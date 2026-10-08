# Sandbox host

An HTTP server that runs **inside** an isolated environment and gives agent tools a file system and a script runner: PowerShell 7, Python 3.12, Node.js 22, and Bash. One image serves two Azure Container Apps targets:

| | [Dynamic Sessions](https://learn.microsoft.com/azure/container-apps/sessions-custom-container) | [Sandboxes](https://techcommunity.microsoft.com/blog/appsonazureblog/azure-container-apps-sandboxes-now-generally-available/4559125) |
| --- | --- | --- |
| Isolation | Hyper-V container per session | MicroVM per sandbox, with suspend and resume |
| Reached through | The pool endpoint, which forwards the path and checks an Entra token | A sandbox port opened with `add_port`, behind an IP allow list |
| Lifetime | Ends after the pool's cooldown period | Until deleted; suspend it when idle |
| Settings the deployment sets | `Sandbox__RequireApiKey=false`, `Sandbox__MaxTimeoutSeconds=220` | `Sandbox__ApiKey=<random key per sandbox>` |

A tool written against this API works against either target; only the base URL and the authentication header change.

The host has no reference to the rest of the backend and carries no application settings. Nothing that can reach SQL, SharePoint, or Azure OpenAI is in the image, so the code it runs has nothing to steal.

## Run locally

```powershell
dotnet run --project backend/SharePointAgent.SandboxHost
curl.exe http://localhost:57960/health
curl.exe -X POST http://localhost:57960/executions -H "Content-Type: application/json" -d '{\"language\":\"python\",\"code\":\"print(1 + 1)\"}'
```

The launch profile turns off the API key requirement. Locally the workspace is `%TEMP%/sharepointagent-sandbox`, and scripts run on the machine itself, not in a sandbox. Use it only for developing tools. On Windows, `powershell` runs Windows PowerShell unless `Sandbox:Executables:powershell` names `pwsh`.

To get the real runtimes, build and run the image from the repository root:

```powershell
docker build -f backend/SharePointAgent.SandboxHost/Dockerfile -t sandboxhost .
docker run --rm -p 8080:8080 -e Sandbox__ApiKey=<random-key> sandboxhost
```

## API

Every route except `/health` requires `X-Api-Key` when `Sandbox:ApiKey` is set. Errors are `{ "error": "..." }`: 400 for a bad request or path, 404 for a missing path, 409 when the target already exists or a directory is not empty, 401 for a missing key. Paths are relative to the workspace (`/workspace` in the image). An absolute path is accepted only when it is inside the workspace.

| Route | Purpose |
| --- | --- |
| `GET /health` | Liveness and startup probe; anonymous; returns `{"status":"ok"}` |
| `GET /runtimes` | Each language, its executable, whether it is available, and its version |
| `POST /executions` | Run code or a workspace script; see below |
| `GET /files?path=&recursive=&pattern=` | List a directory; `pattern` is a file-name wildcard such as `*.py` |
| `GET /files/info?path=` | Describe one file or directory |
| `GET /files/content?path=` | Download raw bytes; supports range requests |
| `PUT /files/content?path=&overwrite=true` | Upload raw bytes (request body), creating parent directories |
| `GET /files/text?path=&startLine=&lineCount=` | Read a UTF-8 text file or a line range of it; refuses binary files |
| `PUT /files/text` | `{ path, content, append?, overwrite? }`; writes UTF-8 |
| `POST /files/edit` | `{ path, oldText, newText, replaceAll? }`; `oldText` must match exactly once unless `replaceAll` |
| `POST /files/directories` | `{ path }`; creates it with any missing parents |
| `POST /files/move`, `POST /files/copy` | `{ source, destination, overwrite? }`; files or whole directories |
| `DELETE /files?path=&recursive=` | Delete a file, or a directory (non-empty needs `recursive=true`) |
| `POST /files/search` | `{ query, path?, glob?, isRegex?, caseSensitive?, maxResults? }`; returns `{ path, line, text }` matches |
| `POST /files/zip` | `{ paths: [...], destination, overwrite? }`; entry names are relative to the workspace |
| `POST /files/unzip` | `{ path, destination, overwrite? }`; refuses entries that escape the destination |

### Executions

```json
POST /executions
{
  "language": "python",
  "code": "import sys; print(sys.argv[1])",
  "arguments": ["hello"],
  "workingDirectory": "reports",
  "stdin": null,
  "environment": { "MODE": "draft" },
  "timeoutSeconds": 60
}
```

Send exactly one of `code` or `scriptPath`. `language` is `powershell` (`pwsh`), `python`, `node` (`javascript`), or `bash`; it can be left out for a `scriptPath` ending in `.ps1`, `.py`, `.js`/`.mjs`/`.cjs`, or `.sh`. Inline code is written to a temporary file outside the workspace and deleted afterwards. The working directory defaults to the workspace root, so files a script writes are visible to the file routes.

```json
{ "language": "python", "exitCode": 0, "timedOut": false, "stdout": "hello\n", "stderr": "",
  "stdoutTruncated": false, "stderrTruncated": false, "durationMs": 84 }
```

A non-zero exit code is still a 200, because the script ran and failed. On timeout the whole process tree is killed, `timedOut` is `true`, and `exitCode` is `null`. Stdin is closed unless supplied, so a script that prompts fails at once instead of waiting out the timeout. Each stream keeps the first `MaxOutputChars` characters. Host settings (`Sandbox__*` variables, including the API key) are removed from the scripts' environment.

The run is synchronous: the request returns when the script does. Behind a session pool it must finish inside the single request the pool forwards, so the pool sets `MaxTimeoutSeconds` to 220, under the Container Apps ingress timeout of 240 seconds. For longer work there, start a background process from a script and poll for its output file.

## Settings

All settings are under `Sandbox` (`Sandbox__Name` as environment variables). The image sets `WorkspaceRoot` and the port. The defaults, in [SandboxHostOptions.cs](SandboxHostOptions.cs), are the safe ones; each deployment relaxes only what its platform covers.

| Setting | Default | Notes |
| --- | --- | --- |
| `WorkspaceRoot` | `/workspace` in the image | Blank uses the temp directory |
| `ApiKey` | none | Required while `RequireApiKey` is true; see [Security](#security) |
| `RequireApiKey` | `true` | Fail startup when `ApiKey` is blank; the session pool sets `false` |
| `MaxFileBytes` | 100 MB | Upload, text write, edit, and search file size limit |
| `MaxExtractBytes` | 1 GB | Declared uncompressed size of one unzip |
| `MaxTextReadChars` | 1,048,576 | Characters per text read; page with `startLine` |
| `MaxListEntries` / `MaxSearchResults` | 5000 / 1000 | Results set `truncated: true` past these |
| `DefaultTimeoutSeconds` / `MaxTimeoutSeconds` | 60 / 1800 | Per execution; the session pool sets 220 |
| `MaxOutputChars` | 1,048,576 | Per stream |
| `Executables:<language>` | `pwsh`, `python3`, `node`, `bash` | Override an interpreter path |

## Deploying to a Dynamic Sessions pool

In this repository, [infra/main.bicep](../../infra/main.bicep) creates the pool (`deployDynamicSessions`), and **Release Dynamic Sessions** builds, tests, and installs the image. See [isolated code execution](../../infra/README.md#isolated-code-execution). For a pool outside these templates, push the image to a registry the environment can pull from, then create a custom-container pool with target port `8080`, and set `Sandbox__RequireApiKey=false` and `Sandbox__MaxTimeoutSeconds=220` on its container:

```powershell
az containerapp sessionpool create `
  --name sandbox-sessions --resource-group <rg> --environment <aca-environment> `
  --container-type CustomContainer --image <registry>.azurecr.io/sandboxhost:<tag> `
  --target-port 8080 --cpu 1.0 --memory 2.0Gi `
  --cooldown-period 600 --max-sessions 50 --ready-sessions 2 `
  --network-status EgressEnabled `
  --env-vars Sandbox__RequireApiKey=false Sandbox__MaxTimeoutSeconds=220
```

Add `Liveness` and `Startup` probes on `GET /health:8080` to the pool's `customContainerTemplate` (API version `2025-02-02-preview` or later). Choose `--network-status EgressDisabled` if agent code should not reach the internet. `pip install` and `npm install` then stop working, so preinstall what tools need in [requirements.txt](requirements.txt).

Callers reach a session through the pool's management endpoint. The pool forwards the path unchanged and allocates a session for a new `identifier`:

```http
POST https://<pool-management-endpoint>/executions?identifier=<conversation-or-workspace-id>
Authorization: Bearer <Entra token for https://dynamicsessions.io>
```

The calling identity needs the **Azure ContainerApps Session Executor** role on the pool. Use one identifier per sandbox the agent should keep, such as a workspace or conversation ID, so files survive between turns until the cooldown period ends the session. Stop a session early with `POST <endpoint>/.management/stopSession?identifier=<id>&api-version=2025-02-02-preview`.

## Deploying to a sandbox

The steps below follow the `azure-containerapps-sandbox` SDK (still beta, `0.1.0b4`). Check them against the current sandbox documentation before automating them. The calling identity needs **Container Apps SandboxGroup Data Owner** on the group.

1. Run **Release Sandboxes**, which smoke-tests the image and pushes it to ACR. Then register it as a disk image in the group that `deploySandboxGroup` creates, so new sandboxes boot from it rather than the stock `ubuntu` disk. See [isolated code execution](../../infra/README.md#isolated-code-execution).
2. Create a sandbox from that disk image. Generate a random API key per sandbox and keep it with the binding, such as the workspace or conversation the sandbox serves.
3. Start the server, unless the platform already runs the image entrypoint:

   ```python
   sandbox.exec("cd /app && Sandbox__ApiKey=<key> nohup dotnet SharePointAgent.SandboxHost.dll > /tmp/sandbox-host.log 2>&1 &")
   ```

4. Expose port `8080` with `sandbox.add_port(8080, ip_access_control=...)`, allowing only the egress addresses of the API and AgentHost.
5. Call the API on the port's address with `X-Api-Key`. Wait for `GET /health` before the first call after a create or a resume.

The sandbox data plane also has its own `exec` and file APIs. This host adds what those lack: timeouts that kill the process tree, bounded output, workspace path confinement, text editing, search, and zip, all under the same contract as the session pool.

## Security

- **The session or sandbox is the boundary.** Code runs as the image's non-root `app` user inside a per-session Hyper-V container or a per-sandbox microVM. Workspace path checks keep tool calls predictable; they do not restrict scripts, which can read anything that user can.
- **A key is required unless something else authenticates callers.** A pool session is reachable only through the pool endpoint, which requires an Entra token and the Session Executor role, so the pool turns the key requirement off. A sandbox port has only its IP allow list, so it keeps the requirement. Use a random key for each sandbox: scripts cannot read it from their environment, but they can from the server process's `/proc` entry, since both run as one user. The key keeps out outside callers, not the code that sandbox runs.
- **Nothing to leak.** Do not pass application secrets to a pool or sandbox. Scripts see every variable except `Sandbox__*`.
- **Recursive operations skip symbolic links**, so a link cycle, or a link to `/`, cannot make a copy, zip, or search walk the disk.
