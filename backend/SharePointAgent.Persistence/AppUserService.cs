using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SharePointAgent.Domain;

namespace SharePointAgent.Persistence;

public sealed class UserManagementException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class AppUserService(SharePointIndexDbContext db, UserManager<ApplicationUser> users)
{
    // Serialize binding, provisioning and admin changes across API replicas. Unique indexes are the
    // final guard against duplicate email/external identities. Role/user writes share one transaction.
    private async Task<IDbContextTransaction> BeginAsync(CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        try
        {
            if (db.Database.IsSqlServer())
            {
                await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sp_getapplock @Resource = 'SharePointAgent.Users', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; IF @result < 0 THROW 51000, 'User management lock unavailable.', 1;", ct);
            }

            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public async Task<AppUserView> RecordSignInAsync(AppUserView user, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.LastLoginAtUtc, now), ct);
        return user with { LastLoginAtUtc = now };
    }

    public async Task<SystemAttachmentStorage> GetSystemAttachmentStorageAsync(CancellationToken ct)
    {
        var files = db.ChatMessageAttachmentFiles.AsNoTracking();
        var totals = await files.GroupBy(x => 1).Select(group => new
        {
            FileCount = group.LongCount(),
            UsedBytes = group.Sum(x => x.SizeBytes),
            UnassignedBytes = group.Sum(x => x.CreatedById == null ? x.SizeBytes : 0)
        }).SingleOrDefaultAsync(ct);
        var orphanBytes = await files.Where(x => x.ChatMessageAttachmentId == null && !x.MessageAttachments.Any())
            .SumAsync(x => (long?)x.SizeBytes, ct) ?? 0;
        return new(totals?.FileCount ?? 0, totals?.UsedBytes ?? 0, orphanBytes, totals?.UnassignedBytes ?? 0);
    }

    public async Task<AppUserView?> FindBoundAsync(string tenant, string objectId, CancellationToken ct)
    {
        var user = await users.Users.SingleOrDefaultAsync(x => x.EntraTenantId == tenant && x.EntraObjectId == objectId, ct);
        if (user is null)
        {
            return null;
        }

        EnsureActive(user);
        return await ViewAsync(user);
    }

    public async Task<AppUserView> LinkEntraAccountAsync(string tenant, string objectId, string email, string displayName, CancellationToken ct)
    {
        ValidateEmail(email);
        await using var transaction = await BeginAsync(ct);
        var user = await users.Users.SingleOrDefaultAsync(x => x.EntraTenantId == tenant && x.EntraObjectId == objectId, ct);
        if (user is null)
        {
            user = await users.FindByEmailAsync(email.Trim());
            if (user is not null && (user.EntraTenantId is not null || user.EntraObjectId is not null))
            {
                throw new UserManagementException("This email is already linked to another Entra account. Contact an administrator.", 403);
            }

            if (user is null)
            {
                user = NewUser(email, displayName);
                Check(await users.CreateAsync(user));
                Check(await users.AddToRoleAsync(user, AppRoles.User));
            }
            EnsureActive(user);
            user.EntraTenantId = tenant;
            user.EntraObjectId = objectId;
            user.EmailConfirmed = true;
        }
        EnsureActive(user);
        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        Check(await users.UpdateAsync(user));
        await transaction.CommitAsync(ct);
        return await ViewAsync(user);
    }

    public async Task SeedAdminAsync(string email, CancellationToken ct)
    {
        ValidateEmail(email);
        await using var transaction = await BeginAsync(ct);
        // Configuration creates missing records only; it never re-enables or re-promotes an account.
        if (await users.FindByEmailAsync(email.Trim()) is null)
        {
            var user = NewUser(email, email.Trim());
            Check(await users.CreateAsync(user));
            Check(await users.AddToRoleAsync(user, AppRoles.GlobalAdmin));
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<PagedResult<AppUserView>> ListAsync(string? search, int skip, int top, CancellationToken ct)
    {
        var query = users.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = users.NormalizeEmail(search.Trim())!;
            query = query.Where(x => x.NormalizedEmail!.Contains(term) || x.DisplayName.Contains(search.Trim()));
        }
        var count = await query.LongCountAsync(ct);
        var page = await query.OrderBy(x => x.NormalizedEmail).Skip(skip).Take(top).ToListAsync(ct);
        var result = new List<AppUserView>();
        foreach (var user in page)
        {
            result.Add(await ViewAsync(user));
        }

        return new(count, result);
    }

    public async Task<AppUserView> SaveAsync(Guid? id, AppUserInput input, CancellationToken ct)
    {
        ValidateEmail(input.Email);
        if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Trim().Length > 200)
        {
            throw new UserManagementException("Display name is required and must be at most 200 characters.");
        }

        if (input.Roles is null || input.Roles.Count == 0 || input.Roles.Any(role => !AppRoles.All.Contains(role)))
        {
            throw new UserManagementException("Select at least one supported application role.");
        }

        var requestedRoles = input.Roles.Distinct(StringComparer.Ordinal).ToArray();
        await using var transaction = await BeginAsync(ct);
        ApplicationUser user;
        if (id is null)
        {
            user = NewUser(input.Email, input.DisplayName);
            user.IsActive = input.IsActive;
            Check(await users.CreateAsync(user));
        }
        else
        {
            user = await users.FindByIdAsync(id.ToString()!) ?? throw new UserManagementException("User not found.", 404);
            if (input.ConcurrencyStamp != user.ConcurrencyStamp)
            {
                throw new UserManagementException("This user changed. Refresh the list and try again.", 409);
            }

            if (user.EntraObjectId is not null && users.NormalizeEmail(input.Email.Trim()) != user.NormalizedEmail)
            {
                throw new UserManagementException("The email of a linked account cannot be changed here.");
            }

            if (user.IsActive && await users.IsInRoleAsync(user, AppRoles.GlobalAdmin) && (!input.IsActive || !requestedRoles.Contains(AppRoles.GlobalAdmin)))
            {
                var admins = await users.GetUsersInRoleAsync(AppRoles.GlobalAdmin);
                if (!admins.Any(x => x.Id != user.Id && x.IsActive))
                {
                    throw new UserManagementException("At least one active Global Admin must remain.", 409);
                }
            }
            user.Email = input.Email.Trim();
            user.UserName = user.Email;
            user.DisplayName = input.DisplayName.Trim();
            user.IsActive = input.IsActive;
            Check(await users.UpdateAsync(user));
        }
        var roles = await users.GetRolesAsync(user);
        var removedRoles = roles.Except(requestedRoles).ToArray();
        var addedRoles = requestedRoles.Except(roles).ToArray();
        if (removedRoles.Length > 0)
        {
            Check(await users.RemoveFromRolesAsync(user, removedRoles));
        }

        if (addedRoles.Length > 0)
        {
            Check(await users.AddToRolesAsync(user, addedRoles));
        }

        await transaction.CommitAsync(ct);
        return await ViewAsync(user);
    }

