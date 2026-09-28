# Azure infrastructure

The Bicep template provisions shared Azure resources and describe the application's deployment targets. Python deployment scripts and their dependencies have been removed.

All Azure resources are defined in `main.bicep`: shared services, SQL, runtime identities, Foundry, Container Apps, Static Web Apps, role assignments and the Foundry secret connection.

Main resource names start with `workloadName`, followed by the environment. ACR and Storage use the compact prefix without hyphens; for example, ACR uses `<workloadName><environment>cr<uniqueSuffix>`. Child resources retain their service-specific names. Changing a resource name creates a new resource rather than renaming an existing one; registries created with the previous `cr<workloadName>...` pattern and their images are not migrated automatically.

## GitHub Actions deployment

For a complete application release, run **Actions → Release → Run workflow** using [release.yml](../.github/workflows/release.yml). Select `dev` or `test`; the selected branch or tag supplies all application code. The release workflow deploys the API, Background and MarkItDown to ACA and AgentHost to Foundry, then builds and publishes the frontend to **Azure Static Web Apps**. The deployed API URL is passed directly into the frontend build as `VITE_API_BASE_URL`.

Run the infrastructure workflow to create the Static Web App for each environment, then store its deployment token as `AZURE_STATIC_WEB_APPS_API_TOKEN` in the matching GitHub environment. Set `FRONTEND_ORIGIN` to its default HTTPS origin or configured custom domain, and register `<FRONTEND_ORIGIN>/auth-redirect.html` as an Entra **Single-page application** redirect URI. The API uses this origin for CORS. Each release publishes to that Static Web App's production site, rather than creating a preview environment. The template outputs `staticWebAppName` and `staticWebAppUrl`. Static Web Apps uses the Free tier by default; all supplied environment JSON files set `staticWebAppLocation` to `eastasia`. Change `staticWebAppSku` or the location in those files as needed.

The frontend is deployed only after backend deployment succeeds. A frontend failure does not roll back the backend. SPA routes use `staticwebapp.config.json`; authentication continues through the application's Entra integration.

Run **Actions → Deploy infrastructure → Run workflow** first. `infra.yml` provisions `main.bicep`, including API, Background and MarkItDown Container Apps with `mcr.microsoft.com/k8se/quickstart:latest`. The API and MarkItDown use port 80 and `/` readiness checks until released; Background has no ingress. Provisioning does not build images, run SQL migrations or publish a Foundry agent version.

Then run **Release**. It reads the saved `infra-<environment>` deployment outputs, builds four Linux/amd64 images in ACR, applies SQL migrations and runtime grants, and applies `main.bicep` with `deployApplicationImages=true` and the release image tags. This replaces the hello images and configures application secrets, API port 8080 and MarkItDown port 8000 with `/health` probes. It publishes AgentHost to Foundry and the frontend to Static Web Apps. Image tags contain the commit SHA, run ID and attempt.

Configure `API_IMAGE`, `BACKGROUND_IMAGE` and `MARKITDOWN_IMAGE` in the selected GitHub environment using the settings table below. Set all three to existing application images, preferably immutable tags or digests in this environment's ACR. Infrastructure automatically enables application runtime settings, ports and health checks and reads the application secrets from the same GitHub environment. SQL migrations and runtime database grants must already exist; infrastructure does not run them. Partial image configuration is rejected.

Leave all three variables unset for the default hello images. Re-running infrastructure with them unset resets ACA apps to hello images. Release builds and deploys new images independently and does not update these GitHub variables; set them to the desired release references before the next infrastructure run. Both workflows share an environment concurrency group. AgentHost is published separately by Release and is not controlled by these ACA image variables.

Create matching GitHub environments with these settings:

| Name | Kind | Purpose |
| --- | --- | --- |
| `API_IMAGE` | Optional variable | Existing API image, e.g. `YOUR_REGISTRY.azurecr.io/api:EXISTING_TAG`; set all three image variables together |
| `AZURE_CLIENT_ID` | Secret | Application/client ID of the deployment principal used for Azure OIDC login |
| `AZURE_RESOURCE_GROUP` | Optional variable | Defaults to `rg-<workloadName>-<environment>` |
| `AZURE_RESOURCE_GROUP_LOCATION` | Variable | Resource-group metadata location |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Secret | Deployment token for this environment's Static Web App; required by `release.yml` |
| `AZURE_SUBSCRIPTION_ID` | Secret | Azure subscription ID to deploy resources into |
| `AZURE_TENANT_ID` | Secret | Entra tenant ID used for Azure OIDC login and SharePoint access |
| `BACKGROUND_IMAGE` | Optional variable | Existing Background image, e.g. `YOUR_REGISTRY.azurecr.io/background:EXISTING_TAG`; set all three image variables together |
| `BOOTSTRAP_ADMIN_EMAIL` | Variable | Initial application Global Admin |
| `FRONTEND_ORIGIN` | Variable | Frontend HTTPS origin for CORS; configure its Entra redirect URI separately |
| `MARKITDOWN_API_KEY` | Secret | Shared MarkItDown authentication key, at least 32 characters |
| `MARKITDOWN_IMAGE` | Optional variable | Existing MarkItDown image, e.g. `YOUR_REGISTRY.azurecr.io/markitdown:EXISTING_TAG`; set all three image variables together |
| `SHAREPOINT_CLIENT_ID` | Variable | Existing SharePoint/Entra application client ID; tenant uses `AZURE_TENANT_ID` |
| `SHAREPOINT_CLIENT_SECRET` | Secret | Graph application credential |
| `SHAREPOINT_CLIENT_STATE` | Secret | Webhook validation secret, at least 16 characters |
| `SHAREPOINT_DOCUMENT_LIBRARY_NAME` | Variable | Name of the source SharePoint document library |
| `SHAREPOINT_SITE_HOSTNAME` | Variable | Hostname of the source SharePoint site |
| `SHAREPOINT_SITE_PATH` | Variable | Path of the source SharePoint site |
| `SQL_ENTRA_ADMINISTRATOR_PRINCIPAL_TYPE` | Optional variable | `Application` (default), `Group` or `User`; use `Application` for a managed identity or service principal |
| `SQL_ENTRA_ADMIN_OBJECT_ID` | Variable | Object ID of the SQL administrator matching the configured principal type, not its application/client ID |

