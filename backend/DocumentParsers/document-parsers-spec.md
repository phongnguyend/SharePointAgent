# Document Parsers --- Implementation Spec

## Objective

Implement four C#/.NET parsers for PDF, DOCX, PPTX, and XLSX, each with
its own interface and format-specific result structure. Each parser exposes
`ParseAsync` for extraction and `ConvertToMarkdown` for converting its own
result to Markdown. Extract images/figures as
`ImageElement`, describe them using an LLM in a separate image stage,
optionally extract text using OCR, then convert the enriched result to
Markdown for reuse by downstream consumers. The output is not tied to a
specific downstream use case.

``` text
PDF  -> IPdfDocumentParser.ParseAsync  -> PdfParseResult
DOCX -> IDocxDocumentParser.ParseAsync -> DocxParseResult
PPTX -> IPptxDocumentParser.ParseAsync -> PptxParseResult
XLSX -> IXlsxDocumentParser.ParseAsync -> XlsxParseResult

For each format, retaining its specific result type:
Result -> ImageDescriptionProcessor (LLM / optional OCR)
       -> Same parser's ConvertToMarkdown(result)
       -> Markdown output
```

Packages:

``` bash
dotnet add package Azure.AI.DocumentIntelligence
dotnet add package DocumentFormat.OpenXml
```

## Format-specific APIs and shared element model

### Project layout

Keep all projects and `DocumentParsers.slnx` under `backend/DocumentParsers`:

- `DocumentParsers.Pdf` and `DocumentParsers.Pdf.Tests`
- `DocumentParsers.Docx` and `DocumentParsers.Docx.Tests`
- `DocumentParsers.Pptx` and `DocumentParsers.Pptx.Tests`
- `DocumentParsers.Xlsx` and `DocumentParsers.Xlsx.Tests`
- `DocumentParsers.Shared` and `DocumentParsers.Shared.Tests`
- `DocumentParsers.Demo`: console application referencing all four parsers
  for manual conversion, with `appsettings.json`, .NET user secrets,
  environment variables and command-line overrides. Keep credentials out
  of committed settings. Support output paths, image skipping, optional
  image analysis/OCR and cancellation.

Each format implementation references Shared only, and each test project
references its corresponding library. Shared contains contracts, result and
element models, common parsing/Markdown helpers, image analysis, options,
format resolution and orchestration. Its orchestrator depends on parser
interfaces rather than implementations, avoiding circular references.
Format-specific helpers belong with their parser. Preserve the public
`DocumentParsers` namespace across assemblies.

There is no common `IDocumentParser` interface or common parser return
type. Each parser exposes a strongly typed contract. Shared semantic
elements remain reusable within the four result structures.

