using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [Fact]
    public async Task InAppRequestSavesFieldsAndCompletesWithStoredDocumentAndAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(["DocuSign", InAppSigning.Provider], fixture.Service.EnabledProviders);
        var row = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.InAppInput, default);
        Assert.Equal(InAppSigning.Draft, row.Status);
        Assert.Equal("Please sign by Friday.", row.Message);
        Assert.Equal([new("Reviewer", "reviewer@example.com"), new("Approver", "approver@example.com")],
            System.Text.Json.JsonSerializer.Deserialize<SignatureRecipient[]>(row.RecipientsJson)!);
        Assert.NotNull(row.OriginalSha256);
        await fixture.Provider.DidNotReceive().CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>());

        SigningField[] layout =
        [
            new("sig-1", "signature", 1, 0.1, 0.7, 0.3, 0.08, null),
            new("date-1", "date", 1, 0.5, 0.7, 0.2, 0.04, null)
        ];
        await fixture.Service.SaveFieldsAsync(row, fixture.User.Id, new(layout), default);
        Assert.Equal(2, (await fixture.Service.GetFieldsAsync(row, default)).Fields.Length);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CompleteAsync(row, fixture.User.Id, "%PDF-signed"u8.ToArray(), default));

        SigningField[] signed = [layout[0] with { Value = Png }, layout[1] with { Value = "2026-10-02" }];
        await fixture.Service.SaveFieldsAsync(row, fixture.User.Id, new(signed), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CompleteAsync(row, Guid.NewGuid(), "%PDF-signed"u8.ToArray(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CompleteAsync(row, fixture.User.Id, "not a pdf"u8.ToArray(), default));
        var completed = await fixture.Service.CompleteAsync(row, fixture.User.Id, "%PDF-signed"u8.ToArray(), default);
        Assert.Equal(InAppSigning.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal("%PDF-signed"u8.ToArray(), await fixture.Service.DownloadAsync(completed, false, default));
        var audit = Encoding.ASCII.GetString(await fixture.Service.DownloadAsync(completed, true, default));
        Assert.StartsWith("%PDF-1.4", audit);
        Assert.Contains("Nguyen Doan <sender@example.com>", audit);
        Assert.Contains("Please sign by Friday.", audit);
        Assert.Contains("1. Reviewer <reviewer@example.com>; 2. Approver <approver@example.com>", audit);
        Assert.Contains(completed.SignedSha256!, audit);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveFieldsAsync(row, fixture.User.Id, new(signed), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CompleteAsync(row, fixture.User.Id, "%PDF-again"u8.ToArray(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteNeedsReviewAsync(fixture.File.Id, row.Id, fixture.User.Id, false, default));
    }

    [Fact]
    public async Task InAppDraftCanBeDiscardedAndIsNeverSentToProvider()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.InAppInput, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(row, default));
        Assert.Equal(InAppSigning.Draft, (await fixture.Service.RefreshAsync(row, default)).Status);
        await fixture.Service.DeleteNeedsReviewAsync(fixture.File.Id, row.Id, fixture.User.Id, false, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.FindAsync(fixture.File.Id, row.Id, fixture.User.Id, true, default));
        Assert.DoesNotContain(fixture.Provider.ReceivedCalls(), x => x.GetMethodInfo().Name is not ("get_Name" or "get_Enabled"));
    }

    [Fact]
    public async Task InAppSignersAreOptionalButValidated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.InAppInput with { Recipients = [], Message = "" }, default);
        Assert.Equal("[]", row.RecipientsJson);
        Assert.Null(row.Message);
        Assert.Throws<ArgumentException>(() => SignatureRequestService.Validate(fixture.InAppInput with { Recipients = [new("Reviewer", "not-an-email")] }));
        Assert.Throws<ArgumentException>(() => SignatureRequestService.Validate(fixture.InAppInput with
        {
            Recipients = [new("A", "same@example.com"), new("B", "SAME@example.com")]
        }));
        Assert.Throws<ArgumentException>(() => SignatureRequestService.Validate(fixture.Input with { Recipients = [] }));
    }

    [Fact]
    public async Task DisabledInAppSigningIsNotOffered()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SigningOptions.InApp.Enabled = false;
        Assert.Equal(["DocuSign"], fixture.Service.EnabledProviders);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateAsync(fixture.File.Id, fixture.User.Id, fixture.InAppInput, default));
    }

    [Theory]
    [InlineData("bad id", "signature", 1, 0.1, 0.1, 0.2, 0.1, null)]
    [InlineData("a", "checkbox", 1, 0.1, 0.1, 0.2, 0.1, null)]
    [InlineData("a", "signature", 0, 0.1, 0.1, 0.2, 0.1, null)]
    [InlineData("a", "signature", 1, 0.9, 0.1, 0.2, 0.1, null)]
    [InlineData("a", "signature", 1, 0.1, 0.1, 0.2, 0.001, null)]
    [InlineData("a", "signature", 1, 0.1, 0.1, 0.2, 0.1, "data:image/jpeg;base64,AAAA")]
    [InlineData("a", "text", 1, 0.1, 0.1, 0.2, 0.1, "line\nbreak")]
    public void InvalidFieldsAreRejected(string id, string type, int page, double x, double y, double width, double height, string? value)
    {
        Assert.Throws<ArgumentException>(() => InAppSigning.Validate([new(id, type, page, x, y, width, height, value)], false));
    }

    [Fact]
    public void FinishingRequiresASignatureAndEveryValue()
    {
        Assert.Throws<ArgumentException>(() => InAppSigning.Validate([new("t", "text", 1, 0, 0, 0.2, 0.1, "Hello")], true));
        Assert.Throws<ArgumentException>(() => InAppSigning.Validate(
            [new("s", "signature", 1, 0, 0, 0.2, 0.1, Png), new("t", "text", 1, 0, 0.5, 0.2, 0.1, null)], true));
        Assert.Throws<ArgumentException>(() => InAppSigning.Validate(
            [new("s", "signature", 1, 0, 0, 0.2, 0.1, Png), new("s", "initials", 1, 0, 0.5, 0.2, 0.1, Png)], false));
        InAppSigning.Validate([new("s", "signature", 1, 0, 0, 0.2, 0.1, Png), new("t", "text", 1, 0, 0.5, 0.2, 0.1, "Hello")], true);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        private readonly string directory = Path.Combine(Path.GetTempPath(), "signing-tests-" + Guid.NewGuid().ToString("N"));

        private AttachmentContentCache cache = null!;

        public ApplicationUser User { get; } = new() { UserName = "sender", DisplayName = "Nguyễn Doãn", Email = "sender@example.com" };

        public ChatMessageAttachmentFileEntity File { get; } = new() { FileName = "contract.pdf", SizeBytes = 9 };

        public ISignatureProvider Provider { get; } = Substitute.For<ISignatureProvider>();

        public SignatureRequestService Service { get; private set; } = null!;

        public SignatureInput Input { get; } = new("DocuSign", "Contract", "", [new("Signer", "signer@example.com")], Guid.NewGuid());

        public SignatureInput InAppInput { get; } = new(InAppSigning.Provider, "Contract", " Please sign by Friday. ",
            [new("Reviewer", "reviewer@example.com"), new("Approver", "approver@example.com")], Guid.NewGuid());

        public DocumentSigningOptions SigningOptions { get; } = new();

        public Dictionary<string, byte[]> Blobs { get; } = [];

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
            var blobs = Substitute.For<BlobServiceClient>();
            var container = Substitute.For<BlobContainerClient>();
            blobs.GetBlobContainerClient(Arg.Any<string>()).Returns(container);
            container.GetBlobClient(Arg.Any<string>()).Returns(call =>
            {
                var name = call.Arg<string>();
                var blob = Substitute.For<BlobClient>();
                blob.UploadAsync(Arg.Any<BinaryData>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(upload =>
                {
                    fixture.Blobs[name] = upload.Arg<BinaryData>().ToArray();
                    return Task.FromResult(Substitute.For<Response<BlobContentInfo>>());
                });
                blob.DownloadContentAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromBytes(fixture.Blobs[name])), Substitute.For<Response>())));
                return blob;
            });
            fixture.cache = new AttachmentContentCache(blobs, null!, Options.Create(new UploadOptions()), working);
            fixture.Provider.Name.Returns("DocuSign");
            fixture.Provider.Enabled.Returns(true);
            fixture.Provider.CreateDraftAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<SignatureInput>(), Arg.Any<CancellationToken>()).Returns("external-id");
            fixture.Provider.DownloadAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("%PDF-signed"u8.ToArray());
            fixture.Service = new SignatureRequestService([fixture.Provider], factory, fixture.cache, Options.Create(fixture.SigningOptions));
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
