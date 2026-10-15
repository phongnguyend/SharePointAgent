using System.Net;
using System.Net.Http.Json;
using System.ClientModel.Primitives;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SharePointAgent.Persistence;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointAgent.Api;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class DocumentPlaygroundTests
{
    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public async Task NativeParsersReturnMarkdownWithoutExternalServices(string format)
    {
        await using var app = await CreateApp(AppRoles.GlobalAdmin);
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateDocument(format)), "file", $"example.{format}");
        form.Add(new StringContent("true"), "skip_images");
        using var response = await client.PostAsync($"/api/admin/{format}-playground/convert", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("Example", json.RootElement.GetProperty("result").GetProperty("markdown").GetString());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("result").GetProperty("warnings").ValueKind);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("true", "true", false)]
    [InlineData("false", null, false)]
    [InlineData("false", "false", false)]
    [InlineData("false", "true", true)]
    public async Task PptxEnrichesImagesOnlyWhenRequested(string? skipImages, string? enrichImages, bool expectAnalysis)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var dbOptions = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new SharePointIndexDbContext(dbOptions)));
        await using var db = new SharePointIndexDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        using var images = new ImageHandler();
        using var http = new HttpClient(images);
        var openAi = new AzureOpenAIClient(new Uri("https://vision.test"), new AzureKeyCredential("test"),
            new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        var ocrOptions = Options.Create(new DocumentIntelligenceOptions { Endpoint = "https://ocr.test", ApiKey = "test" });
        var ocr = new DocumentIntelligenceClient(http, ocrOptions);
        var extractor = new ImageExtractor(ocr, openAi, Options.Create(new OpenAiOptions { ChatDeployment = "vision" }), ocrOptions, factory);
        await using var app = await CreateApp(AppRoles.GlobalAdmin, services =>
        {
            services.AddSingleton(extractor);
            services.AddSingleton(ocr);
        });
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateDocument("pptx", includeImage: true)), "file", "image.pptx");
        if (skipImages is not null)
        {
            form.Add(new StringContent(skipImages), "skip_images");
        }
        if (enrichImages is not null)
        {
            form.Add(new StringContent(enrichImages), "enrich_images");
        }
        using var response = await client.PostAsync("/api/admin/pptx-playground/convert", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var markdown = json.RootElement.GetProperty("result").GetProperty("markdown").GetString()!;
        Assert.Contains("<!-- image:", markdown);
        Assert.Equal(expectAnalysis ? 1 : 0, images.DescriptionCalls);
        Assert.Equal(expectAnalysis ? 2 : 0, images.OcrCalls);
        var usage = await db.ImageDescriptionTokenUsage.ToListAsync();
        Assert.Equal(expectAnalysis ? 1 : 0, usage.Count);
        if (expectAnalysis)
        {
            Assert.Equal(Guid.Empty, usage[0].UserId);
            Assert.Equal(12, usage[0].TotalTokens);
        }
        Assert.Equal(expectAnalysis, markdown.Contains("Description: A diagram"));
        Assert.Equal(expectAnalysis, markdown.Contains("Extracted text (OCR): Diagram text"));
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public int DescriptionCalls { get; private set; }

        public int OcrCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "vision.test")
            {
                DescriptionCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { id = "test", model = "vision", created = 1,
                        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "A diagram" } } },
                        usage = new { prompt_tokens = 8, completion_tokens = 4, total_tokens = 12 } })
                });
            }
            OcrCalls++;
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add("Operation-Location", "https://ocr.test/result");
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "succeeded", analyzeResult = new { content = "Diagram text" } })
            });
        }
    }

    private static byte[] CreateDocument(string format, bool includeImage = false)
    {
        if (format == "pdf")
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            builder.AddPage(600, 800).AddText("Example", 12, new PdfPoint(40, 700), font);
            return builder.Build();
        }
        using var stream = new MemoryStream();
        if (format == "docx")
        {
            using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
            document.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("Example")))));
        }
        else if (format == "xlsx")
        {
            using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
            var main = document.AddWorkbookPart();
            var sheet = main.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
            {
                CellReference = "A1",
                DataType = S.CellValues.InlineString,
                InlineString = new S.InlineString(new S.Text("Example"))
            }) { RowIndex = 1 }));
            main.Workbook = new S.Workbook(new S.Sheets(new S.Sheet { Name = "Sheet", SheetId = 1, Id = main.GetIdOfPart(sheet) }));
        }
        else
        {
            using var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation);
            var main = document.AddPresentationPart();
            var slide = main.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(new P.Shape(
                new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "Text" },
                    new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(), new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("Example"))))))));
            main.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId { Id = 256, RelationshipId = main.GetIdOfPart(slide) }));
            if (includeImage)
            {
                var image = slide.AddImagePart(ImagePartType.Png);
                using var data = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
                image.FeedData(data);
                slide.Slide.CommonSlideData!.ShapeTree!.Append(new P.Picture(
                    new P.NonVisualPictureProperties(new P.NonVisualDrawingProperties { Id = 2, Name = "Diagram" },
                        new P.NonVisualPictureDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                    new P.BlipFill(new A.Blip { Embed = slide.GetIdOfPart(image) }), new P.ShapeProperties()));
            }
        }
        return stream.ToArray();
    }

    [Theory]
    [InlineData("markitdown", "convert", "example.docx")]
    [InlineData("pageindex", "index", "example.md")]
    public async Task UsesOnlySelectedServiceAndReturnsResult(string provider, string action, string name)
    {
        using var handler = new Handler(provider);
        using var http = new HttpClient(handler);
        await using var app = await CreateApp(AppRoles.GlobalAdmin, services =>
        {
            if (provider == "markitdown")
            {
                var options = Options.Create(new MarkItDownOptions { Endpoint = "https://markitdown.test", ApiKey = "key" });
                services.AddSingleton<IOptions<MarkItDownOptions>>(options);
                services.AddSingleton(new MarkItDownClient(http, options));
            }
            else
            {
                services.AddSingleton(new PageIndexClient(http, Options.Create(new PageIndexOptions { Endpoint = "https://pageindex.test", ApiKey = "key" })));
            }
        });
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("# Example"), "file", name);
        form.Add(new StringContent("false"), "include_text");
        form.Add(new StringContent("true"), "include_summaries");
        using var response = await client.PostAsync($"/api/admin/{provider}-playground/{action}", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
        Assert.True(json.RootElement.TryGetProperty("elapsedMs", out _));
        Assert.Equal("key", handler.Key);
        if (provider == "markitdown")
        {
            Assert.Equal("# Converted", result.GetProperty("markdown").GetString());
        }
        else
        {
            Assert.Equal("Example", result.GetProperty("doc_name").GetString());
            Assert.Equal(12, result.GetProperty("usage").GetProperty("input_tokens").GetInt64());
            Assert.Equal(4, result.GetProperty("usage").GetProperty("output_tokens").GetInt64());
            Assert.Equal("test-model", result.GetProperty("usage").GetProperty("model_id").GetString());
            Assert.Equal("false", handler.Fields["include_text"]);
            Assert.Equal("true", handler.Fields["include_summaries"]);
        }
    }

    [Theory]
    [InlineData("markitdown/convert", AppRoles.GlobalReaderAdmin, "a.docx", "true", 403)]
    [InlineData("pageindex/index", AppRoles.User, "a.md", "true", 403)]
    [InlineData("pageindex/index", AppRoles.GlobalAdmin, "a.docx", "true", 400)]
    [InlineData("pageindex/index", AppRoles.GlobalAdmin, "a.md", "invalid", 400)]
    [InlineData("pdf/convert", AppRoles.User, "a.pdf", "true", 403)]
    [InlineData("docx/convert", AppRoles.GlobalReaderAdmin, "a.docx", "true", 403)]
    [InlineData("xlsx/convert", AppRoles.User, "a.xlsx", "true", 403)]
    [InlineData("pptx/convert", AppRoles.User, "a.pptx", "true", 403)]
    [InlineData("pdf/convert", AppRoles.GlobalAdmin, "a.docx", "true", 400)]
    [InlineData("pdf/convert", AppRoles.GlobalAdmin, "a.pdf", "true", 400)]
    [InlineData("docx/convert", AppRoles.GlobalAdmin, "a.docx", "true", 400)]
    [InlineData("xlsx/convert", AppRoles.GlobalAdmin, "a.xlsx", "true", 400)]
    [InlineData("pptx/convert", AppRoles.GlobalAdmin, "a.pptx", "true", 400)]
    public async Task RejectsUnauthorizedOrInvalidRequests(string target, string role, string name, string flag, int expected)
    {
        await using var app = await CreateApp(role);
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("sample"), "file", name);
        form.Add(new StringContent(flag), "include_text");
        var parts = target.Split('/');
        using var response = await client.PostAsync($"/api/admin/{parts[0]}-playground/{parts[1]}", form);
        Assert.Equal(expected, (int)response.StatusCode);
    }

    private static async Task<WebApplication> CreateApp(string role, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "admin@example.test", "Admin", [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapDocumentPlaygroundEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class Handler(string provider) : HttpMessageHandler
    {
        public string? Key { get; private set; }

        public Dictionary<string, string> Fields { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Key = request.Headers.GetValues("X-Api-Key").Single();
            foreach (var field in (MultipartFormDataContent)request.Content!)
            {
                Fields[field.Headers.ContentDisposition!.Name!.Trim('"')] = await field.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(provider == "markitdown" ? "# Converted" :
                    """{"doc_name":"Example","structure":[],"usage":{"model_id":"test-model","prompt_tokens":12,"completion_tokens":4,"total_tokens":16}}""")
            };
        }
    }
}
