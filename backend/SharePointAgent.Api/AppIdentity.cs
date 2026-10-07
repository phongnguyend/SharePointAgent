using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using HttpMethods = Microsoft.AspNetCore.Http.HttpMethods;
using SharePointAgent.Domain;
using SharePointAgent.Persistence;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class AppIdentity
{
    public static IServiceCollection AddAppIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>(options => options.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<SharePointIndexDbContext>();
        services.AddScoped<AppUserService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MonthlyTokenQuota>();
        services.AddHostedService<AppIdentityBootstrap>();
        return services;
    }

    public static AppUserView AppUser(this HttpContext context) => (AppUserView)context.Items[typeof(AppUserView)]!;
    public static string EntraObjectId(this HttpContext context) => context.User.FindFirst("oid")!.Value;

    public static void MapAppUsers(this WebApplication app)
    {
        app.MapGet("/api/auth/me", async (HttpContext context, AppUserService users, CancellationToken ct) =>
            Results.Ok(await users.RecordSignInAsync(context.AppUser(), ct)));
        app.MapGet("/api/users", async (AppUserService users, CancellationToken ct, string? search = null, int skip = 0, int top = 25) =>
            skip < 0 || top is < 1 or > 100 ? Results.BadRequest(new { error = "Invalid page size or offset." })
                : Results.Ok(await users.ListAsync(search, skip, top, ct)));
        app.MapPost("/api/users", async (AppUserInput input, AppUserService users, CancellationToken ct) =>
            Results.Ok(await users.SaveAsync(null, input, ct)));
        app.MapPut("/api/users/{id:guid}", async (Guid id, AppUserInput input, AppUserService users, CancellationToken ct) =>
            Results.Ok(await users.SaveAsync(id, input, ct)));
        app.MapGet("/api/roles", () => Results.Ok(AppRoles.All));
        app.MapPut("/api/users/{id:guid}/tokens", async (Guid id, AppUserTokenLimitInput input, AppUserService users, CancellationToken ct) =>
            Results.Ok(await users.SaveTokenLimitAsync(id, input, ct)));
        app.MapPut("/api/users/{id:guid}/storage", async (Guid id, AppUserStorageInput input, AppUserService users, CancellationToken ct) =>
            Results.Ok(await users.SaveStorageAsync(id, input, ct)));
        app.MapGet("/api/storage/attachments", async (AppUserService users, CancellationToken ct) =>
            Results.Ok(await users.GetSystemAttachmentStorageAsync(ct)));
    }
}

