using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using OpenIddict.Abstractions;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;
using PaperDotNet.Messaging;

namespace PaperDotNet.Identity.Features;

public sealed record UpdateUserRequest(
    [property: StringLength(200)] string? DisplayName,
    [property: EmailAddress, StringLength(256)] string? Email,
    bool? IsDisabled);

public sealed record SetPasswordRequest([property: Required, StringLength(256, MinimumLength = 1)] string Password);

public sealed record ChangePasswordRequest(
    [property: Required, StringLength(256, MinimumLength = 1)] string CurrentPassword,
    [property: Required, StringLength(256, MinimumLength = 1)] string NewPassword);

public sealed record UpdateGroupRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Name,
    [property: StringLength(1000)] string? Description);

public sealed record UpdateRoleRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Name,
    [property: StringLength(1000)] string? Description,
    IReadOnlyList<string>? Scopes);

/// <summary>
/// Account lifecycle (IAM-14): update, disable, delete and reset users; change your password; rename and delete
/// groups and roles; remove role assignments. Nothing may leave the organization without an enabled administrator.
/// </summary>
internal static class AccountEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapV1Group("users", "Users");
        users.MapPatch("/{id:guid}", UpdateUserAsync).RequireScope(IdentityScopes.UserManage).WithName("UpdateUser");
        users.MapDelete("/{id:guid}", DeleteUserAsync).RequireScope(IdentityScopes.UserManage).WithName("DeleteUser");
        users.MapPost("/{id:guid}/password", SetPasswordAsync).RequireScope(IdentityScopes.UserManage).WithName("SetUserPassword");

        endpoints.MapV1Group("me", "Me").MapPost("/password", ChangePasswordAsync).WithName("ChangeMyPassword");

        var groups = endpoints.MapV1Group("groups", "Groups");
        groups.MapPatch("/{id:guid}", UpdateGroupAsync).RequireScope(IdentityScopes.GroupManage).WithName("UpdateGroup");
        groups.MapDelete("/{id:guid}", DeleteGroupAsync).RequireScope(IdentityScopes.GroupManage).WithName("DeleteGroup");

        var roles = endpoints.MapV1Group("roles", "Roles");
        roles.MapPatch("/{id:guid}", UpdateRoleAsync).RequireScope(IdentityScopes.RoleManage).WithName("UpdateRole");
        roles.MapDelete("/{id:guid}", DeleteRoleAsync).RequireScope(IdentityScopes.RoleManage).WithName("DeleteRole");
        roles.MapDelete("/{id:guid}/assignments/{assignmentId:guid}", RemoveAssignmentAsync)
            .RequireScope(IdentityScopes.RoleManage).WithName("RemoveRoleAssignment");
    }

    // ---- Users ----------------------------------------------------------------------

    private static async Task<Results<Ok<UserResponse>, ValidationProblem, ProblemHttpResult>> UpdateUserAsync(
        Guid id, UpdateUserRequest request, IdentityDbContext db, UserManager<User> userManager, ICurrentUser current,
        AccountSessions sessions, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null && !u.IsServiceAccount, ct);
        if (user is null)
        {
            return ApiErrors.NotFound();
        }

        var disabling = request.IsDisabled == true && !user.IsDisabled;
        if (disabling && id == current.UserId)
        {
            return ApiErrors.Conflict("cannotChangeSelf", "You cannot disable your own account.");
        }

        if (disabling && !await AdministratorGuard.RemainsAsync(db, withoutUser: id, ct: ct))
        {
            return LastAdministrator();
        }

        if (request.DisplayName is { } name)
        {
            user.DisplayName = name.Trim().Length == 0 ? user.UserName : name.Trim();
        }

        if (request.Email is { } email)
        {
            user.Email = email.Trim().Length == 0 ? null : email.Trim();
            user.NormalizedEmail = user.Email is null ? null : userManager.NormalizeEmail(user.Email);
        }

        user.IsDisabled = request.IsDisabled ?? user.IsDisabled;
        await db.SaveChangesAsync(ct);
        if (disabling)
        {
            await sessions.EndAsync(user, revokeApiTokens: true, ct);
        }
        else
        {
            await sessions.InvalidateScopesAsync(ct);
        }

        return TypedResults.Ok(ToResponse(user));
    }

    /// <summary>
    /// Deletes a user: the row stays so that authors and person values still resolve, but it is anonymized
    /// (user name <c>deleted-{id}</c>, no e-mail, password or passkeys), leaves its groups and roles, and every
    /// token and session ends. Other modules remove its grants and memberships (<see cref="PrincipalDeleted"/>).
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteUserAsync(
        Guid id, IdentityDbContext db, UserManager<User> userManager, ICurrentUser current, ITenantContext tenant,
        AccountSessions sessions, IOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null && !u.IsServiceAccount, ct);
        if (user is null)
        {
            return ApiErrors.NotFound();
        }

        if (id == current.UserId)
        {
            return ApiErrors.Conflict("cannotChangeSelf", "You cannot delete your own account.");
        }

        if (!await AdministratorGuard.RemainsAsync(db, withoutUser: id, ct: ct))
        {
            return LastAdministrator();
        }

        user.DeletedAt = time.GetUtcNow();
        user.IsDisabled = true;
        user.UserName = $"deleted-{id:N}";
        user.NormalizedUserName = userManager.NormalizeName(user.UserName);
        user.Email = null;
        user.NormalizedEmail = null;
        user.PasswordHash = null;
        user.PhoneNumber = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        db.GroupMembers.RemoveRange(await db.GroupMembers.Where(m => m.UserId == id).ToListAsync(ct));
        db.RoleAssignments.RemoveRange(await db.RoleAssignments.Where(a => a.PrincipalType == PrincipalType.User && a.PrincipalId == id).ToListAsync(ct));
        db.Preferences.RemoveRange(await db.Preferences.Where(p => p.UserId == id).ToListAsync(ct));
        db.RemoveRange(await db.Set<IdentityUserPasskey<Guid>>().Where(p => p.UserId == id).ToListAsync(ct));
        db.RemoveRange(await db.Set<IdentityUserLogin<Guid>>().Where(l => l.UserId == id).ToListAsync(ct));
        await outbox.SaveChangesAsync(db, [Deleted(tenant, current, id, isGroup: false)], cancellationToken: ct);
        await sessions.EndAsync(user, revokeApiTokens: true, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Sets a new password (admin reset): unlocks the account and ends its sessions and OAuth tokens.</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetPasswordAsync(
        Guid id, SetPasswordRequest request, IdentityDbContext db, UserManager<User> userManager, AccountSessions sessions, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null || user.DeletedAt is not null || user.IsServiceAccount)
        {
            return ApiErrors.NotFound();
        }

        var errors = new List<string>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, user, request.Password);
            errors.AddRange(result.Errors.Select(e => e.Description));
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["password"] = [.. errors] });
        }

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, request.Password);
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        await userManager.UpdateSecurityStampAsync(user);
        await sessions.EndAsync(user, revokeApiTokens: false, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Changes the caller's password; other sessions and OAuth tokens end (API tokens stay).</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request, UserManager<User> userManager, ICurrentUser current, AccountSessions sessions, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var user = current.UserId is { } userId ? await userManager.FindByIdAsync(userId.ToString()) : null;
        if (user is null || user.IsServiceAccount)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Only users have passwords.");
        }

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            var key = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)) ? "currentPassword" : "newPassword";
            return ApiErrors.Validation(new Dictionary<string, string[]> { [key] = [.. result.Errors.Select(e => e.Description)] });
        }

        await sessions.EndAsync(user, revokeApiTokens: false, ct);
        return TypedResults.NoContent();
    }

    // ---- Groups ---------------------------------------------------------------------

    private static async Task<Results<Ok<GroupResponse>, ValidationProblem, ProblemHttpResult>> UpdateGroupAsync(
        Guid id, UpdateGroupRequest request, IdentityDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return ApiErrors.NotFound();
        }

        if (ETags.TryGetIfMatch(http, out var expected) && expected != group.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (request.Name?.Trim() is { } name && name != group.Name)
        {
            if (await db.Groups.AnyAsync(g => g.Name == name && g.Id != id, ct))
            {
                return ApiErrors.Conflict("nameAlreadyExists", $"A group named '{name}' already exists.");
            }

            group.Name = name;
        }

        if (request.Description is { } description)
        {
            group.Description = description.Length == 0 ? null : description;
        }

        await db.SaveChangesAsync(ct);
        ETags.Set(response, group.Version);
        return TypedResults.Ok(new GroupResponse(group.Id, group.Name, group.Description, group.CreatedAt) { ETag = ETags.From(group.Version) });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteGroupAsync(
        Guid id, IdentityDbContext db, ICurrentUser current, ITenantContext tenant, AccountSessions sessions, IOutbox outbox, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await AdministratorGuard.RemainsAsync(db, withoutGroup: id, ct: ct))
        {
            return LastAdministrator();
        }

        db.Groups.Remove(group);
        db.GroupMembers.RemoveRange(await db.GroupMembers.Where(m => m.GroupId == id).ToListAsync(ct));
        db.RoleAssignments.RemoveRange(await db.RoleAssignments.Where(a => a.PrincipalType == PrincipalType.Group && a.PrincipalId == id).ToListAsync(ct));
        await outbox.SaveChangesAsync(db, [Deleted(tenant, current, id, isGroup: true)], cancellationToken: ct);
        await sessions.InvalidateScopesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---- Roles ----------------------------------------------------------------------

    private static async Task<Results<Ok<RoleResponse>, ValidationProblem, ProblemHttpResult>> UpdateRoleAsync(
        Guid id, UpdateRoleRequest request, IdentityDbContext db, IScopeCatalog catalog, AccountSessions sessions,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return ApiErrors.NotFound();
        }

        if (ETags.TryGetIfMatch(http, out var expected) && expected != role.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        var name = request.Name?.Trim();
        if (role.IsBuiltIn && ((name is not null && name != role.Name) || (role.GrantsAllScopes && request.Scopes is not null)))
        {
            return ApiErrors.Conflict("builtInRole", "Built-in roles cannot be renamed, and the Administrator role always has every scope.");
        }

        if (request.Scopes?.Where(s => !catalog.Contains(s)).ToList() is { Count: > 0 } unknown)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["scopes"] = [$"Unknown scopes: {string.Join(", ", unknown)}."] });
        }

        if (name is not null && name != role.Name)
        {
            if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct))
            {
                return ApiErrors.Conflict("nameAlreadyExists", $"A role named '{name}' already exists.");
            }

            role.Name = name;
        }

        if (request.Description is { } description)
        {
            role.Description = description.Length == 0 ? null : description;
        }

        if (request.Scopes is { } scopes)
        {
            role.Scopes = [.. scopes.Distinct()];
        }

        await db.SaveChangesAsync(ct);
        await sessions.InvalidateScopesAsync(ct);
        ETags.Set(response, role.Version);
        return TypedResults.Ok(new RoleResponse(role.Id, role.Name, role.Description, role.IsBuiltIn, role.GrantsAllScopes, role.Scopes) { ETag = ETags.From(role.Version) });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteRoleAsync(
        Guid id, IdentityDbContext db, AccountSessions sessions, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return ApiErrors.NotFound();
        }

        if (role.IsBuiltIn)
        {
            return ApiErrors.Conflict("builtInRole", "Built-in roles cannot be deleted.");
        }

        db.Roles.Remove(role);
        db.RoleAssignments.RemoveRange(await db.RoleAssignments.Where(a => a.RoleId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        await sessions.InvalidateScopesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAssignmentAsync(
        Guid id, Guid assignmentId, IdentityDbContext db, AccountSessions sessions, CancellationToken ct)
    {
        var assignment = await db.RoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId && a.RoleId == id, ct);
        if (assignment is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await AdministratorGuard.RemainsAsync(db, withoutAssignment: assignmentId, ct: ct))
        {
            return LastAdministrator();
        }

        db.RoleAssignments.Remove(assignment);
        await db.SaveChangesAsync(ct);
        await sessions.InvalidateScopesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---- Helpers --------------------------------------------------------------------

    internal static ProblemHttpResult LastAdministrator() =>
        ApiErrors.Conflict("lastAdministrator", "The organization would be left without an enabled administrator.");

    private static PrincipalDeleted Deleted(ITenantContext tenant, ICurrentUser current, Guid id, bool isGroup) => new()
    {
        TenantId = tenant.TenantId!.Value,
        TenantIdentifier = tenant.TenantIdentifier!,
        UserId = current.UserId,
        PrincipalId = id,
        IsGroup = isGroup,
    };

    private static UserResponse ToResponse(User u) => new(u.Id, u.UserName, u.DisplayName, u.Email, u.IsDisabled, u.CreatedAt);
}

/// <summary>Checks that enabled administrators remain after a change (IAM-14).</summary>
internal static class AdministratorGuard
{
    /// <summary>
    /// True when at least one enabled user still holds a role that grants every scope once the given user,
    /// assignment, group or membership is gone.
    /// </summary>
    public static async Task<bool> RemainsAsync(
        IdentityDbContext db,
        Guid? withoutUser = null,
        Guid? withoutAssignment = null,
        Guid? withoutGroup = null,
        (Guid Group, Guid User)? withoutMembership = null,
        CancellationToken ct = default)
    {
        var assignments = await db.RoleAssignments.AsNoTracking()
            .Where(a => db.Roles.Any(r => r.Id == a.RoleId && r.GrantsAllScopes) && a.Id != withoutAssignment)
            .ToListAsync(ct);
        assignments.RemoveAll(a => a.PrincipalType == PrincipalType.Group && a.PrincipalId == withoutGroup);

        var userIds = assignments.Where(a => a.PrincipalType == PrincipalType.User).Select(a => a.PrincipalId).ToHashSet();
        var groupIds = assignments.Where(a => a.PrincipalType == PrincipalType.Group).Select(a => a.PrincipalId).ToList();
        var members = await db.GroupMembers.AsNoTracking().Where(m => groupIds.Contains(m.GroupId)).ToListAsync(ct);
        userIds.UnionWith(members
            .Where(m => withoutMembership is not { } gone || m.GroupId != gone.Group || m.UserId != gone.User)
            .Select(m => m.UserId));
        if (withoutUser is { } user)
        {
            userIds.Remove(user);
        }

        return await db.Users.AnyAsync(u => userIds.Contains(u.Id) && !u.IsDisabled && !u.IsServiceAccount && u.DeletedAt == null, ct);
    }
}

/// <summary>Ends a user's sessions and tokens and drops cached scopes after account changes.</summary>
internal sealed class AccountSessions(
    IdentityDbContext db,
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations,
    ITenantContext tenant,
    TimeProvider time,
    HybridCache cache)
{
    public async Task EndAsync(User user, bool revokeApiTokens, CancellationToken ct)
    {
        var subject = user.Id.ToString();
        await tokens.RevokeBySubjectAsync(subject, ct);
        await authorizations.RevokeBySubjectAsync(subject, ct);
        if (revokeApiTokens)
        {
            var now = time.GetUtcNow();
            await db.ApiTokens.Where(t => t.TenantId == tenant.TenantId && t.UserId == user.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        }

        await InvalidateScopesAsync(ct);
    }

    public Task InvalidateScopesAsync(CancellationToken ct) =>
        cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenant.TenantId!.Value), ct).AsTask();
}
