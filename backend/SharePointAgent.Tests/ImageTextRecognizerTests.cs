using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ImageTextRecognizerTests
{
    [Theory]
    [InlineData("Recognized text")]
    [InlineData("")]
    public async Task RecognizesSandboxImageAndRejectsInvalidPaths(string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = [137, 80, 78, 71];
            await File.WriteAllBytesAsync(Path.Combine(root, "scan.PNG"), bytes);
            await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "text");
            using var handler = new OcrHandler(bytes, expected);
            using var http = new HttpClient(handler);
            var options = Options.Create(new DocumentIntelligenceOptions { Endpoint = "https://ocr.test", ApiKey = "test" });
            var files = new AgentFileSystem(Options.Create(new LocalWorkingDirectoryOptions { Directory = root }));
            var recognizer = new ImageTextRecognizer(files, new DocumentIntelligenceClient(http, options), options);

            var result = await recognizer.RecognizeAsync("scan.PNG", default);
            Assert.Equal(expected, result.Text);
            Assert.Equal("scan.PNG", result.FilePath);
            await Assert.ThrowsAsync<ArgumentException>(() => recognizer.RecognizeAsync("notes.txt", default));
            await Assert.ThrowsAsync<ArgumentException>(() => recognizer.RecognizeAsync("../outside.png", default));
            await Assert.ThrowsAsync<ArgumentException>(() => recognizer.RecognizeAsync("", default));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class OcrHandler(byte[] expected, string text) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Contains("prebuilt-read:analyze", request.RequestUri!.AbsoluteUri);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(Convert.ToBase64String(expected), json.RootElement.GetProperty("base64Source").GetString());
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add("Operation-Location", "https://ocr.test/result");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { status = "succeeded", analyzeResult = new { content = text } }), Encoding.UTF8, "application/json")
            };
        }
    }
}
