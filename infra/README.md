# Azure infrastructure

[main.bicep](main.bicep) defines the shared Azure resources for the `dev` and `test` environments: SQL, Storage, Service Bus, Search, Azure OpenAI, Foundry, the container registry, Static Web Apps, monitoring, the API and Background runtime identities, and role assignments. Components with their own template and workflow deploy on top of it:

| Folder | Deployment | Contents |
| --- | --- | --- |
| [ContainerApps](ContainerApps/main.bicep) | `container-apps-<environment>` | The Container Apps environment and the API, Background, MarkItDown, and PageIndex apps |
| [DynamicSessions](DynamicSessions/main.bicep) | `dynamic-sessions-<environment>` | The sandbox host session pool, in the Container Apps environment |
| [Sandboxes](Sandboxes/main.bicep) | `sandboxes-<environment>` | The sandbox group |
| [Ollaya](Ollaya/main.bicep) | `ollaya-<environment>` | Ollaya's own GPU environment and app |

Use this guide in deployment order, or jump to an operational task:

- [First deployment](#first-deployment)
- [Environment setup](#environment-setup) and [GitHub settings table](#github-environment-settings)
- [Infrastructure and service releases](#github-actions-deployment)
- [SQL access](#sql-access)
- [Service configuration](#service-configuration)
- [Operations and troubleshooting](#deployment-behavior)
- [Capacity and networking](#infrastructure-reference)

## First deployment

1. Create the matching GitHub environment (`dev` or `test`), configure [OIDC access](#deployment-identity), and fill in the [settings table](#github-environment-settings) for the services you will deploy.
2. Review `infra/parameters.<environment>.json` and run **Deploy infrastructure**, then review `infra/ContainerApps/parameters.<environment>.json` and run **Deploy Container Apps infrastructure**. The default Container App images are placeholders; provisioning alone does not install the APIs.
3. [Retrieve the frontend origin and deployment token](#retrieve-the-frontend-origin-and-deployment-token), save them in GitHub, and register the Entra redirect URI.
4. [Grant the release identity SQL access](#grant-the-release-identity-sql-access), then run **Release Database migrations**.
5. [Configure API and Background SQL access](#configure-runtime-sql-access-manually) before starting those services.
6. Release **MarkItDown**, **PageIndex** if used, and **AgentHost**. Once Foundry creates AgentHost's identity, grant its runtime SQL access; rerun its release if it failed before routing.
7. Release **API**, **Background**, and **Frontend**. Verify **Admin → Service health** and test a chat request.

Use separate runs wherever the initial SQL grants require a manual step. Subsequent releases can use the combined **Release services** workflow.

## Environment setup

### Deployment identity

Configure the deployment principal's federated credential with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and subject `repo:<owner>/<repository>:environment:<environment>`. It needs deployment and role-assignment permissions at the resource-group scope; creating a resource group requires subscription permission.

### Infrastructure parameters

Parameter files are `infra/parameters.<environment>.json`. Each supplied environment file sets `sqlLocation` to `southeastasia`. The template defaults SQL and other resources to the environment's `location` unless overridden. Configure `sqlLocation`, `foundryLocation` or `contentSafetyLocation` in the parameter files where needed. Confirm regional model/Foundry availability and quota. The chat model, version and capacity are configurable.

### GitHub environment settings

Configure these settings under **Settings → Environments → dev/test → Environment variables / Environment secrets**. This single alphabetical table covers all GitHub environment settings referenced by the deployment and management workflows. Only configure settings needed by the workflows and providers you use; signing settings apply to API only.

| Name | Kind | Purpose / runtime mapping |
| --- | --- | --- |
| `API_IMAGE` | Optional variable | Existing API image, e.g. `YOUR_REGISTRY.azurecr.io/api:EXISTING_TAG`; set all three image variables together |
| `AZURE_CLIENT_ID` | Secret | Application/client ID of the deployment principal used for Azure OIDC login |
| `AZURE_RESOURCE_GROUP` | Optional variable | Defaults to `rg-<workloadName>-<environment>` |
| `AZURE_RESOURCE_GROUP_LOCATION` | Variable | Resource-group metadata location |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Secret | Deployment token for this environment's Static Web App; required by `release-frontend.yml` |
| `AZURE_SUBSCRIPTION_ID` | Secret | Azure subscription ID to deploy resources into |
| `AZURE_TENANT_ID` | Secret | Entra tenant ID for Azure OIDC deployment login; independent of the SharePoint tenant |
| `BACKGROUND_IMAGE` | Optional variable | Existing Background image, e.g. `YOUR_REGISTRY.azurecr.io/background:EXISTING_TAG`; set all three image variables together |
| `BOOTSTRAP_ADMIN_EMAIL` | Variable | Initial application Global Admin |
| `DOCUMENTSIGNING__ADOBESIGN__ACCESSTOKENURL` | Variable | API signing: Required for OAuth setup. Initial OAuth token exchange URL, e.g. `https://api.sg1.adobesign.com/oauth/v2/token`. Always used for token exchange, with no callback endpoint fallback. The token response's `api_access_point` takes precedence for the displayed API origin. Does not change the configured API origin or refresh-token endpoint used for signing. Runtime key: `DocumentSigning__AdobeSign__AccessTokenUrl`. |
| `DOCUMENTSIGNING__ADOBESIGN__APIACCESSPOINT` | Variable | API signing: Actual regional `api_access_point` from OAuth, e.g. `https://api.na1.adobesign.com`; omit `/api/rest/v6`. Always forwarded during API release, including when signing is disabled for OAuth setup; required when Adobe signing is enabled. Runtime key: `DocumentSigning__AdobeSign__ApiAccessPoint`. |
| `DOCUMENTSIGNING__ADOBESIGN__AUTHURL` | Variable | API signing: Required when Adobe signing is enabled or OAuth setup is configured. No deployment fallback. For Singapore use `https://secure.sg1.adobesign.com/public/oauth/v2`. Runtime key: `DocumentSigning__AdobeSign__AuthUrl`. |
| `DOCUMENTSIGNING__ADOBESIGN__CLIENTID` | Variable | API signing: Adobe OAuth application ID. Runtime key: `DocumentSigning__AdobeSign__ClientId`. |
| `DOCUMENTSIGNING__ADOBESIGN__CLIENTSECRET` | Secret | API signing: Active OAuth application client secret. Runtime key: `DocumentSigning__AdobeSign__ClientSecret`. |
| `DOCUMENTSIGNING__ADOBESIGN__ENABLED` | Variable | API signing: `false` by default; set `true` once all Adobe settings are configured. Runtime key: `DocumentSigning__AdobeSign__Enabled`. |
| `DOCUMENTSIGNING__ADOBESIGN__OAUTHREDIRECTURI` | Optional variable | API signing: `https://YOUR_FRONTEND/adobe-sign-callback.html`; register this exact URL in Adobe to use the Global Admin token page. Runtime key: `DocumentSigning__AdobeSign__OAuthRedirectUri`. |
| `DOCUMENTSIGNING__ADOBESIGN__REFRESHTOKEN` | Secret | API signing: Refresh token authorized by the shared sender with `agreement_read:self agreement_write:self agreement_send:self user_login:self`. Runtime key: `DocumentSigning__AdobeSign__RefreshToken`. |
| `DOCUMENTSIGNING__DOCUSIGN__ACCOUNTID` | Variable | API signing: Shared sender's API account GUID. Runtime key: `DocumentSigning__DocuSign__AccountId`. |
| `DOCUMENTSIGNING__DOCUSIGN__APIBASEURL` | Variable | API signing: Developer: `https://demo.docusign.net/restapi/v2.1/`; production: account-specific REST base URL ending `/restapi/v2.1/`. Runtime key: `DocumentSigning__DocuSign__ApiBaseUrl`. |
| `DOCUMENTSIGNING__DOCUSIGN__CLIENTID` | Variable | API signing: Integration key / application client GUID. Runtime key: `DocumentSigning__DocuSign__ClientId`. |
| `DOCUMENTSIGNING__DOCUSIGN__DEMO` | Variable | API signing: `true` for developer accounts; `false` for production. Runtime key: `DocumentSigning__DocuSign__Demo`. |
| `DOCUMENTSIGNING__DOCUSIGN__ENABLED` | Variable | API signing: `false` by default; set `true` once all DocuSign settings are configured. Runtime key: `DocumentSigning__DocuSign__Enabled`. |
| `DOCUMENTSIGNING__DOCUSIGN__PRIVATEKEYPEM` | Secret | API signing: Full RSA private key PEM, including BEGIN/END lines and actual line breaks. Runtime key: `DocumentSigning__DocuSign__PrivateKeyPem`. |
| `DOCUMENTSIGNING__DOCUSIGN__SENDERUSERID` | Variable | API signing: Shared sender's API user GUID. Runtime key: `DocumentSigning__DocuSign__SenderUserId`. |
| `DOCUMENTSIGNING__RETURNURL` | Variable | API signing: `https://YOUR_FRONTEND/attachment-files`; required for the DocuSign return redirect. Runtime key: `DocumentSigning__ReturnUrl`. |
| `FRONTEND_ORIGIN` | Variable | Frontend HTTPS origin for CORS; configure its Entra redirect URI separately |
| `MARKITDOWN_API_KEY` | Secret | Shared MarkItDown authentication key, at least 32 characters |
| `MARKITDOWN_IMAGE` | Optional variable | Existing MarkItDown image, e.g. `YOUR_REGISTRY.azurecr.io/markitdown:EXISTING_TAG`; set all three image variables together |
| `OLLAYA_API_KEY` | Secret | Deploy Ollaya infrastructure and Release Ollaya: bearer key the Ollaya server requires, at least 32 characters; Ollaya ingress is external. API, Background, and AgentHost releases pass it as `Ollaya__ApiKey` when set. Container key: `OLLAYA_API_KEY`. |
| `OLLAYA_IMAGE` | Optional variable | Released Ollaya image, e.g. `YOUR_REGISTRY.azurecr.io/ollaya:EXISTING_TAG`; keeps it when Deploy Ollaya infrastructure runs again. Bicep parameter: `ollayaImage`. |
| `OLLAYA_MODEL` | Optional variable | Release Ollaya: model baked into the image and used by the smoke test; defaults to `winnow:e4b`. |
| `PAGEINDEX_AZURE_API_KEY` | Secret | Release PageIndex: key for the provisioned Azure OpenAI resource; key authentication must be enabled. Container key: `AZURE_API_KEY`. |
| `PAGEINDEX_AZURE_API_VERSION` | Optional variable | Release PageIndex: defaults to `2024-10-21`; select a version supported by the deployment. Container key: `AZURE_API_VERSION`. |
| `PAGEINDEX_INDEX_MODEL` | Optional variable | Release PageIndex: defaults to `azure/<chatDeploymentName>` from infrastructure; use `azure/<deployment-name>`. Container key: `PAGEINDEX_INDEX_MODEL`. |
| `PAGEINDEX_SERVICE_API_KEY` | Secret | Release PageIndex: separately generated service key, at least 32 characters; see [key generation](../backend/PageIndex/README.md#generate-the-service-api-key). Container key: `PAGEINDEX_SERVICE_API_KEY`. |
| `SANDBOX_HOST_IMAGE` | Optional variable | Sandbox host image the Dynamic Sessions pool runs, e.g. `YOUR_REGISTRY.azurecr.io/sandboxhost:EXISTING_TAG`; keeps it when Deploy Dynamic Sessions infrastructure runs again. Independent of the three application image variables; Sandboxes use registered disk images instead. Bicep parameter: `sandboxHostImage`. |
| `SANDBOX_DISK_IMAGE_ID` | Optional variable | Resource ID of the disk image registered in the sandbox group; API sandboxes boot from it. Read by Release API; when unset, the current value is kept. Container key: `AgentWorkspace__Sandboxes__DiskImageId`. |
| `SHAREPOINT_CLIENT_ID` | Variable | Existing SharePoint/Entra application client ID in the SharePoint tenant |
| `SHAREPOINT_CLIENT_SECRET` | Secret | Graph application credential |
| `SHAREPOINT_CLIENT_STATE` | Secret | Webhook validation secret, at least 16 characters |
| `SHAREPOINT_DOCUMENT_LIBRARY_NAME` | Variable | Name of the source SharePoint document library |
| `SHAREPOINT_SITE_HOSTNAME` | Variable | Hostname of the source SharePoint site |
| `SHAREPOINT_SITE_PATH` | Variable | Path of the source SharePoint site |
| `SHAREPOINT_TENANT_ID` | Variable | Required for application deployments: Entra tenant ID for SharePoint Graph access and application sign-in; can differ from `AZURE_TENANT_ID` and has no fallback |
| `SQL_ENTRA_ADMIN_OBJECT_ID` | Variable | Object ID of the SQL administrator matching the configured principal type, not its application/client ID |
| `SQL_ENTRA_ADMINISTRATOR_PRINCIPAL_TYPE` | Optional variable | `Application` (default), `Group` or `User`; use `Application` for a managed identity or service principal |

<a id="generate-the-markitdown-api-key"></a>

### Generate the MarkItDown and PageIndex API keys

Use the same PowerShell script for either service. Each run generates one cryptographically random 32-byte key encoded as Base64. Run it separately for MarkItDown and PageIndex:

```powershell
$keyBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $rng.GetBytes($keyBytes)
    [Convert]::ToBase64String($keyBytes)
}
finally {
    $rng.Dispose()
}
```

Under **GitHub repository → Settings → Environments → your environment → Environment secrets**, save the entire output as `MARKITDOWN_API_KEY` or `PAGEINDEX_SERVICE_API_KEY`, depending on which service you are configuring. Preserve trailing `=` characters in the Base64 value. Generate a separate key for each service and environment, and keep them out of source control.

- **MarkItDown:** save `MARKITDOWN_API_KEY`. The deployment supplies it to the service and its callers. To rotate it, run **Release MarkItDown**, **Release API**, **Release Background**, and **Release AgentHost**.
- **PageIndex:** save `PAGEINDEX_SERVICE_API_KEY` and run **Release PageIndex**. Callers must send that value in `X-Api-Key`; configure `PageIndex__ApiKey` separately for C# callers because their release workflows do not provision it. For local use, save the value in `backend/PageIndex/.env` and restart the service or recreate the container. This service key is separate from `PAGEINDEX_AZURE_API_KEY`, which authenticates Azure OpenAI calls.

Generate keys once per environment and service, then reuse the saved values. When rotating a key, coordinate service and caller updates because requests fail while their keys differ.

### Retrieve the frontend origin and deployment token

After the infrastructure workflow succeeds, run the following in PowerShell while signed in to Azure CLI with the target subscription selected. Set `AZURE_RESOURCE_GROUP` to the deployed resource group and `DEPLOY_ENVIRONMENT` to the environment name in your shell's environment variables. Local PowerShell does not automatically inherit GitHub environment settings.

```powershell
$env:AZURE_RESOURCE_GROUP = '<your-resource-group>'
$env:DEPLOY_ENVIRONMENT = '<your-environment>'

foreach ($name in @('AZURE_RESOURCE_GROUP', 'DEPLOY_ENVIRONMENT')) {
  if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
    throw "Set the $name environment variable before running this script."
  }
}

$frontendOrigin = az deployment group show `
  --resource-group $env:AZURE_RESOURCE_GROUP `
  --name "infra-$env:DEPLOY_ENVIRONMENT" `
  --query properties.outputs.staticWebAppUrl.value `
  --output tsv

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($frontendOrigin)) {
  throw 'Could not find the deployed frontend origin.'
}

Write-Output "FRONTEND_ORIGIN=$frontendOrigin"

$appName = az deployment group show `
  --resource-group $env:AZURE_RESOURCE_GROUP `
  --name "infra-$env:DEPLOY_ENVIRONMENT" `
  --query properties.outputs.staticWebAppName.value `
  --output tsv

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($appName)) {
  throw 'Could not find the deployed Static Web App.'
}

az staticwebapp secrets list `
  --resource-group $env:AZURE_RESOURCE_GROUP `
  --name $appName `
  --query properties.apiKey `
  --output tsv
```

In **GitHub repository → Settings → Environments → your environment**, save the printed frontend URL as the **variable** `FRONTEND_ORIGIN` and the returned token as the **secret** `AZURE_STATIC_WEB_APPS_API_TOKEN`. If using a configured custom domain, use its HTTPS origin instead of the default URL. Register `<FRONTEND_ORIGIN>/auth-redirect.html` as an Entra SPA redirect URI. Repeat for each environment's Static Web App. Do not commit the token to the repository. See the [Azure CLI reference](https://learn.microsoft.com/en-us/cli/azure/staticwebapp/secrets?view=azure-cli-latest).

Run the infrastructure workflow to create the Static Web App for each environment, then store its deployment token as `AZURE_STATIC_WEB_APPS_API_TOKEN` in the matching GitHub environment. Set `FRONTEND_ORIGIN` to its default HTTPS origin or configured custom domain, and register `<FRONTEND_ORIGIN>/auth-redirect.html` as an Entra **Single-page application** redirect URI. The API uses this origin for CORS. Each release publishes to that Static Web App's production site, rather than creating a preview environment. The template outputs `staticWebAppName` and `staticWebAppUrl`. Static Web Apps uses the Free tier by default; all supplied environment JSON files set `staticWebAppLocation` to `eastasia`. Change `staticWebAppSku` or the location in those files as needed.

## GitHub Actions deployment

### Provision infrastructure

Run **Actions → Deploy infrastructure → Run workflow** first. `infra.yml` provisions `main.bicep`. Then run **Deploy Container Apps infrastructure**: [infra-container-apps.yml](../.github/workflows/infra-container-apps.yml) passes the `infra-<environment>` outputs to [ContainerApps/main.bicep](ContainerApps/main.bicep) as its `infrastructure` parameter and creates the Container Apps environment with the API, Background, MarkItDown, and PageIndex apps on `mcr.microsoft.com/k8se/quickstart:latest`, as deployment `container-apps-<environment>`. Resource names are unchanged from when `main.bicep` created them, so the first run adopts the existing environment and apps. API, MarkItDown, and PageIndex use port 80 and `/` readiness checks until released; Background has no ingress. Provisioning does not build images, run SQL migrations or publish a Foundry agent version.

Component releases read the saved `infra-<environment>` and `container-apps-<environment>` deployment outputs, merged by [deployment-outputs.ps1](../.github/scripts/deployment-outputs.ps1), and update only their deployment targets. They do not compile or redeploy either template; infrastructure changes belong in `infra.yml` and `infra-container-apps.yml`. API uses port 8080 and MarkItDown and PageIndex use port 8000 with `/health` probes. Image tags contain the commit SHA, run ID and attempt.

For a direct deployment:

```powershell
az deployment group create --resource-group YOUR_RESOURCE_GROUP `
  --template-file infra/main.bicep `
  --parameters infra/parameters.dev.json sqlEntraAdminObjectId=YOUR_PRINCIPAL_OBJECT_ID
```

Then deploy [ContainerApps/main.bicep](ContainerApps/main.bicep) with `infra/ContainerApps/parameters.dev.json`, `location`, and an `infrastructure` object mapping each `infra-dev` output name to its value.

### Preserve application images

Configure `API_IMAGE`, `BACKGROUND_IMAGE` and `MARKITDOWN_IMAGE` in the selected GitHub environment using the [GitHub settings table](#github-environment-settings). Set all three to existing application images, preferably immutable tags or digests in this environment's ACR. **Deploy Container Apps infrastructure** automatically enables application runtime settings, ports and health checks and reads the application secrets from the same GitHub environment. SQL migrations and runtime database grants must already exist; infrastructure does not run them. Partial image configuration is rejected.

Leave all three variables unset for the default hello images. Re-running **Deploy Container Apps infrastructure** with them unset resets ACA apps to hello images; **Deploy infrastructure** does not touch the apps. Component release workflows build and deploy new images independently and do not update these GitHub variables; set them to the desired release references before the next Container Apps infrastructure run. All deployment workflows share an environment concurrency group. AgentHost is published by Release AgentHost and is not controlled by these ACA image variables.
The Container Apps infrastructure workflow currently accepts image variables for API, Background, and MarkItDown only. It does not supply PageIndex's image or credentials. Run **Release PageIndex** after Container Apps infrastructure updates to restore its API image and settings; the three image variables above do not preserve PageIndex. For a direct Bicep deployment, see [PageIndex API](#pageindex-api).

### Release services

Use **Actions → Release services → Run workflow** to select `dev` or `test`, the branch or tag, and any combination of service checkboxes. All checkboxes start unchecked; select at least one. The workflow reuses the component releases at the same commit, preserving GitHub environment secrets and approval rules. Individual workflows also remain available.

Selected components run sequentially: Database migrations → MarkItDown → PageIndex → Ollaya → Dynamic Sessions → Sandboxes → AgentHost → API → Background → Frontend. Unselected components are skipped, while a failure or cancellation prevents later releases. Include required migrations when releasing dependent code. Review each selected job's result; completed deployments are not rolled back if a later component fails. The combined workflow holds the environment deployment lock for the entire batch, preventing standalone releases or infrastructure deployments from overlapping it.

| Workflow | Responsibility |
| --- | --- |
| [infra.yml](../.github/workflows/infra.yml) | Provision or update shared infrastructure; does not build application images |
| [infra-container-apps.yml](../.github/workflows/infra-container-apps.yml) | Deploy only `infra/ContainerApps/main.bicep`: the Container Apps environment and the API, Background, MarkItDown, and PageIndex apps, with application images and settings when the image variables are set |
| [release.yml](../.github/workflows/release.yml) | Release any selected combination of services in one workflow run |
| [release-db-migration.yml](../.github/workflows/release-db-migration.yml) | Restore packages, generate and apply idempotent SQL migrations |
| [release-api.yml](../.github/workflows/release-api.yml) | Build and deploy only API, configure its runtime settings and Foundry access, and check health |
| [release-background.yml](../.github/workflows/release-background.yml) | Build and deploy only Background and wait for its revision to become ready |
| [release-agent.yml](../.github/workflows/release-agent.yml) | Build AgentHost, update Foundry secrets, publish a hosted agent version, grant its identity Azure resource access, and route traffic |
| [release-markitdown.yml](../.github/workflows/release-markitdown.yml) | Build and deploy MarkItDown and check health |
| [release-pageindex.yml](../.github/workflows/release-pageindex.yml) | Build and deploy PageIndex and check health |
| [infra-ollaya.yml](../.github/workflows/infra-ollaya.yml) | Deploy only `infra/Ollaya/main.bicep`: Ollaya's own environment with a serverless T4 profile, its pull identity, and the Container App |
| [infra-dynamic-sessions.yml](../.github/workflows/infra-dynamic-sessions.yml) | Deploy only `infra/DynamicSessions/main.bicep`: the session pool in the existing Container Apps environment, its pull identity, and Session Executor access |
| [infra-sandboxes.yml](../.github/workflows/infra-sandboxes.yml) | Deploy only `infra/Sandboxes/main.bicep`: the sandbox group, its pull identity, and SandboxGroup Data Owner access |
| [release-ollaya.yml](../.github/workflows/release-ollaya.yml) | Build the Ollaya image with the model baked in, roll it out, and smoke-test a real decision |
| [release-dynamic-sessions.yml](../.github/workflows/release-dynamic-sessions.yml) | Build and smoke-test the Dynamic Sessions host image, update the session pool, and smoke-test a real session |
| [release-sandboxes.yml](../.github/workflows/release-sandboxes.yml) | Build, smoke-test, and push the Sandboxes host image |
| [release-frontend.yml](../.github/workflows/release-frontend.yml) | Read the saved API URL, build with `VITE_API_BASE_URL`, and publish to Azure Static Web Apps |

Rerunning one component release does not rebuild or redeploy another component. A frontend failure does not roll back the backend. SPA routes use `staticwebapp.config.json`; authentication continues through the application's Entra integration.

## SQL access

Runtime SQL users created with `WITH SID` use the managed identity client ID; Azure role assignments use its principal/object ID. API and Background never apply migrations. Runtime grants and identity repairs are manual.

### Grant the release identity SQL access

If SQL uses your personal Entra account as administrator, the GitHub OIDC identity does not automatically have database access. A `Login failed for user '<token-identified principal>'` error during Release commonly indicates this missing setup. Azure Owner/Contributor permissions do not grant SQL database access.

Connect to the deployed SQL server using SSMS or VS Code's MSSQL extension with Microsoft Entra authentication as the configured administrator. Select the application database (`sharepointagent` by default), not `master`. Allow your client IP through the SQL firewall if needed. Run the following once, replacing `github-action` if your deployment managed identity or service principal has another display name:

```sql
IF DATABASE_PRINCIPAL_ID(N'github-action') IS NULL
BEGIN
    CREATE USER [github-action] FROM EXTERNAL PROVIDER;
END;

IF IS_ROLEMEMBER(N'db_owner', N'github-action') = 0
BEGIN
    ALTER ROLE [db_owner] ADD MEMBER [github-action];
END;
```

Verify this is the identity whose client ID is configured as `AZURE_CLIENT_ID`. This grants database-scoped administration to the release identity so it can apply migrations. Runtime API, Background and AgentHost identities retain only reader/writer roles. Your account remains the server's Entra administrator. If a user with that name already exists, verify its identity before granting permissions. Repeat for each new database/server, including after changing the SQL server name, then retry Release. See [Microsoft's service-principal setup guide](https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-service-principal-tutorial?view=azuresql).

### Configure runtime SQL access manually

Release workflows do not create, repair or grant roles to runtime SQL users. Connect directly to the application database (`sharepointagent` by default) in SSMS or VS Code's MSSQL extension as the SQL Entra administrator. Allow your workstation through the SQL firewall if necessary.

`@UserName` is a database-local alias that you choose, not an Entra login name or an Azure resource name. Use the names in the table below consistently. `@ClientId` identifies the runtime managed identity; do not use the GitHub deployment identity's `AZURE_CLIENT_ID`, an identity's principal/object ID, or the SharePoint app registration's client ID.

| SQL user | Client ID to use |
| --- | --- |
| `sharepoint_api` | API managed identity client ID (`hosting.apiClientId` in infrastructure outputs) |
| `sharepoint_background` | Background managed identity client ID (`hosting.workerClientId`) |
| `sharepoint_agenthost` | Foundry agent's `instance_identity.client_id`, also printed by Release AgentHost |

Retrieve the values with Azure CLI in PowerShell. Set the environment variables first, sign in to the subscription containing your deployment, and read the saved infrastructure outputs. These commands only read configuration:

```powershell
$env:AZURE_RESOURCE_GROUP = '<your-resource-group>'
$env:DEPLOY_ENVIRONMENT = '<dev-or-test>'
$env:AZURE_SUBSCRIPTION_ID = '<your-subscription-id>'

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
az login
az account set --subscription $env:AZURE_SUBSCRIPTION_ID

$hosting = az deployment group show `
    --resource-group $env:AZURE_RESOURCE_GROUP `
    --name "infra-$env:DEPLOY_ENVIRONMENT" `
    --query properties.outputs.hosting.value --output json | ConvertFrom-Json

if (-not $hosting.apiIdentityId -or -not $hosting.workerIdentityId) {
    throw 'Run Deploy infrastructure first; runtime identity outputs are missing.'
}

# Read current client IDs from the managed identity resources.
$apiClientId = az identity show --ids $hosting.apiIdentityId --query clientId --output tsv
$backgroundClientId = az identity show --ids $hosting.workerIdentityId --query clientId --output tsv

Write-Host "SQL server: $($hosting.sqlServerFqdn)"
Write-Host "SQL database: $($hosting.sqlDatabaseName)"
@(
    [pscustomobject]@{ UserName = 'sharepoint_api'; ClientId = $apiClientId }
    [pscustomobject]@{ UserName = 'sharepoint_background'; ClientId = $backgroundClientId }
) | Format-Table -AutoSize
```

For AgentHost, run this next in the same PowerShell session, **after Release AgentHost has created the agent identity**. Your signed-in account needs permission to read the Foundry agent. A 404 means the agent does not exist yet; a 403 means your account lacks access.

```powershell
$agentClientId = az rest --method get `
    --url "$($hosting.foundryProjectEndpoint)/agents/sharepoint-agent?api-version=v1" `
    --resource https://ai.azure.com `
    --query instance_identity.client_id --output tsv

if ([string]::IsNullOrWhiteSpace($agentClientId)) {
    throw 'Foundry has not returned an agent client ID yet.'
}

[pscustomobject]@{
    UserName = 'sharepoint_agenthost'
    ClientId = $agentClientId
} | Format-Table -AutoSize
```

Copy one output row into the two declarations below, then execute the entire SQL block in the **application database printed above**, not `master`. Repeat for each row. For example, keep `@UserName = 'sharepoint_api'` and replace `<managed-identity-client-id>` with the client ID from that same row. Do not paste PowerShell expressions into SQL. The script creates missing users and adds reader/writer roles; rerunning it for a correct mapping leaves that user in place.

```sql
DECLARE @UserName sysname = 'sharepoint_api';
DECLARE @ClientId uniqueidentifier = '<managed-identity-client-id>';
DECLARE @Sid binary(16) = CONVERT(binary(16), @ClientId);
DECLARE @Sql nvarchar(max);

IF EXISTS (
    SELECT 1 FROM sys.database_principals
    WHERE name = @UserName AND (sid <> @Sid OR type <> 'E')
)
BEGIN
    THROW 50000, 'Existing user has a different identity. Review its permissions and ownership before replacing it.', 1;
END;

IF DATABASE_PRINCIPAL_ID(@UserName) IS NULL
BEGIN
    SET @Sql = N'CREATE USER ' + QUOTENAME(@UserName)
        + N' WITH SID = ' + CONVERT(varchar(34), @Sid, 1) + N', TYPE = E;';
    EXEC sys.sp_executesql @Sql;
END;

IF IS_ROLEMEMBER('db_datareader', @UserName) = 0
BEGIN
    SET @Sql = N'ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@UserName) + N';';
    EXEC sys.sp_executesql @Sql;
END;

IF IS_ROLEMEMBER('db_datawriter', @UserName) = 0
BEGIN
    SET @Sql = N'ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@UserName) + N';';
    EXEC sys.sp_executesql @Sql;
END;
```

Verify the resulting mappings in the same database:

```sql
SELECT
    name AS UserName,
    CONVERT(uniqueidentifier, sid) AS ClientId,
    IS_ROLEMEMBER('db_datareader', name) AS IsReader,
    IS_ROLEMEMBER('db_datawriter', name) AS IsWriter
FROM sys.database_principals
WHERE name IN ('sharepoint_api', 'sharepoint_background', 'sharepoint_agenthost');
```

The client IDs should match the CLI output, and both role columns should be `1`.

Use the **client ID**, not the principal/object ID, for explicit SQL SIDs. Existing users created with the earlier principal-ID mapping must be repaired manually after reviewing their grants and ownership. Configure AgentHost after Foundry creates its identity. Repeat when an identity or database is recreated.

### Migration behavior

SQL migrations still connect as the GitHub OIDC deployment identity. When the SQL administrator is another application, group or user, grant the deployment identity the required SQL migration permissions beforehand. Changing the administrator type does not grant that access automatically. Foundry permissions remain assigned to the deployment identity independently of the SQL administrator.

SQL migrations run as the deployment principal, while runtime identities receive only `db_datareader` and `db_datawriter`. The workflow temporarily allows its runner IP through the SQL firewall and removes that rule in an `always()` cleanup step. Credential-bearing parameter files live only in the runner temporary directory and are deleted during cleanup; they are not uploaded as artifacts. Keep migrations compatible with the previously deployed application while rolling out a new version. A failed deployment does not automatically roll back schema or infrastructure changes.

See [AgentHost configuration](../backend/SharePointAgent.AgentHost/README.md), [Foundry deployment](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent) and [agent identity/routing](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-agent). The frontend is hosted separately.

## Service configuration

### Document signing configuration mapping

Use the signing entries in [GitHub environment settings](#github-environment-settings). These settings apply to **API only**; Background, AgentHost, and Frontend do not need provider credentials.

**Deployment support:** run **Release API**, or select API in **Release services**, after configuring these GitHub settings. The workflow validates required values for enabled providers before building and deploys credentials through Container Apps secrets and secret references. Missing enable flags default to `false`, explicitly disabling those providers; DocuSign `Demo` defaults to `true`. Disabled providers need no credentials. Existing unused Container Apps secrets are retained. `main.bicep` does not configure signing; rerun Release API after infrastructure deployment to reapply these settings. No signing credentials are passed to Background, AgentHost, or Frontend.

Configure each provider independently; keep an unused provider disabled and omit its credentials. Use separate test and production credentials. Local user secrets use colons instead of double underscores, for example `DocumentSigning:AdobeSign:RefreshToken`. The old `Signing` section is no longer read. See [shared organization signing](../README.md#shared-organization-signing) for provider authorization and token renewal details.

For initial Adobe authorization without Postman, set `DOCUMENTSIGNING__ADOBESIGN__AUTHURL`, `DOCUMENTSIGNING__ADOBESIGN__OAUTHREDIRECTURI`, `DOCUMENTSIGNING__ADOBESIGN__CLIENTID`, and the `DOCUMENTSIGNING__ADOBESIGN__CLIENTSECRET` secret. Keep Adobe `ENABLED=false` until tokens are obtained. The API release forwards those four settings even with signing disabled; no refresh token or API origin is required for this setup mode. Release both API and Frontend, then open **Admin → Document signing** as a Global Admin and authorize the shared sender. Copy the displayed refresh token and regional API origin into the matching GitHub secret/variable, set Adobe `ENABLED=true`, and release API. The page does not save tokens or change configuration. Subsequent authorization follows the same flow; removing the callback variable disables the token-generation page's action on the next API release.

### PageIndex API

Infrastructure creates a separate PageIndex Container App with an ACR pull identity,
HTTPS ingress, 1 CPU / 2 GiB memory, 1–3 replicas, and a readiness probe. As with
MarkItDown, the default infrastructure deployment uses a placeholder image; run
**Release PageIndex** after **Deploy Container Apps infrastructure** to install the API. It is also
an optional selection in **Release Services**.

Configure the PageIndex entries in [GitHub environment settings](#github-environment-settings)
for each of `dev` and `test`; that table includes their container variable mappings.
The workflow supplies `AZURE_API_BASE` from the infrastructure output
`openAiEndpoint`, so no GitHub setting is needed for the endpoint.
It does not read the local `.env` file. See [PageIndex configuration](../backend/PageIndex/README.md#github-release-configuration)
for the distinction between GitHub settings and local runtime variables.

The release builds `backend/PageIndex/Dockerfile`, pushes the `pageindex` image,
stores keys as Container App secrets, and checks `/health` after deployment.
`/health` does not validate Azure model credentials; test `/index` with summaries
enabled to verify those settings.

Infrastructure outputs `pageIndexContainerAppName` and `pageIndexEndpoint`.
API, Background, and AgentHost releases configure `PageIndex__Endpoint` from
that output; Bicep also sets it for API and Background application images.
Run **Release API** after updating the release scripts to apply the endpoint
used by **Admin → Service health**. `/health` requires no API key.
For C# indexing calls, configure `PageIndex__ApiKey` separately with the same
service key; these releases do not provision that caller credential. The existing
agent indexing pipeline is not automatically switched to PageIndex.

For direct [ContainerApps/main.bicep](ContainerApps/main.bicep) application-image deployments, supply `pageIndexImage`, secure
`pageIndexServiceApiKey` and `pageIndexAzureApiKey`, and optionally
`pageIndexDeploymentName` / `pageIndexAzureApiVersion`. Do not commit keys in
parameter files.

The deployed worker timeout is 210 seconds to leave headroom below the default
[Container Apps HTTP ingress timeout](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview)
of 240 seconds. Upload and queue time also count toward ingress time; large
summary jobs can still exceed it. This API remains synchronous.

### Ollaya decision model

[Ollaya](../backend/Ollaya/README.md) serves the `winnow:e4b` decision model for the application to call, as MarkItDown and PageIndex are. The model needs about 9 GB of GPU memory, more than a Consumption replica's 8 GiB, so it runs on a serverless NVIDIA T4 GPU, in a Container Apps environment of its own.

**Its own environment.** [Ollaya/main.bicep](Ollaya/main.bicep) creates a workload profiles environment with the Consumption and `Consumption-GPU-NC8as-T4` profiles, in `ollayaLocation` from [Ollaya/parameters.&lt;environment&gt;.json](Ollaya/parameters.dev.json) (`eastus` in the supplied files). It reuses the registry and the Log Analytics workspace from [main.bicep](main.bicep), and leaves the shared environment untouched. That lets Ollaya follow GPU availability and quota to another region, and it adds no base cost: the environment has no dedicated profiles, so only a running GPU replica is billed. Pay-as-you-go and enterprise subscriptions have T4 quota by default; otherwise request **Managed Environment Consumption T4 GPUs** on the Ollaya environment's **Quota** page.

**Reaching it from another environment or region.** The apps call Ollaya over its public HTTPS endpoint with the API key, the same way AgentHost in Foundry reaches MarkItDown, so it does not matter which environment or region either side runs in. A different region adds a cross-region round trip to each decision and egress charges, and the decision states, which may contain document content, are processed in that region; check that against data-residency requirements. A distant region also slows cold starts, because each new replica pulls the image from the registry across regions.

**Changing region.** An environment cannot move, so the environment, app, and pull identity names include a suffix derived from the region. Changing `ollayaLocation` deploys a new set with a new endpoint; release the API, Background, and AgentHost afterwards to pick it up, then delete the old environment, app, and identity.

**Deploy and release.**

1. **Deploy Ollaya infrastructure** ([infra-ollaya.yml](../.github/workflows/infra-ollaya.yml)) deploys only [Ollaya/main.bicep](Ollaya/main.bicep), as deployment `ollaya-<environment>`, after **Deploy infrastructure** has created the registry and workspace. It creates the environment, an ACR pull identity, and the Container App: 8 vCPU, 56 GiB, and one T4 per replica, `minReplicas`–`maxReplicas` replicas (0–1 in the supplied files), external HTTPS ingress, and liveness and readiness probes on `GET /`. Like the other services, the app starts on the hello image unless `OLLAYA_IMAGE` is set. Outputs: `ollayaEnvironmentName`, `ollayaLocation`, `ollayaContainerAppName`, and `ollayaEndpoint`.
2. **Release Ollaya** ([release-ollaya.yml](../.github/workflows/release-ollaya.yml)) builds [backend/Ollaya/Dockerfile](../backend/Ollaya/Dockerfile) in ACR Tasks, which downloads the 8 GB model into the image and takes about eight minutes. The image is tagged `content-<hash>` from the files in `backend/Ollaya` and the model name, so a release with no Ollaya changes reuses the existing image and skips the build; run it with **rebuild** checked to pick up a model the Ollaya registry has republished under the same name. It then switches the app to the image, port 11435, the `OLLAYA_API_KEY` secret, and `/` probes. It waits for a replica to start, checks that a request without the key gets 401, and asks the model a real question. Set `OLLAYA_IMAGE` to the image in the release summary so the next infrastructure run keeps it. It is also a **Release services** checkbox.

**Calling it.** The API, Background, and AgentHost releases set `Ollaya__Endpoint` from the `ollaya-<environment>` outputs (blank until Ollaya is deployed) and `Ollaya__ApiKey` from `OLLAYA_API_KEY` when it is set; AgentHost receives the key through the Foundry `agent-secrets` connection, like MarkItDown's. Release those apps after the first Ollaya deployment to pick up the endpoint. Code calls it through `OllayaClient`, and **Admin → Service health** checks `GET /` without the key.

**Cost and latency.** With `minReplicas` 0 the GPU scales to zero and costs nothing while idle, but the first request after idling waits for a replica to start, pull the image (about 11 GB), and load the model, which can take several minutes. `Ollaya:TimeoutSeconds` is 300 for this reason. Set `minReplicas` to 1 in the Ollaya parameter file to keep it warm at the cost of a continuously running T4. A premium registry with artifact streaming, or geo-replication to the Ollaya region, shortens cold starts.

**Infrastructure redeploys.** Running **Deploy Container Apps infrastructure** with application images rewrites the API and Background environment variables from [ContainerApps/main.bicep](ContainerApps/main.bicep), which does not know the Ollaya endpoint; release those apps again afterwards, as for any setting that the releases own.

### Isolated code execution

The [sandbox host](../backend/SharePointAgent.SandboxHost/README.md) gives future agent tools a file system and a PowerShell, Python, Node.js, and Bash runner inside an isolated environment. One `sandboxhost` image is deployed to both targets below; they differ only in the settings each deployment passes. The API uses them as isolated agent workspaces when it runs the agent itself (`ChatAgent:Mode` `Local`): `agentWorkspaceMode` in `infra/ContainerApps/parameters.<environment>.json` (`Local` by default, `DynamicSessions`, or `Sandboxes`) becomes `AgentWorkspace__Mode`, and the API receives the pool endpoint, sandbox group, and disk image as `AgentWorkspace__DynamicSessions__*` and `AgentWorkspace__Sandboxes__*` settings. In Foundry mode the agent already runs in its own session sandbox.

Each target has its own template and workflow, like Ollaya, deployed after **Deploy infrastructure** has created the registry and API identity and **Deploy Container Apps infrastructure** has created the environment. [ContainerApps/main.bicep](ContainerApps/main.bicep) cannot know the pool endpoint, sandbox group, or disk image, because they exist only after these deployments. **Release API** sets `AgentWorkspace__DynamicSessions__PoolManagementEndpoint` and `AgentWorkspace__Sandboxes__SandboxGroup` from the `dynamic-sessions-<environment>` and `sandboxes-<environment>` deployment outputs, and `AgentWorkspace__Sandboxes__DiskImageId` from `SANDBOX_DISK_IMAGE_ID`, skipping any that are not available yet. Run **Release API** after deploying either target, and again after **Deploy Container Apps infrastructure** with application images, which rewrites the API environment variables without them. Resource names are unchanged from when [main.bicep](main.bicep) created them, so the first run of each workflow adopts an existing pool or group instead of recreating it.

**Dynamic Sessions.** **Deploy Dynamic Sessions infrastructure** ([infra-dynamic-sessions.yml](../.github/workflows/infra-dynamic-sessions.yml)) deploys [DynamicSessions/main.bicep](DynamicSessions/main.bicep) as deployment `dynamic-sessions-<environment>`. It creates a custom-container session pool in the existing Container Apps environment, in that environment's region. It has its own ACR pull identity, which is kept out of the sessions (`lifecycle: None`), so agent code cannot request its tokens. The pool runs 1 vCPU / 2 GiB per session, with `readySessions` warm sessions (default 1, billed while waiting), up to `maxSessions` (default 20), and a timed lifecycle that destroys a session `cooldownSeconds` (default 1800) after its last request. `egressEnabled` (default true) controls internet access from sessions. Set these in `infra/DynamicSessions/parameters.<environment>.json`. The API identity, and the identity that deploys the template, receive **Azure ContainerApps Session Executor** on the pool; the release smoke test uses the second. Outputs: `dynamicSessionsPoolName` and `dynamicSessionsPoolEndpoint`.

Like the other services, the pool starts on the hello image. Run **Release Dynamic Sessions** to install the host. The release builds the image, runs it on the runner, and checks all four languages plus a file round trip before pushing. It then switches the pool to the image, port 8080, `/health` liveness and startup probes, and the pool's settings (`Sandbox__RequireApiKey=false`, `Sandbox__MaxTimeoutSeconds=220`), and repeats the check through the pool endpoint in a fresh session, which it then stops. To keep the image across Dynamic Sessions infrastructure runs, set `SANDBOX_HOST_IMAGE` to the reference in the release summary.

**Sandboxes.** **Deploy Sandboxes infrastructure** ([infra-sandboxes.yml](../.github/workflows/infra-sandboxes.yml)) deploys [Sandboxes/main.bicep](Sandboxes/main.bicep) as deployment `sandboxes-<environment>`. It creates a `Microsoft.App/sandboxGroups` group in `sandboxGroupLocation` (set in `infra/Sandboxes/parameters.<environment>.json`), with a user-assigned identity that has ACR pull access. The API identity and the deploying identity receive **Container Apps SandboxGroup Data Owner** on the group. Confirm that Container Apps sandboxes are available in your subscription and region before running it. The template uses the documented `2026-02-01-preview` API, for which Bicep has no type information, so property errors appear only at deployment. Outputs: `sandboxGroupName`, `sandboxGroupId`, and `sandboxesPullIdentityId`.

Sandboxes are created at run time by callers, so **Release Sandboxes** does not update a running resource. It builds the image, applies the same local smoke test, and pushes `sandboxhost:<tag>` to ACR; when both releases run in one **Release services** run, the second reuses the image the first pushed. Then register that image as a disk image in the group: in the portal, open **Disk images**, select **Create**, enter the image as the base image URL, and choose managed-identity registry authentication with `sandboxesPullIdentityId`. The release does not automate this step, because the disk-image API and CLI are not yet documented for private registries. Set `SANDBOX_DISK_IMAGE_ID` to the registered disk image's resource ID, then run **Release API** to apply it as `AgentWorkspace__Sandboxes__DiskImageId`.

### Chat dictation

Set `deployTranscription=true` in the parameter file to deploy `gpt-4o-transcribe` (GlobalStandard, `transcriptionDeploymentCapacity`, default 10) on the Azure OpenAI account. It is `false` in the supplied files because the model is not offered in every region; check model availability for the account's region first. The deployment name is output as `transcriptionDeploymentName` and set as `AzureOpenAI__TranscriptionDeployment` on the API by provisioning and by **Release services**. When the flag is false the setting is empty and the chat microphone is hidden. The existing API identity role on the account covers transcription.

### Content Safety

`deployContentSafety=true` in the supplied parameter files provisions S0. The template configures API managed-identity access; AgentHost's role must be assigned when registering the hosted agent. `allowContentSafetyApiKeyAuth` controls local key authentication. Outputs include `contentSafetyEndpoint` and `contentSafetyResourceId`, never keys. Disabling the deployment flag does not delete a previously created resource in incremental deployment mode.

## Deployment behavior

### Health checks and common failures

Use **Admin → Service health** to inspect MarkItDown, PageIndex, and the Background heartbeat. A running Container App or successful infrastructure deployment does not confirm that the application is ready.

| Symptom | Check / recovery |
| --- | --- |
| Conversion returns 404, or health returns invalid JSON | Inspect the deployed image. If it is `mcr.microsoft.com/k8se/quickstart:latest`, run the corresponding component release. |
| PageIndex health reports it could not connect | Check API's `PageIndex__Endpoint`; it must use the deployed HTTPS endpoint, not `http://localhost:8001`. Release API to apply the endpoint from infrastructure outputs. |
| Service reports unhealthy after being stopped | Use the matching start workflow, then check application health and container logs. |
| PageIndex health succeeds but indexing with summaries fails | Verify its Azure OpenAI credentials, endpoint, API version, and model deployment. The health endpoint does not make model calls. |
| SQL login fails during release or at runtime | Check the appropriate [SQL identity and database grants](#sql-access); Azure role assignments alone do not grant SQL access. |

For AgentHost, inspect the hosted agent version and routing in Foundry, then test a chat request through the API. Use its trace ID to investigate failures; the Admin service-health checks do not currently probe AgentHost.

### Start or stop MarkItDown, PageIndex, and Ollaya

Open **Actions → Start or Stop MarkItDown** or **Actions → Start or Stop PageIndex**,
select **Run workflow**, choose `dev` or `test`, and choose `start` or `stop`.
Run infrastructure and the corresponding service release first.

These workflows use the existing Azure OIDC environment secrets and optional
`AZURE_RESOURCE_GROUP` variable in [GitHub environment settings](#github-environment-settings). They resolve the app from
`markItDownContainerAppName` or `pageIndexContainerAppName` in the
`infra-<environment>` deployment outputs. The shared
[control script](../.github/scripts/manage-container-app.ps1) verifies the container
name, skips an action when already in the requested state, and waits up to 15
minutes for completion. Both workflows share the environment deployment lock
with Background, infrastructure, and application releases.

Stopping MarkItDown makes conversions unavailable; stopping PageIndex makes
indexing requests unavailable. Starting reuses the deployed image and settings.
After starting, use **Admin → Service health** to check API connectivity and
container logs to investigate failures. The workflow confirms Azure running state,
not application readiness. No images are rebuilt by these workflows.

**Start or Stop Ollaya** ([manage-ollaya.yml](../.github/workflows/manage-ollaya.yml)) works the same way, using the same script, but reads `ollayaContainerAppName` from the `ollaya-<environment>` deployment. Run **Deploy Ollaya infrastructure** first.

With `minReplicas` 0, Ollaya already scales to zero when idle and bills no GPU time, but any request wakes it: a GPU replica then starts, about two minutes of pulling and loading, and stays up for the scale-down cooldown (300 seconds by default) after the last request. That includes the **Admin → Service health** check of `GET /`, so leaving that page on automatic refresh keeps a T4 running. Stopping the app removes its replicas and keeps requests from waking it, which makes it the way to guarantee no GPU charges; calls to Ollaya fail until it is started again. A stopped app still incurs its share of the registry (the Basic tier, plus storage for the 10 GB images) and of Log Analytics ingestion; the Ollaya environment itself has no standing charge, because it has no dedicated workload profiles.

### Start or stop Background

Open **Actions → Start or Stop Background → Run workflow**, select `dev` or
`test`, and choose `start` or `stop`. The [workflow](../.github/workflows/manage-background.yml)
resolves the Background Container App from the `container-apps-<environment>` deployment's
`workerContainerAppName` output. Run **Deploy Container Apps infrastructure** and **Release
Background** before using it to control the worker.

It uses the existing environment secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and
`AZURE_SUBSCRIPTION_ID` for OIDC login, plus the optional `AZURE_RESOURCE_GROUP`
variable (otherwise `rg-<workloadName>-<environment>`). The deployment identity needs
permission to read the deployment and Container App and perform
`Microsoft.App/containerApps/start/action` and `Microsoft.App/containerApps/stop/action`.
The existing resource-group Contributor role includes these operations.

The pipeline calls Azure's [Container App start/stop operations](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps?view=rest-resource-manager-containerapps-2025-01-01),
keeps the existing image and configuration, and waits up to 15 minutes for the
requested running status. An app already in that state succeeds without another
action. The workflow shares the environment deployment lock with releases.
Its summary reports the final Azure running status; check container logs to verify
that a started worker is processing successfully.

Stopping pauses all work hosted by Background, including synchronization and
subscription renewal. Pending queue messages remain subject to their expiry and
delivery policies. Use `start` to resume the deployed worker. This is an operational
control, not a persistent infrastructure setting; check the app state after later
deployments.

### Distributed tracing

Infrastructure provisions Application Insights using the environment's Log Analytics
workspace. Dev/test release scripts configure API, Background, and AgentHost with
the Azure Monitor exporter and the provisioned connection string. Redeploy infrastructure
before releasing these hosts after this change. Local Aspire runs use OTLP instead.
See [OpenTelemetry configuration](../docs/telemetry.md) for setup and trace-ID queries.

### Report current capacity

Run **Actions → Report Current Capacity → Run workflow**, selecting `dev` or
`test`. The [workflow](../.github/workflows/report-capacity.yml) reads the deployed
resources and publishes a table in the job summary:

- API, Background, MarkItDown, and PageIndex: app state, active revisions,
  CPU/memory per container per replica, and configured minimum/maximum replicas.
- AgentHost: explicitly routed versions, status, traffic percentage, and
  CPU/memory per session.

This is a read-only configuration snapshot, not CPU utilization or live replica
counts. It does not invoke AgentHost. It uses the existing OIDC secrets and
resource-group setting, and waits on the shared deployment lock. If a service
cannot be read, the summary retains the available results and the job fails with
an incomplete-report notice. The identity needs Azure resource read access and
Foundry agent/version read access.

### Configure capacity with GitHub Actions

Run either workflow from **Actions → Run workflow**. Both use the existing Azure
OIDC secrets, optional `AZURE_RESOURCE_GROUP`, and the `dev`/`test` environment
approval rules. They share the deployment lock with infrastructure, releases,
and start/stop workflows. No additional GitHub settings are required.

| Workflow | Inputs | Behavior |
| --- | --- | --- |
| [Configure Container App Capacity](../.github/workflows/configure-container-app-capacity.yml) | Environment, component (`Api`, `Background`, `MarkItDown`, `PageIndex`), CPU (`0.25`, `0.5`, `1`, `2`, `4`) | Sets memory to 2 GiB per vCPU, creates a revision using the existing image and settings, and waits for that revision to become ready. Replica limits stay unchanged. |
| [Configure AgentHost Capacity](../.github/workflows/configure-agent-capacity.yml) | Environment, CPU (`0.5`, `1`, `2`) | Sets memory to 1, 2, or 4 GiB per session. Copies the currently routed version's definition, changes its capacity, waits for the new version to become active, then routes 100% of traffic to it. |

Run infrastructure and the relevant component release first. Container Apps must
be running in single-revision mode with one container. AgentHost must have an
active hosted version with explicit 100% routing. The deployment identity needs
Container App update access or Foundry agent version/routing access, respectively.
Identical capacity requests make no changes. AgentHost failures before routing
leave the previous version selected; inspect the job summary for the created and
previous version numbers. No image builds, secret rotation, or SQL grants occur.

These workflows change deployed capacity, not repository defaults. Container App
releases preserve the allocation, but **Deploy Container Apps infrastructure** reapplies Bicep
capacity. **Release AgentHost** reapplies the allocation in `release-agent.ps1`.
Update those defaults too if the change should persist across those deployments.
To revert, rerun the capacity workflow with the previous CPU selection.

The available pairs follow the documented [Foundry sandbox sizes](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents#sandbox-sizes)
and [Container Apps workload profile limits](https://learn.microsoft.com/en-us/azure/container-apps/workload-profiles-overview).
Changes remain subject to environment quota and platform availability.

### Manually update Container Apps capacity

You can change CPU and memory without rebuilding the image or running the infrastructure/release workflows. Azure creates a new revision using the existing image and settings; this is not an in-place resize of a running container. These instructions cover API, Background, MarkItDown, and PageIndex. AgentHost is hosted in Foundry and requires a new agent version instead.

Use Azure CLI in PowerShell with permission to update the target Container App. Run the commands when no release or infrastructure deployment is in progress; manual CLI operations do not use the workflows' concurrency lock.

#### Select the app and inspect its current allocation

Sign in, select the intended subscription, and list the apps. Use your actual resource group if you overrode the default name:

```powershell
az login
az account set --subscription "<subscription-id>"
$resourceGroup = 'rg-sharepointagent-dev' # Use rg-sharepointagent-test for test.
az containerapp list --resource-group $resourceGroup --query "[].{name:name,state:properties.runningStatus}" --output table
```

Choose one app using this mapping. The CPU and memory values are the current repository targets, per replica:

| Service | App name contains | Container name | CPU | Memory |
| --- | --- | --- | --- | --- |
| API | `-api-` | `api` | `2` | `4Gi` |
| Background | `-wrk-` | `background` | `1` | `2Gi` |
| MarkItDown | `-md-` | `markitdown` | `0.5` | `1Gi` |
| PageIndex | `-pi-` | `pageindex` | `1` | `2Gi` |

For example, select API and record the existing allocation and image before changing it:

```powershell
$appName = '<full-api-container-app-name-from-the-list>'
$containerName = 'api'
az containerapp show --resource-group $resourceGroup --name $appName `
  --query "properties.template.{containers:containers[].{name:name,image:image,cpu:resources.cpu,memory:resources.memory},scale:scale}" `
  --output json
```

#### Update CPU and memory

This example applies API's 2 vCPU / 4 GiB allocation. To resize Background, MarkItDown, or PageIndex, change `$appName`, `$containerName`, and the resource values using the table above. Supply a CPU/memory combination supported by the app's workload profile.

```powershell
az containerapp update --resource-group $resourceGroup --name $appName `
  --container-name $containerName --cpu 2 --memory '4Gi' --output none
```

Omitting image, environment, secrets, and scale options preserves those settings. Replica limits remain unchanged: API, MarkItDown, and PageIndex use 1–3, while Background uses 1. The repository configures single-revision mode, so Azure moves traffic to the new revision when it is ready. Background restarts with the new allocation; allow ongoing work to complete before resizing where possible.

#### Verify the new revision

```powershell
az containerapp show --resource-group $resourceGroup --name $appName `
  --query "properties.{state:runningStatus,latest:latestRevisionName,ready:latestReadyRevisionName,containers:template.containers[].{name:name,cpu:resources.cpu,memory:resources.memory}}" `
  --output json

az containerapp revision list --resource-group $resourceGroup --name $appName `
  --query "[].{revision:name,active:properties.active,health:properties.healthState,replicas:properties.replicas}" `
  --output table
```

Confirm the intended resources are shown and the latest revision becomes ready and healthy. For API, MarkItDown, and PageIndex, also check their `/health` endpoint; for Background, check its logs and processing activity. To revert the allocation, run the update command again with the CPU and memory values recorded before the change; this creates another revision with the previous sizing.

Manual changes affect only the selected app and environment. Component release workflows preserve its resource allocation, but the next **Deploy Container Apps infrastructure** run reapplies `ContainerApps/main.bicep`. Keep that file and the [capacity table](#current-configured-capacity) aligned with any sizing you intend to retain. The API example already matches the repository's 2 vCPU / 4 GiB target.

Reference: [Azure CLI Container Apps update](https://learn.microsoft.com/en-us/cli/azure/containerapp#az-containerapp-update) and [Container Apps revisions](https://learn.microsoft.com/en-us/azure/container-apps/revisions).

## Infrastructure reference

### Current configured capacity

These are the repository's target allocations for both `dev` and `test`; existing Azure deployments keep their previous allocations until the corresponding deployment runs.

| Service | Configured resources | Scaling |
| --- | --- | --- |
| AgentHost, hosted in Foundry | 2 vCPU + 4 GiB per session | On-demand sessions; compute released after inactivity |
| API, Container Apps | 2 vCPU + 4 GiB per replica | 1–3 replicas |
| Background, Container Apps | 1 vCPU + 2 GiB per replica | 1 replica while running |
| MarkItDown, Container Apps | 0.5 vCPU + 1 GiB per replica | 1–3 replicas |
| PageIndex, Container Apps | 1 vCPU + 2 GiB per replica | 1–3 replicas |
| Ollaya, own Container Apps environment (serverless T4 GPU) | 8 vCPU + 56 GiB + 1 T4 per replica | 0–1 replicas; scales to zero when idle |
| Dynamic Sessions, session pool | 1 vCPU + 2 GiB per session | `readySessions` warm (default 1), up to `maxSessions` (default 20) |

AgentHost allocation is defined in [release-agent.ps1](../.github/scripts/release-agent.ps1). Container Apps allocations and replica limits are defined in [ContainerApps/main.bicep](ContainerApps/main.bicep). The four Container Apps together allocate at least 4.5 vCPU and 9 GiB per environment while running at their configured minimums; AgentHost sessions add capacity separately. API can allocate up to 6 vCPU and 12 GiB across three replicas.

To resize a deployed service using its current image, use the
[capacity workflows](#configure-capacity-with-github-actions). To apply capacity
defaults from the repository to an existing environment:

1. Run **Deploy Container Apps infrastructure** to update API to 2 vCPU and 4 GiB, or follow [manual Container Apps capacity updates](#manually-update-container-apps-capacity) in the operations section. For a Container Apps infrastructure deployment, set `API_IMAGE`, `BACKGROUND_IMAGE`, and `MARKITDOWN_IMAGE` to the current application images first, as described under [Preserve application images](#preserve-application-images), to preserve them. An API-only release preserves the existing resource allocation and does not apply Bicep changes.
2. Run **Release AgentHost**, or select **AgentHost** in **Release services**, to publish a version with 2 vCPU and 4 GiB per session.

### Runtime configuration and networking

Azure SQL uses Entra-only authentication and a Basic database (5 DTUs, maximum 2 GB) by default. Override `sqlDatabaseSku` with an S-series SKU when more capacity is needed. SQL permits authenticated connections from Azure services through the `0.0.0.0` firewall rule; this is Azure-wide, not subscription-only. Non-Azure deployment clients need their own firewall rule. Private networking is not configured.

API and Background have separate user-assigned identities selected through `AZURE_CLIENT_ID`; SQL connection strings use their client IDs. Foundry creates AgentHost's execution identity. MarkItDown and PageIndex identities have ACR image-pull access.

API listens on 8080, MarkItDown and PageIndex on 8000, and Background has no ingress. Background stays at one replica. MarkItDown uses external HTTPS so Foundry can reach it, and requires `X-Api-Key` before accepting upload bodies; `/health` remains public. Supply the same key as `MARKITDOWN_API_KEY` on the MarkItDown and `MarkItDown:ApiKey` on callers. Local MarkItDown instances with no key configured retain unauthenticated behavior.

### Resource naming

Main resource names start with `workloadName`, followed by the environment. ACR and Storage use the compact prefix without hyphens; for example, ACR uses `<workloadName><environment>cr<uniqueSuffix>`. Child resources retain their service-specific names. Changing a resource name creates a new resource rather than renaming an existing one; registries created with the previous `cr<workloadName>...` pattern and their images are not migrated automatically.

## Validation

Compile `main.bicep` and the templates in its subfolders with `az bicep build`. Compilation does not verify subscription quota, permissions, networking or Foundry regional availability. Application deployments use actual images and change runtime configuration; review them before applying to an existing environment.
