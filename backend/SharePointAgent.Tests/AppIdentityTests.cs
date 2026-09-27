using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharePointAgent.Api;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AppIdentityTests
{
    [Fact]
    public async Task UserEndpointsInitializeWithAppUserServiceFromDependencyInjection()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // This test builds the real endpoint delegates without connecting to a database or Entra.
        builder.Services.AddDbContext<SharePointIndexDbContext>(options =>
            options.UseSqlServer("Server=localhost;Database=EndpointBindingTest;Integrated Security=true"));
        builder.Services.AddAppIdentity();
        await using var app = builder.Build();
        app.MapAppUsers();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.GetAsync("/api/roles");
        response.EnsureSuccessStatusCode();
        Assert.Contains(AppRoles.GlobalAdmin, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(AppRoles.GlobalAdmin, "POST", "/api/users", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "GET", "/api/users", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "PUT", "/api/users/123", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/chat/conversations", false)]
    [InlineData(AppRoles.User, "GET", "/api/users", false)]
    [InlineData(AppRoles.User, "GET", "/api/state/indexed-files", false)]
    [InlineData(AppRoles.User, "POST", "/api/search/hybrid", true)]
    [InlineData(AppRoles.User, "POST", "/api/chat/conversations", true)]
    [InlineData(AppRoles.User, "POST", "/api/attachment-files", true)]
    [InlineData(AppRoles.User, "POST", "/api/agents", false)]
    [InlineData("Entra administrator", "GET", "/api/users", false)]
    [InlineData("Entra administrator", "POST", "/api/search/hybrid", false)]
    public void LocalRoleControlsAccess(string role, string method, string path, bool allowed)
        => Assert.Equal(allowed, AppAccess.Allows([role], method, path));

    [Fact]
    public void CombinedRolesGrantUnionWithoutGrantingAdministrativeWrites()
    {
        string[] roles = [AppRoles.GlobalReaderAdmin, AppRoles.User];
        Assert.True(AppAccess.Allows(roles, "GET", "/api/users"));
        Assert.True(AppAccess.Allows(roles, "POST", "/api/chat/conversations"));
        Assert.False(AppAccess.Allows(roles, "POST", "/api/users"));
        Assert.False(AppAccess.RequiresOwnership(roles, "GET"));
        Assert.True(AppAccess.RequiresOwnership(roles, "DELETE"));
        Assert.True(AppAccess.Allows([.. roles, AppRoles.GlobalAdmin], "POST", "/api/users"));
        Assert.False(AppAccess.RequiresOwnership([.. roles, AppRoles.GlobalAdmin], "DELETE"));
        Assert.False(AppAccess.Allows([], "GET", "/api/users"));
    }

    [Fact]
    public async Task MultipleRolesCanBeAddedRemovedAndDeduplicated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.Service.SaveAsync(null, new("multi@example.com", "Multi", [AppRoles.GlobalReaderAdmin, AppRoles.User, AppRoles.User]), default);
        Assert.Equal(new[] { AppRoles.GlobalReaderAdmin, AppRoles.User }, saved.Roles);
        var updated = await fixture.Service.SaveAsync(saved.Id,
            new(saved.Email, saved.DisplayName, [AppRoles.User], true, saved.ConcurrencyStamp), default);
        Assert.Equal(new[] { AppRoles.User }, updated.Roles);
        updated = await fixture.Service.SaveAsync(updated.Id,
            new(updated.Email, updated.DisplayName, [AppRoles.User, AppRoles.GlobalReaderAdmin], true, updated.ConcurrencyStamp), default);
        Assert.Equal(2, updated.Roles.Count);
        Assert.Equal(2, await fixture.Db.UserRoles.CountAsync());
    }

    [Fact]
    public async Task EmptyOrUnknownRoleSetsAreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var roles in new IReadOnlyList<string>[] { [], [AppRoles.User, "Unknown"], null! })
            await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveAsync(null,
                new("invalid@example.com", "Invalid", roles), default));
        Assert.Equal(0, await fixture.Db.Users.CountAsync());
    }

    [Fact]
    public async Task PrecreatedEmailMatchesCaseInsensitivelyAndKeepsRole()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.Service.SaveAsync(null, new("Reader@example.com", "Reader", [AppRoles.GlobalReaderAdmin, AppRoles.User]), default);
        var bound = await fixture.Service.LinkEntraAccountAsync("tenant", "object", "reader@EXAMPLE.COM", "Directory name", default);
        Assert.Equal(saved.Id, bound.Id);
        Assert.Equal(new[] { AppRoles.GlobalReaderAdmin, AppRoles.User }, bound.Roles);
        Assert.True(bound.HasSignedIn);
        Assert.Equal(1, await fixture.Db.Users.CountAsync());
        var repeated = await fixture.Service.LinkEntraAccountAsync("tenant", "object", "changed@example.com", "Changed", default);
        Assert.Equal(saved.Id, repeated.Id);
        Assert.Equal(bound.Roles, repeated.Roles);
        var conflict = await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.LinkEntraAccountAsync("tenant", "another-object", "reader@example.com", "Other", default));
        Assert.Equal(403, conflict.Status);
    }

    [Fact]
    public async Task NewAccountGetsUserRoleAndDisabledAccountCannotSignIn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bound = await fixture.Service.LinkEntraAccountAsync("tenant", "object", "user@example.com", "User", default);
        Assert.Equal(new[] { AppRoles.User }, bound.Roles);
        await fixture.Service.SaveAsync(bound.Id, new(bound.Email, bound.DisplayName, bound.Roles, false, bound.ConcurrencyStamp), default);
        var error = await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.FindBoundAsync("tenant", "object", default));
        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task LastAdminAndStaleUpdatesAreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SeedAdminAsync("admin@example.com", default);
        var admin = await fixture.Service.LinkEntraAccountAsync("tenant", "admin", "admin@example.com", "Admin", default);
        var error = await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveAsync(admin.Id,
            new(admin.Email, admin.DisplayName, [AppRoles.GlobalReaderAdmin, AppRoles.User], true, admin.ConcurrencyStamp), default));
        Assert.Equal(409, error.Status);
        var stale = await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveAsync(admin.Id,
            new(admin.Email, admin.DisplayName, admin.Roles, true, "stale"), default));
        Assert.Equal(409, stale.Status);
    }

    [Fact]
    public async Task BootstrapDoesNotPromoteAnExistingAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bound = await fixture.Service.LinkEntraAccountAsync("tenant", "object", "user@example.com", "User", default);
        await fixture.Service.SeedAdminAsync(bound.Email, default);
        Assert.Equal(new[] { AppRoles.User }, (await fixture.Service.FindBoundAsync("tenant", "object", default))!.Roles);
    }

    [Fact]
    public async Task CreatorsAreOptionalUserForeignKeys()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var type in new[] { typeof(ChatConversationEntity), typeof(ChatMessageAttachmentFileEntity) })
        {
            var entity = fixture.Db.Model.FindEntityType(type)!;
            Assert.True(entity.FindProperty("CreatedById")!.IsNullable);
            Assert.Null(entity.FindProperty("OwnerUserId"));
            Assert.Contains(entity.GetForeignKeys(), fk => fk.Properties.Single().Name == "CreatedById"
                && fk.PrincipalEntityType.ClrType == typeof(ApplicationUser) && fk.DeleteBehavior == DeleteBehavior.Restrict);
        }
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 404)]
    public async Task UploadedFileAccessChecksCreatedById(bool ownsFile, int expectedStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        var current = await fixture.Service.LinkEntraAccountAsync("tenant", "object", "user@example.com", "User", default);
        var other = await fixture.Service.LinkEntraAccountAsync("tenant", "other", "other@example.com", "Other", default);
        var file = new ChatMessageAttachmentFileEntity { Id = Guid.NewGuid(), CreatedById = ownsFile ? current.Id : other.Id, FileName = "file.txt", BlobName = "file" };
        fixture.Db.ChatMessageAttachmentFiles.Add(file);
        await fixture.Db.SaveChangesAsync();
        var context = new DefaultHttpContext { RequestServices = fixture.Services };
        context.Response.Body = new MemoryStream();
        context.Request.Method = "GET";
        context.Request.Path = $"/api/attachment-files/{file.Id}/download";
        context.Request.RouteValues["id"] = file.Id.ToString();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "attachment"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "tenant"), new Claim("oid", "object"), new Claim("roles", AppRoles.GlobalAdmin)], "test"));
        var reachedEndpoint = false;
        var middleware = new AppIdentityMiddleware(_ => { reachedEndpoint = true; return Task.CompletedTask; });
        // Already bound accounts do not need directory or SharePoint calls for attachment access.
        await middleware.InvokeAsync(context, fixture.Service, null!, fixture.Db, null!);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(ownsFile, reachedEndpoint);
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider provider, IServiceScope scope) : IAsyncDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;
        public SharePointIndexDbContext Db => scope.ServiceProvider.GetRequiredService<SharePointIndexDbContext>();
        public AppUserService Service => scope.ServiceProvider.GetRequiredService<AppUserService>();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            connection.CreateFunction("NEWSEQUENTIALID", () => Guid.NewGuid().ToString());
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<SharePointIndexDbContext>(options => options.UseSqlite(connection));
            services.AddIdentityCore<ApplicationUser>(options => options.User.RequireUniqueEmail = true)
                .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<SharePointIndexDbContext>();
            services.AddScoped<AppUserService>();
            var provider = services.BuildServiceProvider();
            var fixture = new Fixture(connection, provider, provider.CreateScope());
            await fixture.Db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