``` csharp
public interface IPdfDocumentParser
{
    Task<PdfParseResult> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default);

    string ConvertToMarkdown(
        PdfParseResult result,
        CancellationToken cancellationToken = default,
        bool skipImages = false);
}

public interface IDocxDocumentParser
{
    Task<DocxParseResult> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default);

    string ConvertToMarkdown(
        DocxParseResult result,
        CancellationToken cancellationToken = default,
        bool skipImages = false);
}

public interface IPptxDocumentParser
{
    Task<PptxParseResult> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default);

    string ConvertToMarkdown(
        PptxParseResult result,
        CancellationToken cancellationToken = default,
        bool skipImages = false);
}

public interface IXlsxDocumentParser
{
    Task<XlsxParseResult> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default);

    string ConvertToMarkdown(
        XlsxParseResult result,
        CancellationToken cancellationToken = default,
        bool skipImages = false);
}

public interface IImageAnalysisService
{
    Task<string> DescribeAsync(
        ReadOnlyMemory<byte> image,
        string contentType,
        string? contextualText = null,
        CancellationToken cancellationToken = default);

    Task<string> ExtractTextAsync(
        ReadOnlyMemory<byte> image,
        string contentType,
        CancellationToken cancellationToken = default);
}

public sealed class PdfParseResult
{
    public int PageCount { get; init; }

    public List<DocumentElement> Elements { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}

public sealed class DocxParseResult
{
    public List<DocumentElement> BodyElements { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}

public sealed class PptxParseResult
{
    public List<ParsedSlide> Slides { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}

public sealed record ParsedSlide(
    int SlideNumber, IReadOnlyList<DocumentElement> Elements)
{
    public string? Notes { get; init; }
}

public sealed class XlsxParseResult
{
    public List<ParsedWorksheet> Worksheets { get; } = [];

    public Dictionary<string, string> Metadata { get; } = [];

    public List<DocumentParseWarning> Warnings { get; } = [];
}

public sealed record ParsedWorksheet(
    int SheetIndex,
    string SheetName,
    SpreadsheetElement Data,
    IReadOnlyList<ImageElement> Images);

public abstract record DocumentElement
{
    public int? PageNumber { get; init; }

    public long? Order { get; init; }

    public DocumentBoundingBox? BoundingBox { get; init; }
}

public sealed record DocumentBoundingBox(
    double X, double Y, double Width, double Height);

public sealed record TextElement(string Text) : DocumentElement;
public sealed record HeadingElement(string Text, int Level) : DocumentElement;
public sealed record TableElement(string Markdown) : DocumentElement;

public sealed record ImageElement : DocumentElement
{
    public required byte[] Data { get; init; }

    public required string ContentType { get; init; }

    public string? AltText { get; init; }

    public string? Caption { get; init; }

    public string? Anchor { get; init; }

    public string? Description { get; set; }

    public string? ExtractedText { get; set; }
}

public sealed record SpreadsheetElement : DocumentElement
{
    public required string SheetName { get; init; }

    public required IReadOnlyList<SpreadsheetRow> Rows { get; init; }
}

public sealed record SpreadsheetRow(
    int RowIndex, IReadOnlyList<SpreadsheetCell> Cells);

public sealed record SpreadsheetCell(
    string Reference, string? Value, string? Formula);
```

Parsers must not dispose the caller's stream. Copy to a seekable
`MemoryStream` when required. Parsers extract images but must not invoke
the LLM or image OCR service. PDF layout analysis may perform OCR as
part of Azure Document Intelligence parsing; that is separate from
optional OCR of extracted images.

## Format-specific result processing

Keep each result in its own format-specific structure through image
enrichment and Markdown conversion. No shared document wrapper or result
normalizer is required.

- PDF: traverse ordered `Elements`; retain `PageCount` on the result.
- DOCX: traverse ordered `BodyElements`; do not invent page numbers because
  Open XML does not provide reliable rendered pagination.
- PPTX: traverse `Slides` in presentation order, retaining each slide's
  number and element reading order, including gaps for empty slides.
- XLSX: traverse `Worksheets` in workbook order, retaining sheet names,
  indices, rows/cells and image anchors for the later region stage.
- Preserve metadata, warnings and image references on the typed result without duplicating
  binary data. Page, slide and sheet numbers are one-based.

## Image description processor

Implement one processor shared by all formats, accepting an
`IEnumerable<ImageElement>`, processing options and a cancellation token,
and returning operation warnings. The orchestrator enumerates images from
PDF `Elements`, DOCX `BodyElements`, PPTX `Slides[].Elements`, or XLSX
`Worksheets[].Images`, and adds returned warnings to the typed result's
`Warnings`. Pass the existing image instances so enrichment updates that
result directly. For each image, call
`IImageAnalysisService.DescribeAsync`, and set `Description`.

The service exposes two distinct operations:

- `DescribeAsync` must use a vision-capable LLM to explain the image's
  content and meaning, including diagrams, charts, relationships and
  relevant visual context. Alt text/caption may guide the LLM; OCR output
  alone does not satisfy this operation.
- `ExtractTextAsync` must use OCR to transcribe visible text, preserving
  reading order and line breaks where possible. Return an empty string
  when no text is detected. Do not summarize or infer missing text.

LLM description is the normal enrichment operation. When image OCR is
enabled by processing options, also call `ExtractTextAsync` and set
`ExtractedText`. Keep the two outputs separate; neither overwrites the
other. Null means the operation was skipped or failed, while an empty
OCR string means the operation succeeded without finding text.

