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
    [InlineData(AppRoles.User, "PUT", "/api/users/123/storage", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "PUT", "/api/users/123/storage", false)]
    [InlineData(AppRoles.GlobalAdmin, "PUT", "/api/users/123/storage", true)]
    [InlineData(AppRoles.User, "PUT", "/api/users/123/tokens", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "PUT", "/api/users/123/tokens", false)]
    [InlineData(AppRoles.GlobalAdmin, "PUT", "/api/users/123/tokens", true)]
    [InlineData(AppRoles.User, "GET", "/api/storage/attachments", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "GET", "/api/storage/attachments", true)]
    [InlineData(AppRoles.GlobalAdmin, "GET", "/api/storage/attachments", true)]
    [InlineData(AppRoles.User, "GET", "/api/state/indexed-files", false)]
    [InlineData(AppRoles.User, "POST", "/api/search/hybrid", true)]
    [InlineData(AppRoles.User, "POST", "/api/chat/conversations", true)]
    [InlineData(AppRoles.User, "POST", "/api/attachment-files", true)]
    [InlineData(AppRoles.User, "POST", "/api/attachment-files/123/signatures", true)]
    [InlineData(AppRoles.GlobalAdmin, "GET", "/api/admin/document-signing/adobe", true)]
    [InlineData(AppRoles.GlobalAdmin, "POST", "/api/admin/document-signing/adobe/authorize", true)]
    [InlineData(AppRoles.GlobalAdmin, "POST", "/api/admin/document-signing/adobe/exchange", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "GET", "/api/admin/document-signing/adobe", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/admin/document-signing/adobe/exchange", false)]
    [InlineData(AppRoles.User, "GET", "/api/admin/document-signing/adobe", false)]
    [InlineData(AppRoles.User, "POST", "/api/admin/document-signing/adobe/authorize", false)]
    [InlineData(AppRoles.User, "POST", "/api/admin/document-signing/adobe/exchange", false)]
    [InlineData(AppRoles.User, "DELETE", "/api/attachment-files/123/signatures/456", true)]
    [InlineData(AppRoles.GlobalAdmin, "DELETE", "/api/attachment-files/123/signatures/456", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "DELETE", "/api/attachment-files/123/signatures/456", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/attachment-files/123/signatures", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/attachment-files/123/signatures/456/prepare", false)]
    [InlineData(AppRoles.User, "GET", "/api/signing-templates", true)]
    [InlineData(AppRoles.User, "POST", "/api/signing-templates", true)]
    [InlineData(AppRoles.User, "PUT", "/api/signing-templates/123", true)]
    [InlineData(AppRoles.User, "DELETE", "/api/signing-templates/123", true)]
    [InlineData(AppRoles.User, "PATCH", "/api/signing-templates/123", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "PATCH", "/api/signing-templates/123", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "GET", "/api/signing-templates", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/signing-templates", false)]
    [InlineData(AppRoles.User, "POST", "/api/attachment-files/123/describe-image", true)]
    [InlineData(AppRoles.User, "POST", "/api/attachment-files/123/extract-text", true)]
    [InlineData(AppRoles.User, "POST", "/api/attachment-files/123/convert-to-markdown", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/attachment-files/123/convert-to-markdown", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/attachment-files/123/describe-image", false)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/attachment-files/123/extract-text", false)]
    [InlineData(AppRoles.User, "POST", "/api/chat/workspaces", true)]
    [InlineData(AppRoles.User, "PUT", "/api/chat/workspaces/123", true)]
    [InlineData(AppRoles.User, "DELETE", "/api/chat/workspaces/123", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "GET", "/api/chat/workspaces", true)]
    [InlineData(AppRoles.GlobalReaderAdmin, "POST", "/api/chat/workspaces", false)]
    [InlineData("Entra administrator", "GET", "/api/chat/workspaces", false)]
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
        {
            await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveAsync(null,
                new("invalid@example.com", "Invalid", roles), default));
        }

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
    [InlineData(true, 200, "GET", "download")]
    [InlineData(false, 404, "GET", "download")]
    [InlineData(true, 200, "POST", "describe-image")]
    [InlineData(false, 404, "POST", "describe-image")]
    [InlineData(true, 200, "POST", "extract-text")]
    [InlineData(false, 404, "POST", "extract-text")]
    [InlineData(true, 200, "POST", "convert-to-markdown")]
    [InlineData(false, 404, "POST", "convert-to-markdown")]
    public async Task UploadedFileAccessChecksCreatedById(bool ownsFile, int expectedStatus, string method, string action)
    {
        await using var fixture = await Fixture.CreateAsync();
        var current = await fixture.Service.LinkEntraAccountAsync("tenant", "object", "user@example.com", "User", default);
        var other = await fixture.Service.LinkEntraAccountAsync("tenant", "other", "other@example.com", "Other", default);
        var file = new ChatMessageAttachmentFileEntity { Id = Guid.NewGuid(), CreatedById = ownsFile ? current.Id : other.Id, FileName = "file.txt", BlobName = "file" };
        fixture.Db.ChatMessageAttachmentFiles.Add(file);
        await fixture.Db.SaveChangesAsync();
        var context = new DefaultHttpContext { RequestServices = fixture.Services };
        context.Response.Body = new MemoryStream();
        context.Request.Method = method;
        context.Request.Path = $"/api/attachment-files/{file.Id}/{action}";
        context.Request.RouteValues["id"] = file.Id.ToString();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "attachment"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "tenant"), new Claim("oid", "object"), new Claim("roles", AppRoles.GlobalAdmin)], "test"));
        var reachedEndpoint = false;
        var middleware = new AppIdentityMiddleware(_ =>
{
    reachedEndpoint = true;
    return Task.CompletedTask;
});
        // Already bound accounts do not need directory or SharePoint calls for attachment access.
        await middleware.InvokeAsync(context, fixture.Service, null!, fixture.Db, null!);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(ownsFile, reachedEndpoint);
    }

    [Fact]
    public async Task StorageQuotaCountsFilesByCreatorAndAllowsExactLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.Service.SaveAsync(null, new("quota@example.com", "Quota", [AppRoles.User]), default);
        user = await fixture.Service.SaveStorageAsync(user.Id, new(100, user.ConcurrencyStamp), default);
        var other = await fixture.Service.SaveAsync(null, new("other@example.com", "Other", [AppRoles.User]), default);
        fixture.Db.ChatMessageAttachmentFiles.Add(Attachment(other.Id, 999));
        var retained = Attachment(user.Id, 40);
        retained.Status = UploadIndexStatus.Failed;
        fixture.Db.ChatMessageAttachmentFiles.Add(retained);
        await fixture.Db.SaveChangesAsync();
        var wrote = false;
        await AttachmentStorageQuota.StoreAsync(fixture.Db, Attachment(user.Id, 60), _ =>
{
    wrote = true;
    return Task.CompletedTask;
}, default);
        Assert.True(wrote);
        var error = await Assert.ThrowsAsync<UserManagementException>(() => AttachmentStorageQuota.StoreAsync(fixture.Db,
            Attachment(user.Id, 1), _ => throw new InvalidOperationException("Must reject before writing content"), default));
        Assert.Equal(409, error.Status);
        var linked = await fixture.Service.LinkEntraAccountAsync("tenant", "quota", user.Email, "Quota", default);
        Assert.Equal(100L, linked.AttachmentStorageUsedBytes);
        Assert.Equal(100L, linked.AttachmentStorageLimitBytes);
        fixture.Db.ChatMessageAttachmentFiles.Remove(retained);
        await fixture.Db.SaveChangesAsync();
        await AttachmentStorageQuota.StoreAsync(fixture.Db, Attachment(user.Id, 40), _ => Task.CompletedTask, default);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(9L, false)]
    [InlineData(10L, true)]
    [InlineData(null, true)]
    public async Task StorageQuotaAppliesToAdminsAndSupportsUnlimited(long? limit, bool allowed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.Service.SaveAsync(null, new("admin@example.com", "Admin", [AppRoles.GlobalAdmin]), default);
        user = await fixture.Service.SaveStorageAsync(user.Id, new(limit, user.ConcurrencyStamp), default);
        var upload = () => AttachmentStorageQuota.StoreAsync(fixture.Db, Attachment(user.Id, 10), _ => Task.CompletedTask, default);
        if (allowed)
        {
            await upload();
        }
        else
        {
            Assert.Equal(409, (await Assert.ThrowsAsync<UserManagementException>(upload)).Status);
        }

        Assert.Equal(allowed ? 1 : 0, await fixture.Db.ChatMessageAttachmentFiles.CountAsync());
    }

    [Fact]
    public async Task FailedUploadRollsBackUsageAndLoweredLimitKeepsExistingFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.Service.SaveAsync(null, new("quota@example.com", "Quota", [AppRoles.User]), default);
        user = await fixture.Service.SaveStorageAsync(user.Id, new(100, user.ConcurrencyStamp), default);
        await Assert.ThrowsAsync<IOException>(() => AttachmentStorageQuota.StoreAsync(fixture.Db, Attachment(user.Id, 80),
            _ => throw new IOException("Storage unavailable"), default));
        Assert.Equal(0, await fixture.Db.ChatMessageAttachmentFiles.CountAsync());
        await AttachmentStorageQuota.StoreAsync(fixture.Db, Attachment(user.Id, 80), _ => Task.CompletedTask, default);
        var updated = await fixture.Service.SaveStorageAsync(user.Id, new(50, user.ConcurrencyStamp), default);
        Assert.Equal(80L, updated.AttachmentStorageUsedBytes);
        Assert.Equal(50L, updated.AttachmentStorageLimitBytes);
        await Assert.ThrowsAsync<UserManagementException>(() => AttachmentStorageQuota.StoreAsync(fixture.Db,
            Attachment(user.Id, 1), _ => Task.CompletedTask, default));
        Assert.Equal(1, await fixture.Db.ChatMessageAttachmentFiles.CountAsync());
    }

    [Fact]
    public async Task InvalidStorageLimitsAreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.Service.SaveAsync(null, new("quota@example.com", "Quota", [AppRoles.User]), default);
        foreach (var limit in new[] { -1L, long.MaxValue })
        {
            await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveStorageAsync(user.Id,
                new(limit, user.ConcurrencyStamp), default));
        }
    }

    [Fact]
    public async Task StorageUpdatesAreIndependentAndRejectStaleVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.Service.SaveAsync(null, new("quota@example.com", "Quota", [AppRoles.User, AppRoles.GlobalReaderAdmin], false), default);
        var storage = await fixture.Service.SaveStorageAsync(user.Id, new(100, user.ConcurrencyStamp), default);
        Assert.Equal(user.Email, storage.Email);
        Assert.Equal(user.DisplayName, storage.DisplayName);
        Assert.Equal(user.Roles, storage.Roles);
        Assert.False(storage.IsActive);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveStorageAsync(user.Id, new(200, user.ConcurrencyStamp), default))).Status);
        var edited = await fixture.Service.SaveAsync(user.Id, new(user.Email, "Updated name", [AppRoles.User], true, storage.ConcurrencyStamp), default);
        Assert.Equal(100L, edited.AttachmentStorageLimitBytes);
        var unlimited = await fixture.Service.SaveStorageAsync(user.Id, new(null, edited.ConcurrencyStamp), default);
        Assert.Null(unlimited.AttachmentStorageLimitBytes);
        Assert.Equal("Updated name", unlimited.DisplayName);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveStorageAsync(Guid.NewGuid(), new(100, "missing"), default))).Status);
    }

    [Fact]
    public async Task SystemStorageIncludesAllUsersAndLegacyFilesRegardlessOfIndexStatus()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(new SystemAttachmentStorage(0, 0, 0, 0), await fixture.Service.GetSystemAttachmentStorageAsync(default));
        var user = await fixture.Service.SaveAsync(null, new("storage@example.com", "Storage", [AppRoles.User]), default);
        var owned = Attachment(user.Id, 100);
        owned.Status = UploadIndexStatus.Failed;
        var legacy = Attachment(user.Id, 50);
        legacy.CreatedById = null;
        fixture.Db.ChatMessageAttachmentFiles.AddRange(owned, legacy);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(new SystemAttachmentStorage(2, 150, 150, 50), await fixture.Service.GetSystemAttachmentStorageAsync(default));
        fixture.Db.ChatMessageAttachmentFiles.Remove(legacy);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(new SystemAttachmentStorage(1, 100, 100, 0), await fixture.Service.GetSystemAttachmentStorageAsync(default));
    }

    [Fact]
    public async Task MonthlyTokenLimitsAndDailyUsageAreIndependentOfAccountEditsAndMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.Service.SaveAsync(null, new("tokens@example.com", "Tokens", [AppRoles.User]), default);
        Assert.Null(user.MonthlyTokenLimit);
        var now = DateTimeOffset.UtcNow;
        var first = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var conversation = Guid.NewGuid();
        var question = Guid.NewGuid();
        var beforeRecording = DateTimeOffset.UtcNow;
        await MonthlyTokenQuota.RecordUsageAsync(fixture.Db, user.Id, conversation, question, first, new(20, 10, 30, 999), "model-a", default);
        var createdAt = (await fixture.Db.ChatTokenUsage.AsNoTracking().SingleAsync(x => x.QuestionId == question)).CreatedAtUtc;
        Assert.InRange(createdAt, beforeRecording, DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, createdAt.Offset);

        // A whole-turn fallback row is marked as such rather than posing as the turn's first request.
        Assert.Equal(ChatTokenUsageEntity.TurnTotalSequence,
            (await fixture.Db.ChatTokenUsage.AsNoTracking().SingleAsync(x => x.QuestionId == question)).Sequence);
        await MonthlyTokenQuota.RecordUsageAsync(fixture.Db, user.Id, conversation, question, first, new(20, 10, 30), "model-b", default);
        Assert.Equal("model-a", (await fixture.Db.ChatTokenUsage.AsNoTracking().SingleAsync(x => x.QuestionId == question)).ModelId);
        Assert.Equal(createdAt, (await fixture.Db.ChatTokenUsage.AsNoTracking().SingleAsync(x => x.QuestionId == question)).CreatedAtUtc);
        await MonthlyTokenQuota.RecordUsageAsync(fixture.Db, user.Id, conversation, Guid.NewGuid(), first.AddHours(1), new(5, 5, 10), "model-b", default);
        await MonthlyTokenQuota.RecordUsageAsync(fixture.Db, user.Id, conversation, Guid.NewGuid(), first.AddDays(1), new(10, 10, 20), "model-a", default);
        await MonthlyTokenQuota.RecordUsageAsync(fixture.Db, user.Id, conversation, Guid.NewGuid(), first.AddMonths(-1), new(900, 100, 1000), null, default);
        var modelTotals = await fixture.Db.ChatTokenUsage.Where(x => x.Month == MonthlyTokenQuota.MonthKey(first))
            .GroupBy(x => x.ModelId).Select(x => new { Model = x.Key, Tokens = x.Sum(t => t.TotalTokens ?? 0) }).ToListAsync();
        Assert.Equal(50, modelTotals.Single(x => x.Model == "model-a").Tokens);
        Assert.Equal(10, modelTotals.Single(x => x.Model == "model-b").Tokens);
        var limited = await fixture.Service.SaveTokenLimitAsync(user.Id, new(50, user.ConcurrencyStamp), default);
        Assert.Equal(60, limited.MonthlyTokensUsed);
        Assert.Equal(2, limited.DailyTokenUsage!.Count);
        Assert.Equal(3, limited.DailyModelTokenUsage!.Count);
        Assert.Equal(50, limited.DailyModelTokenUsage.Where(x => x.ModelId == "model-a").Sum(x => x.TotalTokens));
        Assert.Equal(10, limited.DailyModelTokenUsage.Where(x => x.ModelId == "model-b").Sum(x => x.TotalTokens));
        Assert.Equal(limited.MonthlyTokensUsed, limited.DailyModelTokenUsage.Sum(x => x.TotalTokens));
        Assert.Equal(40, limited.DailyTokenUsage[0].TotalTokens);
        Assert.Equal(25, limited.DailyTokenUsage[0].InputTokens);
        Assert.Equal(15, limited.DailyTokenUsage[0].OutputTokens);
        Assert.Equal(first.AddMonths(1), limited.TokenUsageResetsAtUtc);
        Assert.Empty(await fixture.Db.ChatMessages.ToListAsync());
        Assert.Equal(0, await MonthlyTokenQuota.UsedAsync(fixture.Db, user.Id, MonthlyTokenQuota.MonthKey(first.AddMonths(1))));
        Assert.Equal(429, Assert.Throws<UserManagementException>(() => MonthlyTokenQuota.EnsureAvailable(limited.MonthlyTokenLimit, limited.MonthlyTokensUsed)).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveTokenLimitAsync(user.Id, new(100, user.ConcurrencyStamp), default))).Status);
        var edited = await fixture.Service.SaveAsync(user.Id, new(user.Email, "Renamed", [AppRoles.User], true, limited.ConcurrencyStamp), default);
        Assert.Equal(50, edited.MonthlyTokenLimit);
        var unlimited = await fixture.Service.SaveTokenLimitAsync(user.Id, new(null, edited.ConcurrencyStamp), default);
        Assert.Equal(60, unlimited.MonthlyTokensUsed);
        Assert.Null(unlimited.MonthlyTokenLimit);
        foreach (var limit in new[] { -1L, long.MaxValue })
        {
            await Assert.ThrowsAsync<UserManagementException>(() => fixture.Service.SaveTokenLimitAsync(user.Id, new(limit, unlimited.ConcurrencyStamp), default));
        }
    }

    [Fact]
    public async Task IndependentImagesCountTowardQuotaWithOrWithoutCompletedTurns()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.Service.SaveAsync(null, new("vision-quota@example.com", "Vision", [AppRoles.User]), default);
        var now = DateTimeOffset.UtcNow;
        var month = MonthlyTokenQuota.MonthKey(now);
        var conversation = Guid.NewGuid();
        var question = Guid.NewGuid();
        await MonthlyTokenQuota.RecordUsageAsync(fixture.Db, user.Id, conversation, question, now, new(80, 20, 100), "chat", default);
        fixture.Db.ImageDescriptionTokenUsage.AddRange(
            new ImageDescriptionTokenUsageEntity { UserId = user.Id, QuestionId = question, Day = MonthlyTokenQuota.DayKey(now), Month = month, ModelId = "vision", InputTokens = 15, OutputTokens = 5, TotalTokens = 20 },
            new ImageDescriptionTokenUsageEntity { UserId = user.Id, QuestionId = question, Day = MonthlyTokenQuota.DayKey(now), Month = month, ModelId = "vision", InputTokens = 20, OutputTokens = 10, TotalTokens = 30 },
            new ImageDescriptionTokenUsageEntity { UserId = user.Id, QuestionId = Guid.NewGuid(), Day = MonthlyTokenQuota.DayKey(now), Month = month, ModelId = "vision", InputTokens = 30, OutputTokens = 10, TotalTokens = 40 },
            new ImageDescriptionTokenUsageEntity { UserId = user.Id, Day = MonthlyTokenQuota.DayKey(now.AddMonths(-1)), Month = MonthlyTokenQuota.MonthKey(now.AddMonths(-1)), ModelId = "vision", TotalTokens = 900 });
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(190, await MonthlyTokenQuota.UsedAsync(fixture.Db, user.Id, month));
        var daily = await MonthlyTokenQuota.DailyUsageAsync(fixture.Db, user.Id, month);
        Assert.Equal(190, daily.Sum(x => x.TotalTokens));
        Assert.Equal(90, daily.Single(x => x.ModelId == "vision").TotalTokens);
        Assert.Equal(100, (await fixture.Db.ChatTokenUsage.SingleAsync()).TotalTokens ?? 0);
        var profile = await fixture.Service.SaveTokenLimitAsync(user.Id, new(190, user.ConcurrencyStamp), default);
        Assert.Equal(190, profile.MonthlyTokensUsed);
        Assert.Throws<UserManagementException>(() => MonthlyTokenQuota.EnsureAvailable(profile.MonthlyTokenLimit, profile.MonthlyTokensUsed));
    }

    [Fact]
    public void TokenPeriodsUseUtcAndRollOverAtYearBoundary()
    {
        var local = new DateTimeOffset(2027, 1, 1, 1, 0, 0, TimeSpan.FromHours(7));
        Assert.Equal(202612, MonthlyTokenQuota.MonthKey(local));
        Assert.Equal(20261231, MonthlyTokenQuota.DayKey(local));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), MonthlyTokenQuota.NextMonth(local));
        MonthlyTokenQuota.EnsureAvailable(null, long.MaxValue);
        MonthlyTokenQuota.EnsureAvailable(100, 99);
        Assert.Throws<UserManagementException>(() => MonthlyTokenQuota.EnsureAvailable(0, 0));
        Assert.Throws<UserManagementException>(() => MonthlyTokenQuota.EnsureAvailable(100, 100));
    }

    [Fact]
    public void TokenUsageMigrationMatchesTheCurrentModel()
    {
        using var db = new SharePointIndexDbContext(new DbContextOptionsBuilder<SharePointIndexDbContext>()
            .UseSqlServer("Server=localhost;Database=ModelOnly;Integrated Security=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static ChatMessageAttachmentFileEntity Attachment(Guid creator, long size) => new()
    {
        Id = Guid.NewGuid(),
        CreatedById = creator,
        FileName = "file.txt",
        BlobName = Guid.NewGuid().ToString(),
        SizeBytes = size
    };

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
