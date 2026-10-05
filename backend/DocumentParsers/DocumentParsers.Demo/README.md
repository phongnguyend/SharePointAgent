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
not the directory containing appsettings. Configure parser limits in the `PdfParser`,
`DocxParser`, `PptxParser`, or `XlsxParser` section. Each binds to that format's
options type. Migrate settings from the former `Parser` section to the corresponding
format sections; the old section is no longer read.

```powershell
$env:DOCUMENTPARSERS_Demo__InputPath = "C:\Documents\sample.docx"
$env:DOCUMENTPARSERS_Demo__SkipImages = "true"
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo
```

Local PDF/DOCX/PPTX/XLSX conversion needs no credentials. PDF text/layout uses PdfPig.
Use `--pdf-ocr` (or `Demo:PdfOcr=true`) to enable Document Intelligence OCR for PDF pages
without native text. This option needs only Document Intelligence credentials, not Azure OpenAI.
Image descriptions are off by default; `--analyze-images` enables the configured vision
deployment. `--ocr` adds OCR and requires image analysis. `--skip-images` takes precedence
over both, omitting visible image content and enrichment while retaining an
`<!-- image: filename.png; caption: Sales overview; alt: Sales chart -->` comment
per image, omitting empty caption/alt fields. It does not disable explicit `--pdf-ocr`.

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

Enabling PDF OCR or image analysis sends document content to the configured Azure services and incurs their
normal usage charges. The demo never prints API keys.

```powershell
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo -- --input "C:\Documents\scan.pdf" --pdf-ocr --skip-images
```

PDF OCR uploads the PDF and requests only pages without native text. Without `--pdf-ocr`,
those pages produce a warning. Local layout analysis detects headings by font size, normalizes
list markers, detects aligned numeric tables, and reads each column top-to-bottom before
moving left-to-right between columns, separating sections around spanning content. Ambiguous
headings/tables remain text; complex layouts and rotated text may require further rules.

PDF reading order defaults to `LayoutAware`. For forms or side-by-side comparisons that
should read across rows, set `PdfParser:PdfReadingOrder` to `RowBased` in appsettings or
user secrets (or `DOCUMENTPARSERS_PdfParser__PdfReadingOrder=RowBased`). This applies to
native PDF text and OCR results. Other document formats are unaffected.