public sealed class AppIdentityBootstrap(IServiceScopeFactory scopes, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<AppUserService>();
        foreach (var email in configuration.GetSection("AppIdentity:BootstrapAdminEmails").Get<string[]>() ?? [])
        {
            await users.SeedAdminAsync(email, ct);
        }
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class AppIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AppUserService users, GraphServiceClient graph, SharePointIndexDbContext db, SharePointClient sharePoint)
    {
        if (context.GetEndpoint() is null || context.GetEndpoint()!.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }
        if (context.User.Identity?.IsAuthenticated != true)

        {
            context.Response.StatusCode = 401;
            return;
        }
        try
        {
            var ct = context.RequestAborted;
            var tenant = context.User.FindFirst("tid")!.Value;
            var objectId = context.EntraObjectId();
            var user = await users.FindBoundAsync(tenant, objectId, ct);
            if (user is null)
            {
                // Do not grant preassigned roles based on mutable/unverified token email claims.
                var directoryUser = await graph.Users[objectId].GetAsync(request => request.QueryParameters.Select = ["id", "mail", "userPrincipalName", "displayName"], ct);
                if (!string.Equals(directoryUser?.Id, objectId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UserManagementException("The Entra account could not be verified.", 403);
                }

                var email = string.IsNullOrWhiteSpace(directoryUser!.Mail) ? directoryUser.UserPrincipalName : directoryUser.Mail;
                user = await users.LinkEntraAccountAsync(tenant, objectId, email ?? "", directoryUser.DisplayName ?? "", ct);
            }
            context.Items[typeof(AppUserView)] = user;
            using var embeddingAttribution = SharePointAgent.Application.EmbeddingUsageScope.Begin(new(UserId: user.Id));
            if (!AppAccess.Allows(user.Roles, context.Request.Method, context.Request.Path.Value!))
            {
                throw new UserManagementException("Your application role does not allow this action.", 403);
            }

            if (AppAccess.RequiresOwnership(user.Roles, context.Request.Method))
            {
                await CheckOwnershipAsync(context, db, user.Id, ct);
            }

            if (!AppAccess.CanReadAdministration(user.Roles) && context.Request.Path.StartsWithSegments("/api/state/indexed-files"))
            {
                var driveId = context.Request.RouteValues["driveId"]?.ToString();
                var itemId = context.Request.RouteValues["itemId"]?.ToString();
                if (driveId != await sharePoint.GetDriveIdAsync(ct) || string.IsNullOrWhiteSpace(itemId))
                {
                    throw new UserManagementException("Resource not found.", 404);
                }

                var permissions = await sharePoint.GetPermissionsAsync(itemId, ct);
                var principals = await sharePoint.GetUserPrincipalsAsync(objectId, ct);
                if (!permissions.HasAnonymousAccess && !permissions.AllowedPrincipals.Intersect(principals, StringComparer.OrdinalIgnoreCase).Any())
                {
                    throw new UserManagementException("Resource not found.", 404);
                }
            }
            await next(context);
        }
        catch (UserManagementException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = ex.Status;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message }, context.RequestAborted);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (!context.Response.HasStarted
            && !context.Items.ContainsKey(typeof(AppUserView)) && ex.ResponseStatusCode is 401 or 403)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "Cannot verify the user with Microsoft Graph. Configure User.Read.All application permission with admin consent." }, context.RequestAborted);
        }
    }

    private static async Task CheckOwnershipAsync(HttpContext context, SharePointIndexDbContext db, Guid owner, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.RouteValues.GetValueOrDefault("id")?.ToString(), out var id))
        {
            return;
        }

        var path = context.Request.Path;
        var owned = path.StartsWithSegments("/api/chat/workspaces")
            ? await db.ChatWorkspaces.AnyAsync(x => x.Id == id && x.CreatedById == owner, ct)
            : path.StartsWithSegments("/api/chat/conversations")
            ? await db.ChatConversations.AnyAsync(x => x.Id == id && x.CreatedById == owner, ct)
            : path.StartsWithSegments("/api/chat/messages")
                ? await db.ChatMessages.AnyAsync(x => x.Id == id && x.Conversation!.CreatedById == owner, ct)
                : path.StartsWithSegments("/api/attachment-files")
                    ? await db.ChatMessageAttachmentFiles.AnyAsync(x => x.Id == id && x.CreatedById == owner, ct)
                    : false;
        if (!owned)
        {
            throw new UserManagementException("Resource not found.", 404);
        }
    }
}

public static class AppAccess
{
    public static bool CanReadAdministration(IReadOnlyList<string> roles) =>
        roles.Contains(AppRoles.GlobalAdmin) || roles.Contains(AppRoles.GlobalReaderAdmin);

    public static bool RequiresOwnership(IReadOnlyList<string> roles, string method) =>
        !roles.Contains(AppRoles.GlobalAdmin) &&
        (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) || !CanReadAdministration(roles));

    public static bool Allows(IReadOnlyList<string> roles, string method, string path) =>
        roles.Any(role => AllowsRole(role, method, path));

    private static bool AllowsRole(string role, string method, string path)
    {
        path = path.TrimEnd('/').ToLowerInvariant();
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (role == AppRoles.GlobalAdmin)
        {
            return true;
        }

        if (role != AppRoles.GlobalReaderAdmin && role != AppRoles.User)
        {
            return false;
        }

        if (path == "/api/auth/me" && read)
        {
            return true;
        }

        var search = path is "/api/search/fulltext" or "/api/search/vector" or "/api/search/hybrid";
        if (search && HttpMethods.IsPost(method))
        {
            return true;
        }

        if (role == AppRoles.GlobalReaderAdmin)
        {
            if (path == "/api/admin/document-signing" || path.StartsWith("/api/admin/document-signing/") ||
                path == "/api/admin/site-permissions" || path.StartsWith("/api/admin/site-permissions/"))
            {
                return false;
            }
            return read;
        }

        if (role != AppRoles.User)
        {
            return false;
        }

        if (read && path.StartsWith("/api/state/indexed-files/") && (path.EndsWith("/content") || path.EndsWith("/markdown")))
        {
            return true;
        }

        if (path == "/api/agents" && read)
        {
            return true; // Available agents for the chat selector.
        }

        return path == "/api/chat/workspaces" || path.StartsWith("/api/chat/workspaces/")
            || path == "/api/chat/conversations" || path.StartsWith("/api/chat/conversations/")
            || (path.StartsWith("/api/chat/messages/") && path.EndsWith("/feedback") && HttpMethods.IsPost(method))
            || path == "/api/attachment-files" || path.StartsWith("/api/attachment-files/")
            || path == "/api/signing-templates" || path.StartsWith("/api/signing-templates/");
    }
}