Production requirements: SHA-256 dedup/cache, bounded concurrency, skip
tiny decorative images when possible, resize oversized images, pass alt
text/caption as context, and do not fail the entire document when one
LLM or OCR request fails. Cache keys must include the operation and
provider/model configuration, plus contextual text for descriptions, so
OCR and descriptions cannot collide. Record operation-specific warnings
on failure, retain any successful output, and propagate caller cancellation.

------------------------------------------------------------------------

# PDF Parser

Use `Azure.AI.DocumentIntelligence` with `prebuilt-layout`.

Extract paragraphs, headings, tables, figures, page numbers,
spans/offsets, and bounding regions. Use span offset as the primary
reading-order key.

``` csharp
public sealed class PdfDocumentParser : IPdfDocumentParser
{
    // Parsing excerpt; also implement ConvertToMarkdown as specified below.
    private readonly DocumentIntelligenceClient _client;

    public PdfDocumentParser(DocumentIntelligenceClient client)
        => _client = client;

    public async Task<PdfParseResult> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        var operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            "prebuilt-layout",
            BinaryData.FromBytes(buffer.ToArray()),
            cancellationToken: cancellationToken);

        return Map(operation.Value);
    }
}
```

Mapping requirements:

1.  Collect paragraphs, tables and figures into temporary
    `(offset, element)` records.
2.  Map `ParagraphRole.Title` to heading level 1 and `SectionHeading` to
    level 2.
3.  Populate `PageNumber`, `Order`, and bounding box where available.
4.  Do not emit paragraphs whose spans are fully contained in a table
    span; otherwise table text is duplicated.
5.  Convert tables to Markdown, escaping `|`, preserving empty cells and
    handling irregular/merged cells reasonably.
6.  Sort all elements by offset before returning `PdfParseResult`;
    populate `PageCount` from the analysis result.

For table duplicate detection, compare paragraph start/end offsets
against each table's start/end span.

### PDF figures

Implement figures using the exact Azure Document Intelligence SDK/API
version selected by the project because figure APIs vary by version.
Request figure output when necessary, retrieve the supported crop/image,
create `ImageElement`, retain caption/page/span/bounding box, and insert
by offset. Leave `Description` null for the common image-description
stage.

Do not silently screenshot arbitrary PDF pages and treat them as
figures.

Per-request application metering may store `AnalyzeResult.Pages.Count`;
do not treat it as authoritative Azure billing data.

------------------------------------------------------------------------

# DOCX Parser

Use Open XML SDK. Walk `Body.ChildElements` in order. Never enumerate
all paragraphs, tables and images independently because that destroys
reading order.

``` csharp
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

public sealed class DocxDocumentParser : IDocxDocumentParser
{
    // Parsing excerpt; also implement ConvertToMarkdown as specified below.
    public async Task<DocxParseResult> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        using var doc = WordprocessingDocument.Open(buffer, false);
        var main = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Missing MainDocumentPart.");
        var body = main.Document.Body
            ?? throw new InvalidOperationException("Missing document body.");

        var result = new DocxParseResult();

        foreach (var child in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (child)
            {
                case Paragraph p:
                    ParseParagraph(p, main, result);
                    break;
                case Table t:
                    ParseTable(t, main, result);
                    break;
            }
        }

        return result;
    }
}
```

`ParseParagraph` and `ParseTable` append to `DocxParseResult.BodyElements`.
A paragraph may contain `text -> image -> text`. Iterate runs and their
child elements. Append `Text`, tab and break values to a buffer. When a
`Drawing` is encountered, flush the current buffer to
`TextElement`/`HeadingElement`, add image(s), then continue accumulating
text.

Resolve images via:

``` text
Drawing -> Drawing.Blip -> relationship ID -> ImagePart
```

Store bytes, MIME type and alt text from `DW.DocProperties`.

Initially detect `Heading1`, `Heading2`, etc.; production code must
resolve `StyleDefinitionsPart` because custom templates may use custom
style IDs/names.

A first version may flatten DOCX tables to Markdown. Document the
limitation: table cells may contain nested tables, images, lists and
hyperlinks. A later semantic model should allow
`Table -> Row -> Cell -> DocumentElement[]`.

