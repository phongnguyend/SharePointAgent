# Manual conversion demo

Run from the repository root:

```powershell
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo -- --input "C:\Documents\sample.docx"
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo -- --input "C:\Documents\sample.xlsx" --output ".artifacts/sample.md" --skip-images
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo -- --help
```

The default output is a UTF-8 Markdown file beside the input, with the extension changed to
`.md`. Existing output is preserved unless you pass `--overwrite`. Warnings go to stderr.
Exit codes: 0 success (possibly with partial-conversion warnings), 1 failure, 130 cancellation.
Press Ctrl+C to cancel.

## Configuration and IDE launch

Settings load in this order; later values override earlier ones:

1. `appsettings.json`, copied beside the executable during build/publish.
2. The project's .NET user-secrets `secrets.json` (loaded in every environment for this demo).
3. Environment variables prefixed with `DOCUMENTPARSERS_`, using `__` for nested keys.
4. Command-line options.

Set `Demo:InputPath` and optionally `Demo:OutputPath` in appsettings or user secrets to run
without arguments from your IDE. Relative input/output paths use the current working directory,
not the directory containing appsettings. Configure parser limits in the `Parser` section.

```powershell
$env:DOCUMENTPARSERS_Demo__InputPath = "C:\Documents\sample.docx"
$env:DOCUMENTPARSERS_Demo__SkipImages = "true"
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo
```

Local DOCX/PPTX/XLSX conversion needs no credentials. PDF parsing needs Document Intelligence.
Image descriptions are off by default; `--analyze-images` enables the configured vision
deployment. `--ocr` adds OCR and requires image analysis. `--skip-images` takes precedence
over both, omitting image output and enrichment while leaving PDF layout analysis enabled.

## User secrets

The project already declares a `UserSecretsId`; no init command is needed. In Visual Studio,
right-click the Demo project and choose **Manage User Secrets**, or use:

```powershell
$project = "backend/DocumentParsers/DocumentParsers.Demo"
dotnet user-secrets set "DocumentIntelligence:Endpoint" "https://YOUR-RESOURCE.cognitiveservices.azure.com/" --project $project
dotnet user-secrets set "DocumentIntelligence:ApiKey" "YOUR-KEY" --project $project
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/" --project $project
dotnet user-secrets set "AzureOpenAI:ApiKey" "YOUR-KEY" --project $project
dotnet user-secrets set "AzureOpenAI:Deployment" "YOUR-VISION-DEPLOYMENT" --project $project
```

Equivalent contents for the user-secrets file:

```json
{
  "DocumentIntelligence": {
    "Endpoint": "https://YOUR-RESOURCE.cognitiveservices.azure.com/",
    "ApiKey": "YOUR-KEY"
  },
  "AzureOpenAI": {
    "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
    "ApiKey": "YOUR-KEY",
    "Deployment": "YOUR-VISION-DEPLOYMENT"
  }
}
```

This file belongs in the .NET user-secrets store, not the repository. On Windows its location is
`%APPDATA%\Microsoft\UserSecrets\DocumentParsers.Demo-8f17d847-e24a-463f-8c89-afbbfd7c802f\secrets.json`.
User secrets are a local development store, not encrypted storage.
[Microsoft's user-secrets documentation](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
describes the platform-specific locations and tooling.

Image analysis currently constructs the shared `ImageAnalysisService`, so configure both
Document Intelligence and Azure OpenAI when enabling it. Use a deployment that accepts image
inputs; see [vision documentation](https://developers.openai.com/api/docs/guides/images-vision).

```powershell
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo -- --input "C:\Documents\sample.pdf" --analyze-images --ocr
```

PDF/image analysis sends document content to the configured Azure services and incurs their
normal usage charges. The demo never prints API keys.
