using System.Net;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AgentMarkdownConverterTests
{
    [Fact]
    public async Task ConvertsSandboxFileWithoutChangingOriginalAndReturnsReadablePath()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var files = new AgentFileSystem(Options.Create(new LocalWorkingDirectoryOptions { Directory = root }));
            await files.WriteTextAsync("sample.docx", "original", false, default);
            using var handler = new ConversionHandler();
            using var http = new HttpClient(handler);
            var converter = new AgentMarkdownConverter(files,
                new MarkItDownClient(http, Options.Create(new MarkItDownOptions { Endpoint = "https://converter.test" })),
                Options.Create(new UploadOptions()));

            var path = await converter.ConvertAsync("sample.docx", default);
            Assert.Equal("# Converted", (await new AgentTextFiles(files).ReadAsync(path)).Text);
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "sample.docx")));
            Assert.StartsWith("Converted/", path);
            Assert.NotEqual(path, await converter.ConvertAsync("sample.docx", default));
            await files.WriteTextAsync("plain.txt", "text", false, default);
            await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync("plain.txt", default));
            await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync("../outside.docx", default));
            Assert.Equal(2, handler.Calls);

            var destination = "Reports/nested/result.md";
            var customPath = await converter.ConvertAsync("sample.docx", default, destination);
            Assert.Equal(destination, customPath);
            await File.WriteAllTextAsync(files.Resolve(customPath), "keep existing");
            await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync("sample.docx", default, destination));
            await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync("sample.docx", default, "../outside.md", true));
            await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync("sample.docx", default, "sample.docx", true));
            Assert.Equal(3, handler.Calls);
            Assert.Equal("keep existing", await File.ReadAllTextAsync(files.Resolve(customPath)));
            Assert.Equal(customPath, await converter.ConvertAsync("sample.docx", default, destination, true));
            Assert.Equal("# Converted", await File.ReadAllTextAsync(files.Resolve(customPath)));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "sample.docx")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class ConversionHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Converted") });
        }
    }
}
