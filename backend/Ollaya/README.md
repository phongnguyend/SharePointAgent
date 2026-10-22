# Ollaya

[Ollaya](https://github.com/ollaya-dev/ollaya) serving the `winnow:e4b` decision model. A decision model answers typed questions about a state with a probability per option: `choice`, `noul` (yes, no, or unknown), or `score`. It is not a chat model. The image is the official `ghcr.io/ollaya-dev/ollaya:0.12.0-cuda12` with the model baked in, so a replica starting from zero loads it from local disk instead of downloading 8 GB.

## Run locally

`winnow:e4b` needs about 9 GB of GPU memory and takes seconds per request on a CPU. Build on a machine with an NVIDIA GPU:

```powershell
docker build -t ollaya backend/Ollaya
docker run --rm --gpus=all -p 11435:11435 -e OLLAYA_API_KEY=<random-key> ollaya
```

To try the image logic without a GPU, build the CPU variant with Ollaya's small model:

```powershell
docker build --build-arg OLLAYA_IMAGE=ghcr.io/ollaya-dev/ollaya:0.12.0 --build-arg OLLAYA_MODEL=laya -t ollaya-cpu backend/Ollaya
```

## API

`GET /` answers `Ollaya is running` without the key; use it for health. Every other route needs `Authorization: Bearer <OLLAYA_API_KEY>`.

```powershell
$headers = @{ Authorization = "Bearer $key" }
$body = @{
  model = 'winnow:e4b'
  state = 'The package arrived two weeks late and the box was damaged.'
  questions = @{ complaint = @{ type = 'noul'; instructions = 'Is this a complaint?' } }
} | ConvertTo-Json -Depth 10
Invoke-RestMethod -Method Post "$endpoint/v1/systemone" -Headers $headers -ContentType 'application/json' -Body $body
```

The response has `model`, `answers` keyed by question id, and `usage`. `/v1/systemone` is wire-compatible with TypeSafe's API; `/api/decide` adds routing and timings. See the [Ollaya API reference](https://github.com/ollaya-dev/ollaya/blob/main/docs/api.md).

From .NET, use `OllayaClient` (registered by `AddOllayaClient` in the API, Background, and AgentHost), configured by the `Ollaya` section:

| Setting | Default | Notes |
| --- | --- | --- |
| `Ollaya:Endpoint` | blank | Blank means not configured; the releases set it from the `ollaya-<environment>` deployment |
| `Ollaya:ApiKey` | none | Sent as the bearer token |
| `Ollaya:Model` | `winnow:e4b` | Used when a call names no model |
| `Ollaya:DecidePath` / `Ollaya:HealthPath` | `/v1/systemone` / `/` | |
| `Ollaya:TimeoutSeconds` | 300 | Covers a GPU replica starting from zero |

## Troubleshoot response time

Use these endpoints directly against the Ollaya host, either from the HTTP playground or PowerShell. All except `/` require the bearer token. Do not include the token in shared diagnostics.

| Method and path | Purpose |
| --- | --- |
| `GET /` | Server liveness only; a fast response does not establish that the model is loaded or inference is fast |
| `GET /api/version` | Identify the running Ollaya version |
| `GET /api/tags` | List models stored on disk; this does not mean they are loaded |
| `GET /api/ps` | List loaded models, their device, GPU memory allocation, and expiry |
| `POST /api/decide` | Run a decision with loading and inference timings; also supports explicit model preloading |
| `POST /v1/systemone` | TypeSafe-compatible decision endpoint used by the application; use `/api/decide` for timing diagnostics |

### 1. Check the loaded model

```powershell
$endpoint = 'https://<ollaya-host>'
$headers = @{ Authorization = "Bearer $env:OLLAYA_API_KEY" }
Invoke-RestMethod "$endpoint/api/version" -Headers $headers
Invoke-RestMethod "$endpoint/api/ps" -Headers $headers | ConvertTo-Json -Depth 10
```

Check `/api/ps` immediately after a decision:

- `device: "cuda:0"` and nonzero `size_vram` indicate GPU allocation; `device: "cpu"` with `size_vram: 0` indicates CPU execution.
- `expires_at: null` means the model is kept loaded without an idle expiry. This does not prevent replica shutdown or restart.
- An empty `models` array means no model is loaded on the responding replica. If multiple replicas exist, correlate requests with replica logs.

The observed `winnow:e4b` model reported GGUF, 7.5B parameters, Q8_0, and approximately 8 GB of GPU allocation. GPU allocation alone does not prove efficient inference.

### 2. Compare consecutive decision requests

Send a small request twice immediately, with no other requests running. The native endpoint accepts the same `model`, `state`, and `questions` fields used by `/v1/systemone`.

```powershell
$body = @{
  model = 'winnow:e4b'
  state = 'The package arrived two weeks late and the box was damaged.'
  questions = @{
    complaint = @{ type = 'noul'; instructions = 'Is this a complaint?' }
  }
} | ConvertTo-Json -Depth 10

1..2 | ForEach-Object {
  $timer = [Diagnostics.Stopwatch]::StartNew()
  $response = Invoke-RestMethod -Method Post "$endpoint/api/decide" `
    -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 300
  $timer.Stop()
  [pscustomobject]@{
    Request = $_
    ClientSeconds = [math]::Round($timer.Elapsed.TotalSeconds, 3)
    TotalSeconds = [math]::Round($response.total_duration / 1e9, 3)
    LoadSeconds = [math]::Round($response.load_duration / 1e9, 3)
    EvalSeconds = [math]::Round($response.eval_duration / 1e9, 3)
    InputTokens = $response.usage.input_tokens
  }
}
```

Durations are **nanoseconds**, so divide by `1e9` for seconds. Client elapsed time also includes network and infrastructure delays outside the server's reported processing time.

| Observation | Next check |
| --- | --- |
| First request has high `load_duration`; second has zero loading and low `eval_duration` | Cold model loading; model reuse is working |
| Every consecutive request has high `load_duration` | Replica/revision changes, restarts, memory pressure, model eviction, or runner recreation; correlate server and Container Apps system logs |
| `eval_duration` is high with zero loading | Device/GPU utilization during inference, input length, question count, and concurrent load; compare with the small single-question request |
| Client elapsed time greatly exceeds server timing | Ingress, replica startup, network, or an intervening application/proxy; compare direct calls with application calls |
| Total time substantially exceeds loading plus evaluation | Investigate queueing and other server overhead; the difference alone does not identify the cause |

Observed on 2026-10-22, a two-question request took **57.998 s total**, including **57.804 s loading** and **0.193 s inference**. A subsequent request took **0.144 s total**, with **zero loading** and **0.144 s inference**. This demonstrated a loading delay followed by successful reuse, not persistently slow GPU inference. These are diagnostic observations, not latency guarantees.

### 3. Investigate lifecycle and mitigate cold requests

The deployment defaults to `minReplicas: 0` and `maxReplicas: 1` in [the infrastructure template](../../infra/Ollaya/main.bicep). The image bakes model files onto disk and sets `OLLAYA_KEEP_ALIVE=-1`, but does not preload the model at runtime. Both readiness and liveness currently probe `/`, so readiness does not establish model readiness.

When loading repeats, check Container Apps console and system logs around the request timestamps for replica creation, revision changes, restarts, out-of-memory events, and model-loading messages. Record the image/version, replica identity, request timings, input size, and question count. A fast health check does not exclude any of these model-related delays.

- To avoid idle scale-to-zero, set `minReplicas` to `1` in the applicable infrastructure parameters and redeploy. This incurs ongoing GPU cost and does not eliminate loading after deployments or restarts.
- To load the model explicitly, send the following request before testing decisions. It loads without evaluating questions:

  ```powershell
  $preload = @{ model = 'winnow:e4b'; keep_alive = -1 } | ConvertTo-Json
  Invoke-RestMethod -Method Post "$endpoint/api/decide" -Headers $headers `
    -ContentType 'application/json' -Body $preload -TimeoutSec 300
  ```

