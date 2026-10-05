# DocumentParsers

Standalone .NET 10 libraries for PDF, DOCX, PPTX and XLSX extraction and Markdown conversion.
The solution, format libraries, shared library and their test projects live in this directory:

```text
DocumentParsers/
  DocumentParsers.slnx
  DocumentParsers.Demo/DocumentParsers.Demo.csproj
  DocumentParsers.Shared/DocumentParsers.Shared.csproj
  DocumentParsers.Shared.Tests/DocumentParsers.Shared.Tests.csproj
  DocumentParsers.Pdf/DocumentParsers.Pdf.csproj
  DocumentParsers.Pdf.Tests/DocumentParsers.Pdf.Tests.csproj
  DocumentParsers.Docx/DocumentParsers.Docx.csproj
  DocumentParsers.Docx.Tests/DocumentParsers.Docx.Tests.csproj
  DocumentParsers.Pptx/DocumentParsers.Pptx.csproj
  DocumentParsers.Pptx.Tests/DocumentParsers.Pptx.Tests.csproj
  DocumentParsers.Xlsx/DocumentParsers.Xlsx.csproj
  DocumentParsers.Xlsx.Tests/DocumentParsers.Xlsx.Tests.csproj
  document-parsers-spec.md
```

From the repository root:

```powershell
dotnet build backend/DocumentParsers/DocumentParsers.slnx
dotnet test backend/DocumentParsers/DocumentParsers.slnx
```

## Usage

For manual conversion, run the [console demo](DocumentParsers.Demo/README.md):

```powershell
dotnet run --project backend/DocumentParsers/DocumentParsers.Demo -- --input "sample.docx" --skip-images
```

The demo supports `appsettings.json`, .NET user secrets, environment variables and CLI overrides.

Reference the format project(s) your application needs. Each format library references
`DocumentParsers.Shared`; format libraries do not reference each other. Public namespaces
remain `DocumentParsers`, so existing using directives and method calls remain valid.

Shared owns the parser contracts, result/element models, limits, input/image reading helpers,
Markdown helpers, image analysis, format resolution and conversion orchestration. Dispatch
depends only on parser interfaces, so Shared has no dependency on the format libraries.
The PPTX reading-order resolver lives in the PPTX project; cell-reference utilities live in XLSX.
SDK packages used by shared helpers/services are referenced by Shared and flow transitively.

Each test project references only its corresponding library. Shared tests cover image services
and dispatch with substituted parser interfaces; format tests cover extraction, Markdown and
input handling. For example, run only the DOCX suite with:

```powershell
dotnet test backend/DocumentParsers/DocumentParsers.Docx.Tests/DocumentParsers.Docx.Tests.csproj
```

Each format has its own interface, result type and conversion method. There is no shared parsed-document wrapper.

```csharp
using DocumentParsers;

var parser = new DocxDocumentParser();
await using var stream = File.OpenRead("example.docx");
DocxParseResult result = await parser.ParseAsync(stream, cancellationToken);
string markdown = parser.ConvertToMarkdown(result, cancellationToken);
```

The caller owns the input stream; parsing starts at its current position. Non-seekable streams are supported.
Markdown conversion uses the result in memory and makes no service calls.
To omit image blocks (including captions, descriptions and OCR text), use
`parser.ConvertToMarkdown(result, skipImages: true)`. Each image still emits
`<!-- image: abc.png; caption: Sales overview; alt: Quarterly sales chart -->`
at its position. Existing caption/alt text is retained in the comment; empty fields
are omitted. Comments use the embedded filename
when available, otherwise `image-<SHA256>.<extension>`. These are identifiers;
conversion does not write image files. Images are included
by default, and the parsed result is never modified by this option.
`DocumentConversionService.ConvertAsync(..., skipImages: true)` also skips
LLM/OCR enrichment; parsing still extracts images for reuse.
`new PdfDocumentParser()` extracts PDF text, layout and embedded images locally using PdfPig.
Pass an authenticated `DocumentIntelligenceClient` to enable `prebuilt-read` OCR fallback
for pages without native text. Native text pages never call Azure.
DOCX, PPTX and XLSX parsing runs locally with Open XML SDK.

## Image analysis and dispatch

`ImageAnalysisService` takes a vision-capable `Microsoft.Extensions.AI.IChatClient`, an authenticated
Document Intelligence client, and an optional vision model ID. `DescribeAsync` sends the image and
caption context to the LLM. `ExtractTextAsync` uses Azure `prebuilt-read` and preserves line breaks.
The host supplies provider clients, credentials, timeouts and retry policies; this library does not own their lifetime.

```csharp
var analysis = new ImageAnalysisService(visionClient, documentIntelligenceClient, visionModelId);
var processor = new ImageDescriptionProcessor(analysis);
var warnings = await processor.ProcessAsync(
    result.BodyElements.OfType<ImageElement>(),
    new ImageProcessingOptions
    {
        ExtractText = true,
        ConfigurationKey = "provider/model/prompt-v1",
        MaxConcurrency = 4
    },
    cancellationToken);
result.Warnings.AddRange(warnings);
string enrichedMarkdown = parser.ConvertToMarkdown(result, cancellationToken);
```

