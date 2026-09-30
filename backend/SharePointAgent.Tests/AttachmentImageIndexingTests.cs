using System.Net;
using System.Net.Http.Json;
using System.ClientModel.Primitives;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AttachmentImageIndexingTests
{
    [Theory]
    [InlineData("scan.PNG", true, true, true)]
    [InlineData("scan.jpeg", false, true, false)]
    [InlineData("picture.webp", true, true, false)]
    [InlineData("scan.tiff", true, false, true)]
    [InlineData("scan.tiff", false, false, false)]
    public async Task IndexContentCombinesSupportedProvidersAndRecordsVisionUsage(string name, bool ocrConfigured, bool visionExpected, bool ocrExpected)
    {
        var root = Path.Combine(Path.GetTempPath(), "image-index-" + Guid.NewGuid().ToString("N"));
        var working = Options.Create(new LocalWorkingDirectoryOptions { Directory = root });
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var dbOptions = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new SharePointIndexDbContext(dbOptions)));
        await using var db = new SharePointIndexDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var file = new ChatMessageAttachmentFileEntity { FileName = name, BlobName = "image", SizeBytes = 3 };
        db.ChatMessageAttachmentFiles.Add(file);
        await db.SaveChangesAsync();
        var directory = Path.Combine(working.Value.ResolvedAttachmentsDirectory, file.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original" + Path.GetExtension(name));
        await File.WriteAllBytesAsync(original, [1, 2, 3]);
        try
        {
            var uploads = Options.Create(new UploadOptions { ImageFileExtensions = [".png", ".jpeg", ".webp", ".tiff"] });
            using var cache = new AttachmentContentCache(null!, null!, uploads, working);
            using var handler = new ImageHandler();
            using var http = new HttpClient(handler);
            var openAi = new AzureOpenAIClient(new Uri("https://vision.test"), new AzureKeyCredential("test"),
                new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
            var ocr = Options.Create(new DocumentIntelligenceOptions { Endpoint = ocrConfigured ? "https://ocr.test" : null, ApiKey = "test" });
            var service = new AttachmentImageService(cache, openAi, new DocumentIntelligenceClient(http, ocr),
                Options.Create(new OpenAiOptions { ChatDeployment = "vision" }), ocr, uploads, factory);
            if (!visionExpected && !ocrExpected)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConvertForIndexAsync(file, default));
                Assert.Contains("DocumentIntelligence:Endpoint", error.Message);
                Assert.Equal(0, handler.VisionCalls);
                Assert.Equal(0, handler.OcrCalls);
                return;
            }
            var markdown = await service.ConvertForIndexAsync(file, default);
            Assert.NotNull(markdown);
            Assert.Equal(visionExpected, markdown.Contains("A receipt"));
            Assert.Equal(ocrExpected, markdown.Contains("Total 42"));
            Assert.Equal(visionExpected ? 1 : 0, handler.VisionCalls);
            Assert.Equal(ocrExpected ? 2 : 0, handler.OcrCalls);
            var usage = await db.ImageDescriptionTokenUsage.ToListAsync();
            Assert.Equal(visionExpected ? 1 : 0, usage.Count);
            if (visionExpected)
            {
                Assert.Equal(file.Id, usage[0].AttachmentId);
                Assert.Equal(12, usage[0].TotalTokens);
            }
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(original));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public int VisionCalls { get; private set; }

        public int OcrCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "vision.test")
            {
                VisionCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { id = "test", model = "vision", created = 1, choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "A receipt" } } }, usage = new { prompt_tokens = 8, completion_tokens = 4, total_tokens = 12 } })
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
                Content = JsonContent.Create(new { status = "succeeded", analyzeResult = new { content = "Total 42" } })
            });
        }
    }
}
