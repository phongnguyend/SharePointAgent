# Laya Native infrastructure

Deploys native Laya in its own Azure Container Apps **CPU consumption** environment with 4 vCPU and 8 GiB RAM. It shares the existing registry and Log Analytics workspace, but does not require GPU quota. See the [runtime/API documentation](../../../backend/SystemOne/LayaNative/README.md).

## Setup

1. Run the shared **Deploy infrastructure** workflow first, including the registry and Log Analytics outputs.
2. In GitHub environment `dev` or `test`, configure `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` and **`LAYA_NATIVE_API_KEY`** (single-line random token, at least 32 characters).
3. Review `parameters.<environment>.json`: workload and environment names must match the shared parameters, `layanativeLocation` is `eastus`, replicas default to zero to one. `AZURE_RESOURCE_GROUP` can override the default resource group.
4. Run **Deploy - System One - Laya Native Infrastructure** (`infra-laya-native.yml`). Initially it creates an app using the bootstrap hello image.
5. Run **Release - System One - Laya Native** (`release-laya-native.yml`). ACR Tasks builds and downloads both pinned checkpoints remotely; no local Docker or model storage is used. The workflow deploys an image digest and tests health, authentication and two real decisions.
6. Release the API and frontend to use **Playgrounds → Decision Making → Laya Native**. The API release reads `layanativeEndpoint` and passes `LAYA_NATIVE_API_KEY` as `LayaNative__ApiKey`. For a local API, set `LayaNative:Endpoint` to the server base URL and `LayaNative:ApiKey` through user secrets or environment variables. The default model is `multilingual`, with a 300-second timeout.

The deployment identity needs resource deployment, managed identity/role assignment management, ACR Tasks and image metadata access, and Container App update/start/stop/secret permissions. The app's identity receives `AcrPull` on the shared registry.

## Reruns and lifecycle

Laya owns its [settings](../../../.github/scripts/laya-native-settings.ps1), [deploy](../../../.github/scripts/deploy-laya-native.ps1), [release](../../../.github/scripts/release-laya-native.ps1), and [start/stop](../../../.github/scripts/manage-laya-native.ps1) scripts. Its API integration uses `LayaNativeClient` and `LayaNativeOptions`, configured through the `LayaNative` section.

Infrastructure reruns retain the deployed image. **`LAYA_NATIVE_IMAGE`** is an optional explicit override for rollback; use a recorded image digest and clear obsolete overrides before future releases. Release skips unchanged builds unless `rebuild` is selected. Source/model pins live in the backend build context.

**Start or Stop Laya Native** (`manage-laya-native.yml`) stops/starts the app through Azure's lifecycle API and waits for the requested status. A stopped app runs no replicas and requests cannot wake it. Starting restores the app; with zero minimum replicas, it can still scale away when idle. Check `/health` separately for model readiness. Storage/logging charges can continue while stopped. Start a stopped app before releasing.

All workflows share the environment deployment lock. Changing the region creates new resources and a new endpoint, leaving the old ones for explicit cleanup.

Deployment name: `layanative-dev` / `layanative-test`. Outputs: `layanativeContainerAppName`, `layanativeEnvironmentName`, `layanativeEndpoint`, `layanativeLocation`.

Static validation (no deployment, models or Docker):

```powershell
az bicep build --file infra/SystemOne/LayaNative/main.bicep --outfile "$env:TEMP/layanative.json"
python -B -m unittest discover -s .github/scripts/tests -p test_laya_native.py
```
