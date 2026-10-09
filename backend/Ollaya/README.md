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
