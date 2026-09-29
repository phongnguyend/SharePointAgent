# Azure infrastructure

The Bicep template provisions shared Azure resources and describe the application's deployment targets. Python deployment scripts and their dependencies have been removed.

All Azure resources are defined in `main.bicep`: shared services, SQL, runtime identities, Foundry, Container Apps, Static Web Apps, role assignments and the Foundry secret connection.

Main resource names start with `workloadName`, followed by the environment. ACR and Storage use the compact prefix without hyphens; for example, ACR uses `<workloadName><environment>cr<uniqueSuffix>`. Child resources retain their service-specific names. Changing a resource name creates a new resource rather than renaming an existing one; registries created with the previous `cr<workloadName>...` pattern and their images are not migrated automatically.

## GitHub Actions deployment

Each component has an independent, manually dispatched workflow. Select `dev` or `test` and the branch or tag to release.

| Workflow | Responsibility |
| --- | --- |
| [release-db-migration.yml](../.github/workflows/release-db-migration.yml) | Restore packages, generate and apply idempotent SQL migrations |
| [release-api.yml](../.github/workflows/release-api.yml) | Build and deploy only API, configure its runtime settings and Foundry access, and check health |
| [release-background.yml](../.github/workflows/release-background.yml) | Build and deploy only Background and wait for its revision to become ready |
| [release-agent.yml](../.github/workflows/release-agent.yml) | Build AgentHost, update Foundry secrets, publish a hosted agent version, grant its identity Azure resource access, and route traffic |
| [release-markitdown.yml](../.github/workflows/release-markitdown.yml) | Build and deploy MarkItDown and check health |
| [release-frontend.yml](../.github/workflows/release-frontend.yml) | Read the saved API URL, build with `VITE_API_BASE_URL`, and publish to Azure Static Web Apps |

For the first deployment, provision infrastructure, then run Database, MarkItDown, AgentHost, API, Background, and Frontend releases in that order. Wait for each to finish. For later updates, run only the affected workflows; apply required database migrations before releasing dependent application code. API and Background never apply migrations. Runtime SQL users created with `WITH SID` use the managed identity client ID; Azure role assignments use its principal/object ID. Runtime database grants and identity repairs are manual; use the SQL below. After Database release, configure API/Background SQL access before starting them. For the first AgentHost release, configure its SQL access once Foundry creates the identity; if that release fails before routing, rerun it after granting access. The combined `release.yml` has been removed.


Run the infrastructure workflow to create the Static Web App for each environment, then store its deployment token as `AZURE_STATIC_WEB_APPS_API_TOKEN` in the matching GitHub environment. Set `FRONTEND_ORIGIN` to its default HTTPS origin or configured custom domain, and register `<FRONTEND_ORIGIN>/auth-redirect.html` as an Entra **Single-page application** redirect URI. The API uses this origin for CORS. Each release publishes to that Static Web App's production site, rather than creating a preview environment. The template outputs `staticWebAppName` and `staticWebAppUrl`. Static Web Apps uses the Free tier by default; all supplied environment JSON files set `staticWebAppLocation` to `eastasia`. Change `staticWebAppSku` or the location in those files as needed.

Rerunning one component release does not rebuild or redeploy another component. A frontend failure does not roll back the backend. SPA routes use `staticwebapp.config.json`; authentication continues through the application's Entra integration.

Run **Actions → Deploy infrastructure → Run workflow** first. `infra.yml` provisions `main.bicep`, including API, Background and MarkItDown Container Apps with `mcr.microsoft.com/k8se/quickstart:latest`. The API and MarkItDown use port 80 and `/` readiness checks until released; Background has no ingress. Provisioning does not build images, run SQL migrations or publish a Foundry agent version.

Component releases read the saved `infra-<environment>` deployment outputs and update only their deployment targets. They do not compile or redeploy `main.bicep`; shared infrastructure changes belong in `infra.yml`. API uses port 8080 and MarkItDown uses port 8000 with `/health` probes. Image tags contain the commit SHA, run ID and attempt.

Configure `API_IMAGE`, `BACKGROUND_IMAGE` and `MARKITDOWN_IMAGE` in the selected GitHub environment using the settings table below. Set all three to existing application images, preferably immutable tags or digests in this environment's ACR. Infrastructure automatically enables application runtime settings, ports and health checks and reads the application secrets from the same GitHub environment. SQL migrations and runtime database grants must already exist; infrastructure does not run them. Partial image configuration is rejected.

Leave all three variables unset for the default hello images. Re-running infrastructure with them unset resets ACA apps to hello images. Component release workflows build and deploy new images independently and does not update these GitHub variables; set them to the desired release references before the next infrastructure run. All deployment workflows share an environment concurrency group. AgentHost is published by Release AgentHost and is not controlled by these ACA image variables.