Explicit TODOs: numbering/lists, hyperlinks, headers/footers,
footnotes/endnotes, text boxes, grouped drawings, charts, embedded
objects, captions, nested tables, images inside table cells.

------------------------------------------------------------------------

# PPTX Parser

Use Open XML SDK. Treat slide number as `PageNumber`.

Implement `PptxDocumentParser : IPptxDocumentParser` with
`Task<PptxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)`.

PPTX has no guaranteed linear reading order. Collect positioned elements
and initially sort `Y` then `X`. Keep this ordering logic isolated so a
better `ReadingOrderResolver` can replace it.

Resolve placeholder geometry from the slide, matching layout placeholder
index, then matching master placeholder type before sorting. Local geometry
takes precedence. Preserve unknown positions as null and sort them after
known positions, retaining their source order; never substitute `(0, 0)`
for missing geometry because that incorrectly moves footer text first.

``` csharp
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

private sealed record PositionedElement(
    long X, long Y, DocumentElement Element);
```

For every slide in `Presentation.SlideIdList`:

-   `P.Shape`: extract `TextBody`; preserve paragraph boundaries.
    Preserve automatic numbering, bullet markers and paragraph nesting levels
    in Markdown. Resolve paragraph overrides, shape list styles, matching
    layout/master placeholder styles and presentation defaults. Honor
    explicit no-bullet overrides; continue parent numbering across child
    bullets and reset child counters for each new parent. Normalize bullet
    glyphs to hyphens and numbering schemes to decimal Markdown markers.
    Title/centered-title placeholders become `HeadingElement`; normal
    text becomes `TextElement`.
-   `P.Picture`: resolve
    `BlipFill -> Blip -> relationship ID -> ImagePart`; store bytes,
    MIME type, alt text, slide number and bounding box.
-   `P.GraphicFrame`: if `GraphicData` contains `A.Table`, convert it to
    Markdown.
-   Sort elements by position and add a `ParsedSlide` to
    `PptxParseResult.Slides`, preserving presentation order and empty slides.

High-priority TODOs: `GroupShape`, SmartArt, charts/chart labels,
images inside groups, rotated/overlapping shapes,
master/layout text. `GroupShape` should be implemented before claiming
production-grade PPTX support.

------------------------------------------------------------------------

# XLSX Parser

Use Open XML SDK. Treat worksheet index as `PageNumber`.

Implement `XlsxDocumentParser : IXlsxDocumentParser` with
`Task<XlsxParseResult> ParseAsync(Stream stream, CancellationToken cancellationToken = default)`.

Do not convert every cell into an independent `TextElement`, and do not
turn a huge worksheet into one giant Markdown table.

``` csharp
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
```

For every sheet:

1.  Resolve `WorksheetPart`.
2.  Parse `SheetData`.
3.  Build `SpreadsheetRow` / `SpreadsheetCell`.
4.  Retain cell reference, displayed/cached value and formula.
5.  Extract worksheet drawings/images.
6.  Add a `ParsedWorksheet` containing a `SpreadsheetElement` and
    positioned `ImageElement`s to `XlsxParseResult.Worksheets`. Preserve
    workbook order and empty worksheets.

Correctly handle shared strings, inline strings, booleans, numbers,
cached formula values and formulas. Do not implement an Excel
calculation engine. OpenXML reads stored values; it does not recalculate
arbitrary formulas.

Excel dates require style/number-format inspection because they are
commonly numeric serials. Do not label every numeric cell as a date.

Implement cell-reference helpers:

``` text
A1  -> row 0, column 0
D25 -> row 24, column 3
AA7 -> row 6, column 26
```

Extract images through:

``` text
WorksheetPart
 -> DrawingsPart
 -> WorksheetDrawing
 -> OneCellAnchor / TwoCellAnchor
 -> Picture
 -> Blip
 -> ImagePart
```

Store the top-left anchor cell such as `D3`.

For large sheets, implement a normalization/region stage instead of
giant Markdown:

``` text
A1:D20    Sales Summary
A25:F100  Transactions
H2:M15    Forecast
N2        Revenue Chart
```