- For automated startup warmup, couple preloading with model-aware readiness before routing traffic. This requires deployment/startup changes; the current Dockerfile does not implement it. Preloading shifts the initial wait to startup rather than making loading faster.
- Increasing the client timeout only allows more time to complete; it does not improve performance. Do not disable keep-alive or unload the model between benchmark requests.

See the upstream [API contract](https://github.com/ollaya-dev/ollaya/blob/main/docs/api.md) for model status, preloading, and timing fields. Check the deployed version when comparing behavior with upstream documentation.

## Image settings

| Setting | Value | Why |
| --- | --- | --- |
| `OLLAYA_MODELS` | `/opt/ollaya/models` | The base image declares its default model folder as a volume, and files written under a volume path during a build may not reach the image |
| `OLLAYA_KEEP_ALIVE` | `-1` | Keep the model on the GPU for the replica's life; scaling to zero is what releases it |
| `OLLAYA_API_KEY` | from the deployment | Without it the server answers anyone who can reach it |
| `OLLAYA_HOST` | `0.0.0.0:11435` | Set by the base image |

The `cuda12` base runs on both the 570 driver that Container Apps serverless GPUs use today and the 580 driver they are moving to; the plain `cuda` tag needs 580 or newer. Change the version in the `OLLAYA_IMAGE` build argument to upgrade Ollaya.

## Deployment

See [Ollaya decision model](../../infra/README.md#ollaya-decision-model): **Deploy Ollaya infrastructure** creates a Container Apps environment of its own, in a region of your choice, with the app on a serverless T4 GPU, and **Release Ollaya** builds this image and rolls it out. The apps reach it over HTTPS from any environment or region.
