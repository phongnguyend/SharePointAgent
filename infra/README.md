# Infrastructure deployment

Deploy `main.bicep` with one of these Azure Resource Manager parameter files:

| File | Resource prefix | Purpose |
| --- | --- | --- |
| `parameters.dev.json` | `sharepointagent-dev` | Shared development environment |
| `parameters.test.json` | `sharepointagent-test` | Test environment |
| `parameters.local.json` | `sharepointagent-local` | Azure resources used by locally running applications |

Each file sets `workloadName` and `environmentName`, Basic Azure AI Search, an embedding deployment with capacity 10, and key authentication enabled. Document Intelligence is disabled by default. Resource location is configured in each JSON file through `parameters.location.value` (initially `eastus`). Resource group location is configured separately through the required GitHub environment variable `AZURE_RESOURCE_GROUP_LOCATION`. The two locations can differ. Confirm model availability and quota in the resource region. For an existing resource group, use its current group location; changing the variable does not move the group.

To deploy through GitHub Actions, use [Deploy infrastructure](../.github/workflows/infra.yml) from the Actions tab and select `local`, `dev`, or `test`. The workflow is manual only and uses the corresponding parameter file. Runs for the same environment are serialized without cancelling an active deployment.

Create GitHub environments named `local`, `dev`, and `test`, then configure each:

| Setting | Kind | Purpose |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | Secret | Entra application or user-assigned identity client ID |
| `AZURE_TENANT_ID` | Secret | Azure tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Secret | Target subscription ID |
| `AZURE_RESOURCE_GROUP` | Variable, optional | Defaults to `rg-<workloadName>-<environmentName>` |
| `AZURE_RESOURCE_GROUP_LOCATION` | Variable, required | Resource group metadata region, independent of resource location in JSON |

Configure an Azure federated identity credential for each GitHub environment with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and subject `repo:<owner>/<repository>:environment:<environment>`. The workflow uses [Azure Login with OIDC](https://github.com/Azure/login#login-with-openid-connect-oidc-recommended); no client secret is needed. Grant the identity permission to create the target resource group and deploy its resources (for example, Contributor on the target subscription). If the group is provisioned separately, scope deployment access to that group. Bootstrapping Container Apps additionally requires permission to create role assignments, such as Role Based Access Control Administrator scoped to that resource group.

The workflow compiles both templates, creates/tags the resource group, validates and previews the shared deployment, then deploys it in incremental mode. The what-if output is logged and deployment continues automatically. Enable `deploy_container_apps` only to bootstrap hosted dev/test environments: it deploys placeholder images and can overwrite existing application images. Leave it disabled for routine shared-infrastructure updates. Local rejects this option. SQL Server, MarkItDown, chat model deployment, and application image releases remain separate.

For a CLI deployment, run from the repository root with Azure CLI and Bicep installed and an authenticated Azure subscription:

```powershell
$environment = 'local' # dev, test, or local
$resourceGroup = "rg-sharepointagent-$environment"
$parametersFile = "infra/parameters.$environment.json"
$settings = (Get-Content $parametersFile -Raw | ConvertFrom-Json).parameters
$env:AZURE_RESOURCE_GROUP_LOCATION = 'eastus' # Resource group metadata region

az group create --name $resourceGroup --location $env:AZURE_RESOURCE_GROUP_LOCATION

az deployment group what-if `
  --resource-group $resourceGroup `
  --template-file infra/main.bicep `
  --parameters $parametersFile

$deployment = az deployment group create `
  --resource-group $resourceGroup `
  --template-file infra/main.bicep `
  --parameters $parametersFile | ConvertFrom-Json

$deployment.properties.outputs
```

Use a separate resource group for each environment. For a personal local environment, choose your own resource group to isolate its resources. Parameter values can be overridden after the file, for example `--parameters $parametersFile workloadName=spalice environmentName=local location=eastus`. Use lowercase letters and digits: `workloadName` must be 2-15 characters and `environmentName` 2-5 characters. Both templates derive the prefix as `<workloadName>-<environmentName>` and must receive the same values. Storage and registry names omit the separator; storage uses a four-character uniqueness suffix to fit its 24-character name limit. The worker Container App uses the `wrk` resource label to fit its 32-character name limit.

`local` provisions real Azure resources, not emulators. The current shared template also creates Azure Container Registry, Log Analytics, and a Container Apps environment. For local application hosting, skip `container-apps.bicep`. SQL Server, MarkItDown, and a chat model deployment are not created by this template; configure those separately. Copy the deployed endpoints and embedding deployment name into your local application configuration and keep credentials in user secrets or ignored `appsettings.Local.json` files. Deployment parameter files contain no credentials and are intended to be committed.

For hosted dev/test environments, create the Container App shells after the shared deployment. `container-apps.bicep` accepts only a subset of the shared template's parameters, so pass the matching values explicitly:

```powershell
$settings = (Get-Content $parametersFile -Raw | ConvertFrom-Json).parameters
$tagsJson = ConvertTo-Json -InputObject $settings.tags.value -Compress

az deployment group create `
  --resource-group $resourceGroup `
  --template-file infra/container-apps.bicep `
  --parameters `
    workloadName=$($settings.workloadName.value) `
    environmentName=$($settings.environmentName.value) `
    location=$($settings.location.value) `
    tags=$tagsJson `
    deployDocumentIntelligence=$($settings.deployDocumentIntelligence.value.ToString().ToLowerInvariant())
```

If you override the workload, environment, location, or Document Intelligence setting in the shared deployment, pass the same overrides to the Container Apps deployment. Do not routinely redeploy the shells after releasing application images: this template declares placeholder images. See the [root README](../README.md#prerequisites) for image builds, application configuration, and release commands.

All taggable resources created by both templates receive `workload`, `environment`, and `managedBy=Bicep` tags, merged with the custom `tags` parameter (the files also set `application=SharePointAgent`). The derived identification tags take precedence over custom tags so they stay consistent with deployment inputs. Child resources that do not support tags are identified through their parent resource.

Changing the naming inputs or migrating from the previous `namePrefix` scheme creates resources under new names; it does not rename existing resources. Review `what-if` before deploying to an existing resource group.
