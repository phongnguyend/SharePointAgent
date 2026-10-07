using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SharePointAgent.Infrastructure.DocumentSigning;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class SigningTemplateTests
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly SigningField[] Layout =
    [
        new("sig-1", "signature", 1, 0.1, 0.7, 0.3, 0.08, Png),
        new("text-1", "text", 2, 0.5, 0.2, 0.3, 0.04, "Typed value")
    ];

    [Fact]
    public async Task TemplatesStoreLayoutOnlyAndArePersonal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(fixture.Owner.Id, new(" Contract layout ", Layout, 2), default);
        Assert.Equal("Contract layout", created.Name);
        Assert.All(created.Fields, x => Assert.Null(x.Value));
        Assert.Equal(["sig-1", "text-1"], created.Fields.Select(x => x.Id));

        var summary = Assert.Single(await fixture.Service.ListAsync(fixture.Owner.Id, default));
        Assert.Equal((created.Id, 2, 2), (summary.Id, summary.FieldCount, summary.PageCount));
        Assert.All((await fixture.Service.GetAsync(created.Id, fixture.Owner.Id, default)).Fields, x => Assert.Null(x.Value));

        Assert.Empty(await fixture.Service.ListAsync(fixture.Other.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.GetAsync(created.Id, fixture.Other.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.UpdateAsync(created.Id, fixture.Other.Id, new("Taken", Layout, 2), default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.DeleteAsync(created.Id, fixture.Other.Id, default));
    }

    [Fact]
    public async Task TemplatesCanBeOverwrittenRenamedAndDeleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.CreateAsync(fixture.Owner.Id, new("First", Layout, 2), default);
        var second = await fixture.Service.CreateAsync(fixture.Owner.Id, new("Second", Layout, 2), default);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(fixture.Owner.Id, new("First", Layout, 2), default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UpdateAsync(second.Id, fixture.Owner.Id, new("First", Layout, 2), default));
        // Another user may reuse the name.
        await fixture.Service.CreateAsync(fixture.Other.Id, new("First", Layout, 2), default);

        var updated = await fixture.Service.UpdateAsync(first.Id, fixture.Owner.Id, new("First", [Layout[0]], 1), default);
        Assert.Equal((1, 1), (updated.Fields.Length, updated.PageCount));
        var renamed = await fixture.Service.UpdateAsync(first.Id, fixture.Owner.Id, new("Renamed", [Layout[0]], 1), default);
        Assert.Equal("Renamed", renamed.Name);

        var renamedOnly = await fixture.Service.RenameAsync(second.Id, fixture.Owner.Id, new(" Second v2 "), default);
        Assert.Equal(("Second v2", 2), (renamedOnly.Name, renamedOnly.Fields.Length));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RenameAsync(second.Id, fixture.Owner.Id, new("Renamed"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RenameAsync(second.Id, fixture.Owner.Id, new(" "), default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.RenameAsync(second.Id, fixture.Other.Id, new("Mine now"), default));

        await fixture.Service.DeleteAsync(second.Id, fixture.Owner.Id, default);
        Assert.Equal(["Renamed"], (await fixture.Service.ListAsync(fixture.Owner.Id, default)).Select(x => x.Name));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.DeleteAsync(second.Id, fixture.Owner.Id, default));
    }

    [Theory]
    [InlineData("", 2)]
    [InlineData("Line\nbreak", 2)]
    [InlineData("Valid", 0)]
    [InlineData("Valid", 1)]
    public void InvalidTemplatesAreRejected(string name, int pageCount)
    {
        Assert.Throws<ArgumentException>(() => SigningTemplateService.Normalize(new(name, Layout, pageCount)));
    }

    [Fact]
    public void EmptyOrInvalidLayoutsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SigningTemplateService.Normalize(new("Empty", [], 1)));
        Assert.Throws<ArgumentException>(() => SigningTemplateService.Normalize(new("Bad", [Layout[0] with { X = 0.95 }], 1)));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        public ApplicationUser Owner { get; } = new() { UserName = "owner" };

        public ApplicationUser Other { get; } = new() { UserName = "other" };

        public SigningTemplateService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.connection.OpenAsync();
            fixture.connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString().ToUpperInvariant());
            var options = new DbContextOptionsBuilder<SharePointIndexDbContext>().UseSqlite(fixture.connection).Options;
            var factory = Substitute.For<IDbContextFactory<SharePointIndexDbContext>>();
            factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new SharePointIndexDbContext(options)));
            await using var db = new SharePointIndexDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(fixture.Owner, fixture.Other);
            await db.SaveChangesAsync();
            fixture.Service = new SigningTemplateService(factory);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
