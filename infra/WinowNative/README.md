# Winow Native infrastructure

Deploys native Winnow in its own Azure Container Apps environment using a serverless **T4** profile (8 vCPU, 56 GiB RAM, 16 GB GPU). It reuses the registry and Log Analytics workspace from the shared `infra-<environment>` deployment. See the [runtime/API documentation](../../backend/WinowNative/README.md).

## Setup

1. Run the shared **Deploy infrastructure** workflow first. The `containerRegistryName`, `containerRegistryLoginServer` and `logAnalyticsWorkspaceName` outputs must exist.
2. In GitHub environment `dev` or `test`, configure the existing OIDC secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, plus **`WINNOW_NATIVE_API_KEY`** (single-line random token, at least 32 characters). Use a distinct token from Ollaya and Laya.
3. Review `parameters.<environment>.json`. `workloadName` must match the shared parameters. `winownativeLocation` defaults to `eastus` in the files; select a region with available serverless T4 quota. Default replicas: zero to one. Optional GitHub variable `AZURE_RESOURCE_GROUP` overrides `rg-<workload>-<environment>`.
4. Run **Deploy Winow Native Infrastructure** (`infra-winow-native.yml`). The first deployment uses a public bootstrap hello image, not an inference server.
5. Run **Release Winow Native** (`release-winow-native.yml`). Compilation and model downloads run remotely in ACR Tasks. The workflow installs the image and secret, then tests health, authentication and two decisions. It does not use local Docker.
6. Release the API and frontend to use **Playgrounds → Decision Making → Winnow Native**. The API release reads `winownativeEndpoint` and passes `WINNOW_NATIVE_API_KEY` as `WinowNative__ApiKey`. For a local API, set `WinowNative:Endpoint` to the server base URL and `WinowNative:ApiKey` through user secrets or environment variables. The default model is `Winnow-E4B`, with a 300-second timeout.

The deployment identity needs the same permissions as the Ollaya workflows: resource deployment, managed identity and role assignment management, ACR Tasks build/push and image metadata access, Container App update/start/stop, and secret management. The app's own identity receives only `AcrPull` on the shared registry.

## Reruns, releases and lifecycle

Winnow owns its [settings](../../.github/scripts/winow-native-settings.ps1), [deploy](../../.github/scripts/deploy-winow-native.ps1), [release](../../.github/scripts/release-winow-native.ps1), and [start/stop](../../.github/scripts/manage-winow-native.ps1) scripts. Its API integration uses `WinnowNativeClient` and `WinnowNativeOptions`; the configuration section remains `WinowNative` to match the existing deployment folder.

- Infrastructure reruns preserve the current container image. Optional **`WINNOW_NATIVE_IMAGE`** overrides it, useful for rollback to a recorded digest. Clear an obsolete override before future infrastructure reruns.
- `rebuild` on the release workflow forces a remote rebuild; otherwise unchanged content tags are reused. The deployed image is pinned by digest.
- **Start or Stop Winow Native** (`manage-winow-native.yml`) calls the Azure Container App start/stop API and waits for its running status. Stop removes running replicas and requests cannot wake it; registry storage and logging costs can remain. Start restores the app, but `minReplicas: 0` still allows idle scale-to-zero. Running status is not model readiness.
- If stopped, start before releasing. Changing region creates a new app/environment and endpoint; the old resources remain until explicitly removed.
- All three workflows use the existing environment deployment concurrency lock.

Deployment name: `winownative-dev` / `winownative-test`. Output names: `winownativeContainerAppName`, `winownativeEnvironmentName`, `winownativeEndpoint`, `winownativeLocation`.

Static validation (no deployment, models or Docker):

```powershell
az bicep build --file infra/WinowNative/main.bicep --outfile "$env:TEMP/winownative.json"
python -B -m unittest discover -s .github/scripts/tests -p test_winnow_native.py
```
