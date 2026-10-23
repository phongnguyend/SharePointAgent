# Winnow native

Serves `Winnow-E4B` Q8 directly with the upstream native Winnow/llama.cpp runtime, without Ollaya.

The [Dockerfile](Dockerfile) targets a T4 GPU with CUDA 12.8, text-only decisions, 8K context and one inference branch. It disables MTP and vision. [Infrastructure and workflow setup](../../../infra/SystemOne/WinnowNative/README.md).

## Build and release

Use **Release - System One - Winnow Native** in GitHub Actions. It runs `az acr build` in Azure Container Registry Tasks: compilation, model downloads and image storage happen in Azure. Do not build this image locally when disk space is limited. The GitHub runner uploads only this small build context.

The source commit is pinned in `Dockerfile`; its `runtime.lock.json` pins the patched llama.cpp runtime. [download_model.py](download_model.py) pins the model repository revision and checks both the 8,005,437,472-byte size and SHA256 before accepting the model. The image contains only the E4B Q8 weights, not a vision projector or draft model. To upgrade, update the source/model pins deliberately and rerun release. A source upgrade can also require updating the upstream manifest checksum used by `--target`.

Content-based image tags avoid rebuilding unchanged files. Releases deploy the resolved image digest. `rebuild` rebuilds the same pinned source and model; base-image tags, apt packages and unpinned transitive build dependencies can still change. Reuse the deployed image digest for an identical rollback.

## API

| Endpoint | Purpose | Authentication |
| --- | --- | --- |
| `GET /health` | Model/server readiness | None; used by probes |
| `GET /v1/models` | Loaded model metadata | Bearer token |
| `POST /v1/systemone` | Typed `noul`, `choice`, and `score` decisions | Bearer token |
| `POST /v1/winnow/inspect` | Prompt/token/runtime diagnostics | Bearer token |
| `POST /v1/chat/completions` | Upstream chat API | Bearer token |

Port inside the container: **8091**. Azure ingress exposes HTTPS on 443. Set `WINNOW_API_KEY` through the Container App secret. The entrypoint refuses missing, short or multiline keys and passes a private key file to the native server; the upstream `WINNOW_API_KEY` variable alone only configures clients.

Example PowerShell request (`$endpoint` is the deployment output):

```powershell
$headers = @{ Authorization = "Bearer $env:WINNOW_API_KEY" }
$request = @{
    model = 'Winnow-E4B'
    state = 'The package arrived late and damaged.'
    questions = @{ complaint = @{ type = 'noul'; instructions = 'Is this a complaint?' } }
    winnow = @{ diagnostics = $true }
} | ConvertTo-Json -Depth 10
Invoke-RestMethod "$endpoint/v1/systemone" -Method Post -Headers $headers `
    -ContentType 'application/json' -Body $request -TimeoutSec 600
```

## Latency and troubleshooting

1. Check `GET /health`, then send the same decision twice. A successful health request alone does not measure inference. Release summaries report first and warm decision times.
2. With `minReplicas: 0`, idle replicas disappear. Starting requires pulling the large image and loading GPU memory. Use `minReplicas: 1` for warm service, accepting ongoing GPU cost. Startup probes allow ten minutes after container startup; image pull happens before that.
3. Inspect Container App console/system logs for model loading, CUDA initialization, memory pressure, quota or image-pull failures. The build targets compute capability 7.5; validate it on the provisioned T4 before relying on it for production traffic.
4. If every request is slow, inspect the `winnow` diagnostics for queue time, evaluation time and context evictions, and compare small versus large requests. Only one inference thread owns the model; concurrent requests can queue. Mixing chat and decisions can evict contexts with exclusive memory mode.
5. A failed release smoke test leaves the newly deployed revision for investigation. Roll back by setting `WINNOW_NATIVE_IMAGE` to a previously recorded digest and running the infrastructure workflow, then clear the override before later releases.

There is no Ollaya `/api/ps`, `/api/pull`, or `load_duration` envelope here. Native timing fields are documented in the [upstream API](https://github.com/EldanRing/winnow-inference/blob/a50c7d0d8cbc5c3ba34987c5c23ea114e44ce6b5/docs/API.md). Packaging the native runtime is not a production-support guarantee; GPU compatibility, throughput and decision quality still require deployment testing.
