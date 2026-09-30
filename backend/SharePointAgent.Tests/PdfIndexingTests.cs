using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class PdfIndexingTests
{
    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("scan.PDF", true)]
    [InlineData("report.pdf", false)]
    public async Task BothPipelinesUseDocumentIntelligenceAndPreserveOriginal(string name, bool configured)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("%PDF-1.7 test");
        using var handler = new PdfHandler(bytes);
        using var http = new HttpClient(handler);
        var options = Options.Create(new DocumentIntelligenceOptions
        {
            Endpoint = configured ? "https://ocr.test" : null,
            ApiKey = "test"
        });
        var client = new DocumentIntelligenceClient(http, options);
        // No MarkItDown client: PDF extraction must use Document Intelligence exclusively.
        var extractor = new ContentExtractor(client, options, null!, NullLogger<ContentExtractor>.Instance);
        var item = new DriveItemChange("id", name, null, "application/pdf", bytes.Length, null, null, null, true, false, null);
        var root = Path.Combine(Path.GetTempPath(), "pdf-index-" + Guid.NewGuid().ToString("N"));
        var working = Options.Create(new LocalWorkingDirectoryOptions { Directory = root });
        var id = Guid.NewGuid();
        var directory = Path.Combine(working.Value.ResolvedAttachmentsDirectory, id.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "original" + Path.GetExtension(name));
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var markItDown = new MarkItDownClient(http, Options.Create(new MarkItDownOptions { Endpoint = "https://converter.test" }));
            using var cache = new AttachmentContentCache(null!, markItDown, Options.Create(new UploadOptions()), working, client);
            var file = new ChatMessageAttachmentFileEntity { Id = id, FileName = name, SizeBytes = bytes.Length };
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbOptions = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
            var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
            factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new SharePointIndexDbContext(dbOptions)));
            await using var db = new SharePointIndexDbContext(dbOptions);
            await db.Database.EnsureCreatedAsync();
            db.ChatMessageAttachmentFiles.Add(file);
            await db.SaveChangesAsync();
            var pdfs = new AttachmentPdfService(cache, client, factory);
            if (configured)
            {
                Assert.Equal("Page one\nScanned page two", await extractor.ExtractAsync(item, bytes, default));
                Assert.Equal("Page one\nScanned page two", await cache.ConvertForIndexAsync(file, default));
                Assert.Equal(2, handler.Analyses);
                Assert.Equal("Page one\nScanned page two", await pdfs.ExtractTextAsync(id, default));
                Assert.Equal(3, handler.Analyses);
            }
            else
            {
                var sharePointError = await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync(item, bytes, default));
                var attachmentError = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.ConvertForIndexAsync(file, default));
                Assert.Contains("DocumentIntelligence:Endpoint", sharePointError.Message);
                Assert.Contains("DocumentIntelligence:Endpoint", attachmentError.Message);
                await Assert.ThrowsAsync<InvalidOperationException>(() => pdfs.ExtractTextAsync(id, default));
                Assert.Equal(0, handler.Analyses);
            }
            Assert.Equal(0, handler.Conversions);
            var analyses = handler.Analyses;
            Assert.Equal("# Converted PDF", await cache.ConvertToMarkdownAsync(file, default));
            Assert.Equal(1, handler.Conversions);
            Assert.Equal(analyses, handler.Analyses);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Null(await pdfs.ExtractTextAsync(Guid.NewGuid(), default));
            file.FileName = "notes.txt";
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => pdfs.ExtractTextAsync(id, default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PdfHandler(byte[] expected) : HttpMessageHandler
    {
        public int Analyses { get; private set; }

        public int Conversions { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "converter.test")
            {
                Conversions++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/convert", request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Converted PDF") };
            }
            if (request.Method == HttpMethod.Post)
            {
                Analyses++;
                Assert.Contains("prebuilt-read:analyze", request.RequestUri!.AbsoluteUri);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal(Convert.ToBase64String(expected), body.RootElement.GetProperty("base64Source").GetString());
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add("Operation-Location", "https://ocr.test/result");
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "succeeded", analyzeResult = new { content = "Page one\nScanned page two" } })
            };
        }
    }
}
