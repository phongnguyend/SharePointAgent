# Laya native

Serves Laya's native Python System One API without Ollaya. The [Dockerfile](Dockerfile) uses **CPU-only PyTorch**, four threads and preloads the English and multilingual checkpoints. [Infrastructure and workflow setup](../../../infra/SystemOne/LayaNative/README.md).

## Build and release

Run **Release - System One - Laya Native** in GitHub Actions. `az acr build` compiles the image and downloads models remotely in Azure Container Registry Tasks. No local Docker build or model download is needed.

The upstream source commit and CPU PyTorch version are pinned in `Dockerfile`. [download_models.py](download_models.py) pins a separate Hugging Face revision for each checkpoint and fetches only inference weights, encoder configuration and tokenizers. The runtime loads those local paths with Hugging Face/Transformers offline mode enabled. Both checkpoints remain resident for the life of a replica; automatic task detection is off. The separate `typed-decisions` checkpoint is not included, so requests should use `english`, `multilingual`, or automatic language routing without a model override.

Change source/model pins deliberately to upgrade. Releases reuse content-tagged images and deploy immutable image digests. Base-image tags and transitive Python packages can change on a forced rebuild; this is not a complete dependency lock. Use a recorded image digest for an identical rollback.

## API

| Endpoint | Purpose | Authentication |
| --- | --- | --- |
| `GET /health` | Basic liveness; authenticated calls also show loaded checkpoints and devices | Optional |
| `POST /v1/systemone` | Typed `noul`, `choice`, and `score` decisions | Bearer token |
| `POST /v1/systemone/batch` | Batched decisions | Bearer token |

Port inside the container: **8000**; Azure ingress uses HTTPS on 443. `LAYA_API_KEY` comes from the Container App secret. The entrypoint refuses missing, short or multiline tokens. `LAYA_JEV_STRICT=1` enables the upstream strict Jev response envelope.

```powershell
$headers = @{ Authorization = "Bearer $env:LAYA_API_KEY" }
Invoke-RestMethod "$endpoint/health" -Headers $headers
$request = @{
    model = 'english'
    state = 'The package arrived late and damaged.'
    questions = @{ complaint = @{ type = 'noul'; instructions = 'Is this a complaint?' } }
} | ConvertTo-Json -Depth 10
Invoke-RestMethod "$endpoint/v1/systemone" -Method Post -Headers $headers `
    -ContentType 'application/json' -Body $request -TimeoutSec 600
```

The native API and limits are documented [upstream](https://github.com/NandhaKishorM/laya/blob/6f0496f797dc7127265d62e1543a62de507d943c/docs/http-api.md). This image caps concurrent admitted requests at four, per-request token budget at 2,048 and batch forward tokens at 8,192. Limits can be adjusted in the Dockerfile after workload testing.

## Latency and troubleshooting

1. Call authenticated `/health`. Confirm `english` and `multilingual` are loaded and the device is `cpu`; this deployment intentionally does not provision a GPU.
2. Send an identical small decision twice. Release summaries record first and warm times. Large requests and concurrent work can still queue on the inference worker.
3. `minReplicas: 0` saves idle compute but introduces image-pull and checkpoint-loading delays. Set it to one for warm service. Both models preload before the HTTP server starts; probes allow ten minutes for startup.
4. Inspect console logs for offline artifact errors, incompatible Python dependencies, OOM or repeated restarts. Fix model/dependency pins and rebuild remotely; do not enable runtime downloads to hide missing artifacts.
5. For rollback, set `LAYA_NATIVE_IMAGE` to a previous release digest and run the infrastructure workflow. Clear that override before subsequent normal releases.

This adds deployment packaging, not a production-support certification. Test the pinned runtime and model quality with your workload before production use.
