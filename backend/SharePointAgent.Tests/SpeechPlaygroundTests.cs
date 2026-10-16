using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Api;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SpeechPlaygroundTests
{
    [Theory]
    [InlineData(AppRoles.User)]
    [InlineData(AppRoles.GlobalReaderAdmin)]
    public async Task EndpointsRequireGlobalAdmin(string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[typeof(AppUserView)] = new AppUserView(Guid.Empty, "user@example.test", "User",
                [role], true, true, DateTimeOffset.UtcNow, null, "");
            return next(context);
        });
        app.MapSpeechPlaygroundEndpoints();

        await app.StartAsync();
        using var client = app.GetTestClient();
        using var options = await client.GetAsync("/api/admin/speech-playground/options");
        using var synthesis = await client.PostAsJsonAsync("/api/admin/speech-playground", new { text = "Hello", voice = "voice", format = "mp3" });
        Assert.Equal(HttpStatusCode.Forbidden, options.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, synthesis.StatusCode);
        Assert.True(synthesis.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData(false, "mp3", "audio-24khz-48kbitrate-mono-mp3")]
    [InlineData(true, "wav", "riff-24khz-16bit-mono-pcm")]
    public async Task SynthesizesEscapedTextWithSelectedAuthentication(bool managedIdentity, string format, string outputFormat)
    {
        using var handler = new Handler(managedIdentity, outputFormat);
        using var http = new HttpClient(handler);
        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
        var service = new SpeechPlaygroundService(http, credential, Options.Create(new SpeechOptions
        {
            Endpoint = "https://speech.example/", ResourceId = "/resource", UseManagedIdentity = managedIdentity, ApiKey = "secret"
        }));
        var bytes = await service.SynthesizeAsync("Hello <voice> & goodbye", "en-US-JennyNeural", format, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("", "mp3")]
    [InlineData("hello", "invalid")]
    [InlineData("bad\u0001text", "mp3")]
    public async Task RejectsInvalidInputWithoutCallingAzure(string text, string format)
    {
        using var handler = new Handler(false, "");
        using var http = new HttpClient(handler);
        var service = new SpeechPlaygroundService(http, Substitute.For<TokenCredential>(), Options.Create(new SpeechOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SynthesizeAsync(text, "voice", format, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SynthesizeAsync(new string('a', 3001), "voice", "mp3", default));
        Assert.Equal(0, handler.Calls);
        Assert.False(service.Configured);
    }

    private sealed class Handler(bool managedIdentity, string outputFormat) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (managedIdentity)
            {
                Assert.Equal("aad#/resource#test-token", request.Headers.Authorization!.Parameter);
                Assert.False(request.Headers.Contains("Ocp-Apim-Subscription-Key"));
            }
            else
            {
                Assert.Equal("secret", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
                Assert.Null(request.Headers.Authorization);
            }
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/tts/cognitiveservices/voices/list", request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    """[{"ShortName":"en-US-JennyNeural","DisplayName":"Jenny","Locale":"en-US","Gender":"Female"}]""", Encoding.UTF8, "application/json") };
            }
            Assert.Equal("/tts/cognitiveservices/v1", request.RequestUri!.AbsolutePath);
            Assert.Equal(outputFormat, request.Headers.GetValues("X-Microsoft-OutputFormat").Single());
            var xml = XElement.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("Hello <voice> & goodbye", xml.Elements().Single().Value);
            Assert.Empty(xml.Elements().Single().Elements());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }
    }
}
