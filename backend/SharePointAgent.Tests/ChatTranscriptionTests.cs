using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class ChatTranscriptionTests
{
    private static readonly byte[] Audio = [1, 2, 3, 4];

    [Fact]
    public async Task TranscribesThroughDeploymentApiAndRecordsTokenUsage()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Body = """{"text":" Xin chào, please summarise the contract. ","usage":{"type":"tokens","input_tokens":120,"output_tokens":14,"total_tokens":134}}""";

        var result = await fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm;codecs=opus", null, default);

        Assert.Equal("Xin chào, please summarise the contract.", result.Text);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal("https://example.openai.azure.com/openai/deployments/gpt-4o-transcribe/audio/transcriptions?api-version=2025-03-01-preview", request.Url);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Contains("name=response_format", request.Body);
        Assert.Contains("filename=dictation.webm", request.Body);
        Assert.Contains("Content-Type: audio/webm", request.Body);

        await using var db = fixture.CreateContext();
        var usage = Assert.Single(db.TranscriptionTokenUsage);
        Assert.Equal((fixture.User.Id, "gpt-4o-transcribe", 4L, 120L, 14L, 134L),
            (usage.UserId, usage.ModelId, usage.AudioBytes, usage.InputTokens, usage.OutputTokens, usage.TotalTokens));
        Assert.Equal(134, await MonthlyTokenQuota.UsedAsync(db, fixture.User.Id, usage.Month));
    }

    [Fact]
    public async Task SeparateTranscriptionResourceUsesItsOwnEndpointAndKey()
    {
        await using var fixture = await Fixture.CreateAsync(configure: options =>
        {
            options.TranscriptionEndpoint = "https://speech-region.cognitiveservices.azure.com";
            options.TranscriptionApiKey = "speech-key";
            options.TranscriptionApiVersion = "2025-04-01-preview";
        });
        await fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", null, default);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(("https://speech-region.cognitiveservices.azure.com/openai/deployments/gpt-4o-transcribe/audio/transcriptions?api-version=2025-04-01-preview", "speech-key"),
            (request.Url, request.ApiKey));
    }

    [Fact]
    public async Task RecordedDurationFillsInWhenTheModelReportsNone()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Body = """{"text":"Hello","usage":{"type":"tokens","input_tokens":40,"output_tokens":2,"total_tokens":42}}""";
        await fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", 12.34, default);
        await fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", 9_999, default);
        await fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", double.NaN, default);
        await fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", -3, default);
        await using var db = fixture.CreateContext();
        Assert.Equal([12.3, ChatTranscriptionService.MaxSeconds, null, null],
            db.TranscriptionTokenUsage.AsEnumerable().OrderBy(x => x.CreatedAtUtc).Select(x => x.DurationSeconds));
    }

    [Fact]
    public async Task DurationUsageIsRecordedWithoutTokens()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Body = """{"text":"Hello","usage":{"type":"duration","seconds":3.5}}""";
        // The provider's duration wins over the browser's measurement.
        await fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/mp4", 4.2, default);
        await using var db = fixture.CreateContext();
        var usage = Assert.Single(db.TranscriptionTokenUsage);
        Assert.Equal((3.5, (long?)null), (usage.DurationSeconds, usage.TotalTokens));
    }

    [Fact]
    public async Task ExhaustedMonthlyLimitBlocksTranscriptionBeforeCallingTheModel()
    {
        await using var fixture = await Fixture.CreateAsync(monthlyLimit: 100);
        await using (var db = fixture.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.TranscriptionTokenUsage.Add(new()
            {
                CreatedAtUtc = now, Day = MonthlyTokenQuota.DayKey(now), Month = MonthlyTokenQuota.MonthKey(now),
                UserId = fixture.User.Id, ModelId = "gpt-4o-transcribe", TotalTokens = 100
            });
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", null, default));
        Assert.Equal(429, error.Status);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task InvalidInputAndProviderErrorsAreReportedWithoutDetails()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "video/mp4", null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.TranscribeAsync(fixture.User.Id, Audio, null, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.TranscribeAsync(fixture.User.Id, [], "audio/webm", null, default));
        Assert.Empty(fixture.Handler.Requests);

        fixture.Handler.Status = HttpStatusCode.BadRequest;
        fixture.Handler.Body = """{"error":{"message":"secret provider detail"}}""";
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", null, default));
        Assert.DoesNotContain("secret", error.Message);
        await using var db = fixture.CreateContext();
        Assert.Empty(db.TranscriptionTokenUsage);
    }

    [Fact]
    public async Task DictationIsOffWithoutADeployment()
    {
        await using var fixture = await Fixture.CreateAsync(deployment: "");
        Assert.False(fixture.Service.View.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.TranscribeAsync(fixture.User.Id, Audio, "audio/webm", null, default));
    }

    [Theory]
    [InlineData("audio/webm;codecs=opus", "webm")]
    [InlineData("AUDIO/OGG; codecs=opus", "ogg")]
    [InlineData("audio/mp4", "m4a")]
    [InlineData("audio/mpeg", "mp3")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/flac", null)]
    [InlineData("", null)]
    public void RecorderContentTypesMapToExtensions(string contentType, string? extension)
    {
        Assert.Equal(extension, ChatTranscriptionService.ExtensionFor(contentType));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = """{"text":""}""";

        public List<(string Url, string? ApiKey, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        private DbContextOptions<SharePointIndexDbContext> options = null!;

        public ApplicationUser User { get; } = new() { UserName = "speaker" };

        public FakeHandler Handler { get; } = new();

        public ChatTranscriptionService Service { get; private set; } = null!;

        public SharePointIndexDbContext CreateContext() => new(options);

        public static async Task<Fixture> CreateAsync(long? monthlyLimit = null, string deployment = "gpt-4o-transcribe", Action<OpenAiOptions>? configure = null)
        {
            var fixture = new Fixture();
            fixture.User.MonthlyTokenLimit = monthlyLimit;
            await fixture.connection.OpenAsync();
            fixture.connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
            fixture.options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(fixture.connection).Options;
            var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
            factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(fixture.CreateContext()));
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(fixture.User);
            await db.SaveChangesAsync();
            var settings = new OpenAiOptions
            {
                Endpoint = "https://example.openai.azure.com/", ApiKey = "test-key", TranscriptionDeployment = deployment
            };
            configure?.Invoke(settings);
            var openAi = Options.Create(settings);
            fixture.Service = new ChatTranscriptionService(new HttpClient(fixture.Handler), openAi,
                new MonthlyTokenQuota(factory, TimeProvider.System), factory, TimeProvider.System);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