The processor deduplicates requests within a document using SHA-256, operation, MIME type,
configuration and description context. It retains successful description/OCR output when the other
operation fails and returns warnings without logging source content. Cancellation propagates.
No cross-document output cache is retained.

`DocumentConversionService` dispatches using MIME type plus extension validation and can run the
image processor before conversion. Its typed result overloads let callers retain the original result.
`MarkdownConversionResult` contains only the output Markdown and copies of metadata/warnings.
Omitting the optional processor produces Markdown using existing image captions/alt text/enrichment.

## Current behavior and limits

- PDF: PdfPig word extraction followed by `LayoutAnalyzer` coordinate normalization,
  word-to-line grouping, font-size headings, Markdown lists, paragraph merging and conservative
  aligned numeric table detection. Text, tables and images read top-to-bottom, then
  left-to-right within each row, with a two-point tolerance for top-edge alignment.
  Page attribution and bounding boxes use points measured from the top-left. Classification
  targets horizontal left-to-right text; same-size headings, nested list indentation,
  text-only/merged tables and overlapping layouts remain limitations. Vector figures are not
  rendered. Optional Azure OCR selects pages
  without native words (including blank pages); pages mixing native text and scanned images
  need separate image OCR. The OCR request uploads the whole PDF with a page selection.
- DOCX: body order, custom/inherited heading styles, bold text from run/paragraph/character
  styles (including explicit bold-off), nested bullets and numbered lists resolved from
  numbering definitions, list instances and start overrides. Bold is emitted as `**text**`;
  adjacent bold runs are merged. List glyphs normalize to `-` and number formats to decimal
  Markdown markers; compound outline labels and numbering-style links are not fully supported.
  Preserves text around inline images, tables and image
  extraction. Nested tables are flattened; cell images follow the table and produce a warning.
  Headers/footers, notes, chart rendering and full text-box support remain follow-up work.
  Hyperlink text is retained but links are not followed or rendered as links.
- PPTX: presentation slide order, empty slides, text, titles, images, tables and nested groups.
  Placeholder positions resolve from slide overrides, layout placeholders and master
  placeholders before sorting top-to-bottom then left-to-right. Unknown positions sort
  after known positions in source order instead of being treated as the slide origin.
  Text shapes preserve numbered lists and nested bullets, including paragraph overrides and
  list styles inherited from matching layout/master placeholders and presentation defaults.
  Number sequences continue across nested sub-items; nested counters reset for a new parent.
  Markdown normalizes numbering styles to decimal and bullet glyphs to hyphens; font styling
  and exact alphabetic/Roman numeral appearances are not retained.
  Group translation/scaling is applied; rotation/flipping and overlap remain approximate.
  `IReadingOrderResolver` is replaceable. Markdown uses `<!-- Slide number: 3 -->`
  comments instead of slide headings. Speaker notes follow a `### Notes:` heading and a blank line;
  empty notes and notes-page slide-number/date/footer placeholders are omitted.
  SmartArt/charts produce warnings; inherited master/layout text content is not included
  (list formatting is resolved).
- XLSX: shared/inline strings, booleans, numeric and cached formula values, formulas, sparse cells,
  common built-in date formats (including the 1904 epoch), worksheets and drawing image anchors.
  No formulas are evaluated. Custom/time/duration formats retain their stored values with warnings;
  percentages/currency are not formatted. Merged cells retain stored coordinates with warnings.
  Hidden content is included. Charts, pivots, named ranges and comments are not rendered.
  Markdown tables are bounded to the configured row count and at most 50 populated columns.
  Rows with only missing, empty or whitespace values and no formulas are omitted before
  forming regions. Original row numbers, formula rows, zero/false values and image anchors
  are retained, and the parsed worksheet data is unchanged.
  Unused columns are excluded, and rows are checked again within each column group so
  wide worksheets cannot produce blank rows in split tables. Formulas are shown when
  their cached values are blank, so retained formula rows have visible content.
  Images follow their corresponding row region with their cell reference retained.
- Input bytes, expanded ZIP bytes/entry count, table cells, extracted image count and total image
  bytes have configurable limits in `ParserOptions`. ZIP contents are validated before SDK parsing.
  Image resizing and decorative-image detection are not implemented yet; the image provider may
  reject unsupported or oversized images, resulting in per-operation warnings.

This is an initial implementation, not completion of every production requirement in the spec.
Tests build Open XML and PDF fixtures in memory and substitute external clients for OCR/LLM checks.
They make no paid service calls. Live Azure/vision validation and a representative real-world
document corpus are still needed before production rollout.

## SDK references

Dependencies are pinned in the Shared project. The figure download implementation targets the
installed Azure Document Intelligence 1.0.0 API rather than assuming the latest method signatures.
See the [Azure SDK source for 1.0.0](https://github.com/Azure/azure-sdk-for-net/tree/Azure.AI.DocumentIntelligence_1.0.0/sdk/documentintelligence/Azure.AI.DocumentIntelligence),
[Open XML SDK](https://github.com/dotnet/Open-XML-SDK), and
[IChatClient documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.ichatclient).

PDF layout implementation follows the [PdfPig layout analysis documentation](https://github.com/UglyToad/PdfPig/wiki/Document-Layout-Analysis).