Configure the deployment principal's federated credential with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and subject `repo:<owner>/<repository>:environment:<environment>`. It needs deployment and role-assignment permissions at the resource-group scope; creating a resource group requires subscription permission.

Parameter files are `infra/parameters.<environment>.json`. Each supplied environment file sets `sqlLocation` to `southeastasia`. The template defaults SQL and other resources to the environment's `location` unless overridden. Configure `sqlLocation`, `foundryLocation` or `contentSafetyLocation` in the parameter files where needed. Confirm regional model/Foundry availability and quota. The chat model, version and capacity are configurable.

For a direct deployment:

```powershell
az deployment group create --resource-group YOUR_RESOURCE_GROUP `
  --template-file infra/main.bicep `
  --parameters infra/parameters.dev.json sqlEntraAdminObjectId=YOUR_PRINCIPAL_OBJECT_ID
```

## Deployment behavior

SQL migrations still connect as the GitHub OIDC deployment identity. When the SQL administrator is another application, group or user, grant the deployment identity the required SQL migration and user-management permissions beforehand. Changing the administrator type does not grant that access automatically. Foundry permissions remain assigned to the deployment identity independently of the SQL administrator.

Provision infrastructure once before the first release. Release validates frontend settings, reads infrastructure outputs, builds images, migrates SQL, deploys application images and settings, publishes and routes the Foundry version, checks backend health, and publishes the frontend.

SQL migrations run as the deployment principal, while runtime identities receive only `db_datareader` and `db_datawriter`. The workflow temporarily allows its runner IP through the SQL firewall and removes that rule in an `always()` cleanup step. Credential-bearing parameter files live only in the runner temporary directory and are deleted during cleanup; they are not uploaded as artifacts. Keep migrations compatible with the previously deployed application while rolling out a new version. A failed deployment does not automatically roll back schema or infrastructure changes.

See [AgentHost configuration](../backend/SharePointAgent.AgentHost/README.md), [Foundry deployment](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent) and [agent identity/routing](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-agent). The frontend is hosted separately.

## Runtime configuration and networking

Azure SQL uses Entra-only authentication and a Basic database (5 DTUs, maximum 2 GB) by default. Override `sqlDatabaseSku` with an S-series SKU when more capacity is needed. SQL permits authenticated connections from Azure services through the `0.0.0.0` firewall rule; this is Azure-wide, not subscription-only. Non-Azure deployment clients need their own firewall rule. Private networking is not configured.

API and Background have separate user-assigned identities selected through `AZURE_CLIENT_ID`; SQL connection strings use their client IDs. Foundry creates AgentHost's execution identity. MarkItDown has only ACR image-pull access.

API listens on 8080, MarkItDown on 8000, and Background has no ingress. Background stays at one replica. MarkItDown uses external HTTPS so Foundry can reach it, and requires `X-Api-Key` before accepting upload bodies; `/health` remains public. Supply the same key as `MARKITDOWN_API_KEY` on the MarkItDown and `MarkItDown:ApiKey` on callers. Local MarkItDown instances with no key configured retain unauthenticated behavior. OfficeCLI is disabled because the images do not include it.

## Content Safety

`deployContentSafety=true` in the supplied parameter files provisions S0. The template configures API managed-identity access; AgentHost's role must be assigned when registering the hosted agent. `allowContentSafetyApiKeyAuth` controls local key authentication. Outputs include `contentSafetyEndpoint` and `contentSafetyResourceId`, never keys. Disabling the deployment flag does not delete a previously created resource in incremental deployment mode.

## Validation

Compile `main.bicep` with `az bicep build`. Compilation does not verify subscription quota, permissions, networking or Foundry regional availability. Application deployments use actual images and change runtime configuration; review them before applying to an existing environment.