Explicit TODOs: merged cells, hidden rows/columns/sheets, Excel tables,
named ranges, charts, pivot tables, comments/notes, rich text,
date/time/percentage/currency formatting.

------------------------------------------------------------------------

# Markdown conversion methods

Each parser implements `ConvertToMarkdown` with its own result type, as
declared in the interfaces above. Keep extraction in `ParseAsync` and
conversion in this separate method on the same parser. Conversion is
synchronous, operates on the supplied result, and performs no file reads,
LLM calls or OCR requests. Honor cancellation during traversal.

All four methods accept optional `skipImages = false`. When true, omit
image blocks entirely, including captions, alt text, anchors, descriptions
and OCR text, without modifying the result. Preserve non-image content and
slide/worksheet headings. Do not emit PDF page markers solely for skipped
images. `DocumentConversionService.ConvertAsync` exposes the same option
and bypasses image enrichment when images are skipped. Parsing still
extracts images so the result remains reusable for later conversions.

Shared internal helpers for escaping, tables and image text are allowed;
each parser owns format-specific traversal and Markdown output. Conversion
must not remove or reorder source data, metadata or warnings on the result.

- PDF: render `Elements` in reading order, retaining page attribution.
- DOCX: render `BodyElements` in order, preserving inline image placement.
- PPTX: render slides in presentation order with `<!-- Slide number: 3 -->`
  comments (using each slide's number), including empty slides, and render
  each slide's elements in reading order. Extract speaker text from the
  slide's notes part into `ParsedSlide.Notes`, excluding slide number,
  date, footer/header and thumbnail placeholders. After slide content,
  render nonempty notes beneath `### Notes:` with a blank line between the
  heading and notes text, preserving subsequent
  paragraphs and line breaks. Omit the notes heading when notes are empty.
- XLSX: render worksheet headings in workbook order, including empty
  worksheets; split data into bounded regions and place image text using
  its cell anchors. Retain sheet names and region references.

Rules:

-   `HeadingElement`: `#`, `##`, etc.
-   `TextElement`: normal paragraph.
-   `TableElement`: emit stored Markdown.
-   `ImageElement`: emit a semantic text block containing caption/alt
    text/LLM description and, when available, separately labeled OCR text;
    do not embed binary data. Omit missing or empty fields.
-   `SpreadsheetElement`: render only normalized/bounded regions, not an
    unbounded giant sheet.

Suggested image rendering:

``` text
[Image]
Caption: System architecture
Description: Architecture diagram showing a React frontend communicating
with an ASP.NET Core API and SQL Server.
Extracted text (OCR): React frontend; ASP.NET Core API; SQL Server
```

Keep source metadata on semantic elements even though Markdown cannot
preserve all layout information.

------------------------------------------------------------------------

# Parser selection

Add a format resolver and an orchestration service that injects the four
specific parser interfaces plus the image processor. Prefer MIME type
plus extension validation rather than trusting extension alone.

``` csharp
public enum DocumentFormat
{
    Pdf,
    Docx,
    Pptx,
    Xlsx
}

public interface IDocumentFormatResolver
{
    DocumentFormat Resolve(
        string fileName,
        string? contentType);
}
```

Supported types:

``` text
.pdf  -> IPdfDocumentParser  -> PdfDocumentParser  -> PdfParseResult
.docx -> IDocxDocumentParser -> DocxDocumentParser -> DocxParseResult
.pptx -> IPptxDocumentParser -> PptxDocumentParser -> PptxParseResult
.xlsx -> IXlsxDocumentParser -> XlsxDocumentParser -> XlsxParseResult
```

The orchestrator switches on `DocumentFormat`, calls the corresponding
typed parser's `ParseAsync`, enriches images on that typed result, then
calls the same parser's `ConvertToMarkdown(result, cancellationToken)`.
Keep warnings and source metadata available on the original result for
the caller. Do not reintroduce
`IDocumentParser`, return `object`, or use casts/dynamic to unify dispatch.

Reject legacy `.doc`, `.ppt`, `.xls` unless a separate conversion
strategy is added.

------------------------------------------------------------------------

# Error handling and security

All parsers must:

-   accept `CancellationToken`;
-   reject unsupported/corrupt files with clear exceptions;
-   enforce configurable maximum input size;
-   limit extracted image count and total image bytes;
-   protect against ZIP bombs in OpenXML packages;
-   avoid unbounded allocation;
-   never execute macros, formulas, embedded objects or external links;
-   not fetch external resources referenced by a document;
-   avoid logging document contents or image bytes by default.

Use the `Warnings` collection on each typed result so partial parsing can
succeed. Append image-enrichment warnings to the same collection and retain
them after Markdown conversion:

``` csharp
public sealed record DocumentParseWarning(
    string Code,
    string Message);
```

Examples: unsupported SmartArt, LLM description failed, image OCR failed, malformed
table, formula has no cached value.

------------------------------------------------------------------------

# Testing requirements

Create unit/integration fixtures for each format.

PDF: - normal text; - scanned/OCR PDF; - headings; - multi-page; -
tables; - figures; - paragraph/table duplication; - multi-column layout.

DOCX: - headings; - paragraph text; - image between text runs; -
multiple images; - table; - image in table; - lists; - custom styles.

PPTX: - title + body; - multiple text boxes; - image; - table; -
two-column slide; - grouped shapes; - SmartArt fixture documented as
supported or warning.

XLSX: - shared strings; - inline strings; - numbers; - booleans; -
formulas; - dates; - multiple worksheets; - sparse rows; - image
anchors; - large worksheet; - merged cells.

Cross-format acceptance test: four documents expressing equivalent
content should produce semantically similar Markdown through each parser's
`ConvertToMarkdown` after image enrichment, containing
headings, body text, tables and image descriptions.

Contract tests must verify that each interface returns its specific result
type and its conversion method accepts that type. Verify Markdown ordering,
empty slide/worksheet headings and image placement, and ensure conversion
preserves source data, metadata, warnings and anchors on the result. Verify
conversion uses existing enrichment without calling the LLM or OCR service.
Image-service tests must distinguish
LLM descriptions from OCR transcriptions, verify OCR is optional, cover
images with no text, and ensure one operation's failure does not discard
the other's successful output. Verify separate cache entries for operation,
model/configuration and description context, and cancellation propagation.

------------------------------------------------------------------------

# Definition of Done

The implementation is complete when:

1.  Each parser implements its own specific interface: `IPdfDocumentParser`,
    `IDocxDocumentParser`, `IPptxDocumentParser`, or `IXlsxDocumentParser`.
2.  Parsers return `PdfParseResult`, `DocxParseResult`, `PptxParseResult`,
    or `XlsxParseResult`, retaining these types throughout processing.
3.  Text/table/image order is preserved reasonably for each format.
4.  Images are extracted with MIME type and useful source metadata.
5.  Image enrichment is format-independent: `DescribeAsync` uses an LLM,
    and `ExtractTextAsync` performs optional OCR with separate output.
6.  Each parser implements `ConvertToMarkdown` for its specific result type.
7.  PDF table text is not duplicated.
8.  DOCX inline images preserve surrounding text order.
9.  PPTX uses a replaceable position-based reading-order resolver.
10. XLSX retains spreadsheet semantics and avoids giant unbounded
    Markdown.
11. Cancellation, size limits, corrupt files and partial failures are
    handled.
12. Tests cover the edge cases above.

## Implementation order

Implement in this order:

1.  Four parser interfaces/result structures and shared element models.
2.  Shared Markdown utilities for use by each parser's conversion method.
3.  Image service (LLM description and OCR) and ImageDescriptionProcessor.
4.  DOCX parser + Markdown conversion + tests.
5.  PPTX parser + Markdown conversion + tests.
6.  XLSX parser + Markdown conversion + tests.
7.  PDF parser + Markdown conversion + tests.
8.  Format resolver and typed parser orchestration.
9.  Security/limits/warnings.
10. End-to-end tests.

Before coding a parser, inspect the installed NuGet package version and
compile against its actual API. In particular, do not invent Azure
Document Intelligence figure methods or OpenXML members from memory;
adapt to the exact package version and add tests for the selected API.