    public async Task<AppUserView> SaveStorageAsync(Guid id, AppUserStorageInput input, CancellationToken ct)
    {
        if (input.AttachmentStorageLimitBytes is < 0 or > 9_007_199_254_740_991)
        {
            throw new UserManagementException("Storage limit must be a non-negative number of bytes within the supported range.");
        }

        await using var transaction = await BeginAsync(ct);
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new UserManagementException("User not found.", 404);
        if (string.IsNullOrEmpty(input.ConcurrencyStamp) || input.ConcurrencyStamp != user.ConcurrencyStamp)
        {
            throw new UserManagementException("This user changed. Refresh the list and try again.", 409);
        }

        user.AttachmentStorageLimitBytes = input.AttachmentStorageLimitBytes;
        Check(await users.UpdateAsync(user));
        await transaction.CommitAsync(ct);
        return await ViewAsync(user);
    }

    public async Task<AppUserView> SaveTokenLimitAsync(Guid id, AppUserTokenLimitInput input, CancellationToken ct)
    {
        if (input.MonthlyTokenLimit is < 0 or > 9_007_199_254_740_991)
        {
            throw new UserManagementException("Monthly token limit must be a non-negative whole number within the supported range.");
        }

        await using var transaction = await BeginAsync(ct);
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new UserManagementException("User not found.", 404);
        if (string.IsNullOrEmpty(input.ConcurrencyStamp) || input.ConcurrencyStamp != user.ConcurrencyStamp)
        {
            throw new UserManagementException("This user changed. Refresh the list and try again.", 409);
        }

        user.MonthlyTokenLimit = input.MonthlyTokenLimit;
        Check(await users.UpdateAsync(user));
        await transaction.CommitAsync(ct);
        return await ViewAsync(user);
    }

    private async Task<AppUserView> ViewAsync(ApplicationUser user)
    {
        var now = DateTimeOffset.UtcNow;
        var month = MonthlyTokenQuota.MonthKey(now);
        var dailyModels = await db.UserTokenUsage.Where(x => x.UserId == user.Id && x.Month == month)
            .GroupBy(x => new { x.Day, x.ModelId }).OrderBy(x => x.Key.Day).ThenBy(x => x.Key.ModelId)
            .Select(x => new DailyModelTokenUsage(x.Key.Day, x.Key.ModelId, x.Sum(t => t.InputTokens), x.Sum(t => t.OutputTokens), x.Sum(t => t.TotalTokens)))
            .ToListAsync();
        var daily = dailyModels.GroupBy(x => x.Day)
            .Select(x => new DailyTokenUsage(x.Key, x.Sum(t => t.InputTokens), x.Sum(t => t.OutputTokens), x.Sum(t => t.TotalTokens))).ToArray();
        return new(user.Id, user.Email!, user.DisplayName,
        (await users.GetRolesAsync(user)).OrderBy(role => Array.IndexOf(AppRoles.All, role)).ToArray(), user.IsActive, user.EntraObjectId is not null,
        user.CreatedAtUtc, user.LastLoginAtUtc, user.ConcurrencyStamp!, user.AttachmentStorageLimitBytes,
        await db.ChatMessageAttachmentFiles.Where(x => x.CreatedById == user.Id).SumAsync(x => (long?)x.SizeBytes) ?? 0,
        user.MonthlyTokenLimit,
        daily.Sum(x => x.TotalTokens), MonthlyTokenQuota.NextMonth(now), daily, dailyModels);
    }

    private static ApplicationUser NewUser(string email, string name) => new()
    {
        Id = Guid.NewGuid(), UserName = email.Trim(), Email = email.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(name) ? email.Trim() : name.Trim()[..Math.Min(name.Trim().Length, 200)]
    };
    private static void EnsureActive(ApplicationUser user)
    {
        if (!user.IsActive)
        {
            throw new UserManagementException("Your application account is disabled. Contact an administrator.", 403);
        }
    }
    private static void ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 254 || !new EmailAddressAttribute().IsValid(email.Trim()))
        {
            throw new UserManagementException("A valid email address is required.");
        }
    }
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new UserManagementException(string.Join(" ", result.Errors.Select(x => x.Description)),
            result.Errors.Any(x => x.Code.Contains("Duplicate") || x.Code == "ConcurrencyFailure") ? 409 : 400);
        }
    }
}
