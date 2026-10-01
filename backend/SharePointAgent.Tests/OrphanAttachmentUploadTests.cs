using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class OrphanAttachmentUploadTests
{
    [Theory]
    [InlineData("document.pdf")]
    [InlineData("photo.png")]
    [InlineData("notes.txt")]
    public async Task StorageOnlyUploadCreatesOwnedOrphanWithoutUsingIndexingServices(string name)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
        var dbOptions = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(connection).Options;
        var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new SharePointIndexDbContext(dbOptions)));
        await using var db = new SharePointIndexDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var user = new ApplicationUser { UserName = "uploader", IsActive = true, AttachmentStorageLimitBytes = 100 };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var blobs = Substitute.For<BlobServiceClient>();
        var container = Substitute.For<BlobContainerClient>();
        var blob = Substitute.For<BlobClient>();
        blobs.GetBlobContainerClient(Arg.Any<string>()).Returns(container);
        container.GetBlobClient(Arg.Any<string>()).Returns(blob);
        // Search, extraction and embedding dependencies are deliberately absent.
        var service = new ChatMessageAttachmentFileService(factory, blobs, null!, null!, null!,
            Options.Create(new UploadOptions()), Options.Create(new SearchOptions()), NullLogger<ChatMessageAttachmentFileService>.Instance);
        using var content = new MemoryStream([1, 2, 3]);
        var result = await service.CreateAsync(name, "application/octet-stream", content.Length, content, default, user.Id, indexAfterUpload: false);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.True(result.IsOrphan);
        Assert.Equal(UploadIndexStatus.NotStarted, result.Status);
        Assert.Equal(0, result.ChunkCount);
        Assert.Null(result.IndexedAtUtc);
        Assert.Null(result.EmbeddingTokenCount);
        var row = await db.ChatMessageAttachmentFiles.SingleAsync();
        Assert.Equal(user.Id, row.CreatedById);
        Assert.Equal(result.Id, row.Id);
        Assert.Equal($"{result.Id:N}/{name}", row.BlobName);
        container.Received(1).GetBlobClient(row.BlobName);
        Assert.Null(row.ChatMessageAttachmentId);
        Assert.Empty(await db.ChatMessageAttachments.ToListAsync());
        await blob.Received(1).UploadAsync(content, Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>());

        // Storage-only uploads still enforce the user's quota before writing another blob.
        await Assert.ThrowsAsync<UserManagementException>(() => service.CreateAsync(name, null, 101, Stream.Null, default, user.Id, indexAfterUpload: false));
        Assert.Equal(1, await db.ChatMessageAttachmentFiles.CountAsync());
        await blob.Received(1).UploadAsync(Arg.Any<Stream>(), Arg.Any<BlobUploadOptions>(), Arg.Any<CancellationToken>());
    }
}
