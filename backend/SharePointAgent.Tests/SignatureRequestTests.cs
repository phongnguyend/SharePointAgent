using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.DocumentSigning;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SignatureRequestTests
{
    [Fact]
    public async Task DraftsAreIdempotentOwnedAndCannotDownloadBeforeCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default);
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal("Draft", row.Status);
        var repeat = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default);
        Assert.Equal(row.Id, repeat.Id);
        await fixture.Provider.Received(1).CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.FindAsync(fixture.File.Id, row.Id, Guid.NewGuid(), false, default));
        Assert.Equal(row.Id, (await fixture.Service.FindAsync(fixture.File.Id, row.Id, fixture.User.Id, false, default)).Id);
        Assert.Equal(row.Id, (await fixture.Service.FindAsync(fixture.File.Id, row.Id, Guid.NewGuid(), true, default)).Id);
        fixture.Provider.GetStatusAsync("external-id", Arg.Any<CancellationToken>()).Returns("created");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DownloadAsync(row, false, default));
        fixture.Provider.GetStatusAsync("external-id", Arg.Any<CancellationToken>()).Returns("completed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(row, default));
        await fixture.Service.DownloadAsync(row, false, default);
        await fixture.Provider.Received(1).DownloadAsync("external-id", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownProviderOutcomeIsRecordedAndNeverAutomaticallyResubmitted()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Provider.CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HttpRequestException("Lost response")));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default));
        var row = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default);
        Assert.Equal("NeedsReview", row.Status);
        Assert.Null(row.ExternalId);
        await fixture.Provider.Received(1).CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledProviderCannotUploadDocument()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Provider.Enabled.Returns(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default));
        await fixture.Provider.DidNotReceive().CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeedsReviewCanBeDeletedByOwnerOrAdminOnly(bool admin)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Provider.CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HttpRequestException("Lost response")));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default));
        var row = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.DeleteNeedsReviewAsync(fixture.File.Id, row.Id, Guid.NewGuid(), false, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.DeleteNeedsReviewAsync(Guid.NewGuid(), row.Id, fixture.User.Id, true, default));
        fixture.Provider.ClearReceivedCalls();
        await fixture.Service.DeleteNeedsReviewAsync(fixture.File.Id, row.Id, admin ? Guid.NewGuid() : fixture.User.Id, admin, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.FindAsync(fixture.File.Id, row.Id, fixture.User.Id, true, default));
        Assert.Empty(fixture.Provider.ReceivedCalls());
    }

    [Fact]
    public async Task DraftAndCompletedRecordsCannotBeDeleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.Input, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteNeedsReviewAsync(fixture.File.Id, row.Id, fixture.User.Id, true, default));
        fixture.Provider.GetStatusAsync("external-id", Arg.Any<CancellationToken>()).Returns("completed");
        await fixture.Service.RefreshAsync(row, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteNeedsReviewAsync(fixture.File.Id, row.Id, fixture.User.Id, true, default));
        Assert.Equal("completed", (await fixture.Service.FindAsync(fixture.File.Id, row.Id, fixture.User.Id, true, default)).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        private readonly string directory = Path.Combine(Path.GetTempPath(), "signing-tests-" + Guid.NewGuid().ToString("N"));

        private AttachmentContentCache cache = null!;

        public ApplicationUser User { get; } = new() { UserName = "sender" };

        public ChatMessageAttachmentFileEntity File { get; } = new() { FileName = "contract.pdf", SizeBytes = 9 };

        public ISignatureProvider Provider { get; } = Substitute.For<ISignatureProvider>();

        public SignatureRequestService Service { get; private set; } = null!;

        public SignatureInput Input { get; } = new("DocuSign", "Contract", "", [new("Signer", "signer@example.com")], Guid.NewGuid());

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.connection.OpenAsync();
            fixture.connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
            var dbOptions = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(fixture.connection).Options;
            var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
            factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new SharePointIndexDbContext(dbOptions)));
            await using var db = new SharePointIndexDbContext(dbOptions);
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(fixture.User);
            db.ChatMessageAttachmentFiles.Add(fixture.File);
            await db.SaveChangesAsync();
            var working = Options.Create(new LocalWorkingDirectoryOptions { Directory = fixture.directory });
            var path = Path.Combine(working.Value.ResolvedAttachmentsDirectory, fixture.File.Id.ToString("N"));
            Directory.CreateDirectory(path);
            await System.IO.File.WriteAllBytesAsync(Path.Combine(path, "original.pdf"), "%PDF-test"u8.ToArray());
            fixture.cache = new AttachmentContentCache(null!, null!, Options.Create(new UploadOptions()), working);
            fixture.Provider.Name.Returns("DocuSign");
            fixture.Provider.Enabled.Returns(true);
            fixture.Provider.CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>()).Returns("external-id");
            fixture.Provider.DownloadAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("%PDF-signed"u8.ToArray());
            fixture.Service = new SignatureRequestService([fixture.Provider], factory, fixture.cache);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            cache.Dispose();
            await connection.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