Create matching GitHub environments with these settings:

| Name | Kind | Purpose |
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
| `FRONTEND_ORIGIN` | Variable | Frontend HTTPS origin for CORS; configure its Entra redirect URI separately |
| `MARKITDOWN_API_KEY` | Secret | Shared MarkItDown authentication key, at least 32 characters |
| `MARKITDOWN_IMAGE` | Optional variable | Existing MarkItDown image, e.g. `YOUR_REGISTRY.azurecr.io/markitdown:EXISTING_TAG`; set all three image variables together |
| `SHAREPOINT_CLIENT_ID` | Variable | Existing SharePoint/Entra application client ID in the SharePoint tenant |
| `SHAREPOINT_CLIENT_SECRET` | Secret | Graph application credential |
| `SHAREPOINT_CLIENT_STATE` | Secret | Webhook validation secret, at least 16 characters |
| `SHAREPOINT_DOCUMENT_LIBRARY_NAME` | Variable | Name of the source SharePoint document library |
| `SHAREPOINT_SITE_HOSTNAME` | Variable | Hostname of the source SharePoint site |
| `SHAREPOINT_SITE_PATH` | Variable | Path of the source SharePoint site |
| `SHAREPOINT_TENANT_ID` | Variable | Required for application deployments: Entra tenant ID for SharePoint Graph access and application sign-in; can differ from `AZURE_TENANT_ID` and has no fallback |
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

## Retrieve the frontend origin and deployment token

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

## Generate the MarkItDown API key

Run this PowerShell script to generate a cryptographically random 32-byte key encoded as Base64:

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

Save the output as the **secret** `MARKITDOWN_API_KEY` under **GitHub repository → Settings → Environments → your environment → Environment secrets**. Generate a separate key for each environment. The deployment supplies the same key to MarkItDown and its callers. Do not commit the key to the repository. If you replace it, run the MarkItDown, API, Background and AgentHost workflows to update the service and callers. Coordinate these runs because requests fail while their keys differ.

## Deployment behavior

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

### Release sequence

SQL migrations still connect as the GitHub OIDC deployment identity. When the SQL administrator is another application, group or user, grant the deployment identity the required SQL migration permissions beforehand. Changing the administrator type does not grant that access automatically. Foundry permissions remain assigned to the deployment identity independently of the SQL administrator.

Provision infrastructure before the first release, then follow the component deployment order above. All workflows share an environment concurrency group to prevent overlapping infrastructure and application changes.

SQL migrations run as the deployment principal, while runtime identities receive only `db_datareader` and `db_datawriter`. The workflow temporarily allows its runner IP through the SQL firewall and removes that rule in an `always()` cleanup step. Credential-bearing parameter files live only in the runner temporary directory and are deleted during cleanup; they are not uploaded as artifacts. Keep migrations compatible with the previously deployed application while rolling out a new version. A failed deployment does not automatically roll back schema or infrastructure changes.

See [AgentHost configuration](../backend/SharePointAgent.AgentHost/README.md), [Foundry deployment](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent) and [agent identity/routing](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-agent). The frontend is hosted separately.

## Runtime configuration and networking

Azure SQL uses Entra-only authentication and a Basic database (5 DTUs, maximum 2 GB) by default. Override `sqlDatabaseSku` with an S-series SKU when more capacity is needed. SQL permits authenticated connections from Azure services through the `0.0.0.0` firewall rule; this is Azure-wide, not subscription-only. Non-Azure deployment clients need their own firewall rule. Private networking is not configured.

API and Background have separate user-assigned identities selected through `AZURE_CLIENT_ID`; SQL connection strings use their client IDs. Foundry creates AgentHost's execution identity. MarkItDown has only ACR image-pull access.

API listens on 8080, MarkItDown on 8000, and Background has no ingress. Background stays at one replica. MarkItDown uses external HTTPS so Foundry can reach it, and requires `X-Api-Key` before accepting upload bodies; `/health` remains public. Supply the same key as `MARKITDOWN_API_KEY` on the MarkItDown and `MarkItDown:ApiKey` on callers. Local MarkItDown instances with no key configured retain unauthenticated behavior.

## Content Safety

`deployContentSafety=true` in the supplied parameter files provisions S0. The template configures API managed-identity access; AgentHost's role must be assigned when registering the hosted agent. `allowContentSafetyApiKeyAuth` controls local key authentication. Outputs include `contentSafetyEndpoint` and `contentSafetyResourceId`, never keys. Disabling the deployment flag does not delete a previously created resource in incremental deployment mode.

## Validation

Compile `main.bicep` with `az bicep build`. Compilation does not verify subscription quota, permissions, networking or Foundry regional availability. Application deployments use actual images and change runtime configuration; review them before applying to an existing environment.
