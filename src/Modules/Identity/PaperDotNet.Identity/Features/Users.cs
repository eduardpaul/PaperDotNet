using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;
using PaperDotNet.Messaging;

namespace PaperDotNet.Identity.Features;

public sealed record UserResponse(Guid Id, string UserName, string DisplayName, string? Email, bool IsDisabled, DateTimeOffset CreatedAt);

public sealed record CreateUserRequest(string? UserName, string? Password, string? DisplayName, string? Email);

/// <summary>Changes to a user; an empty e-mail removes it, an empty display name falls back to the user name.</summary>
public sealed record UpdateUserRequest(string? DisplayName, string? Email, bool? IsDisabled);

public sealed record SetPasswordRequest(string? Password);

/// <summary>
/// Users of the organization and their lifecycle (IAM-14): create, update, disable, delete (anonymized) and reset
/// passwords. Nothing may leave the organization without an enabled administrator. Queries copy their arguments
/// into locals (precompiled queries, ADR-0039).
/// </summary>
internal static class Users
{
    public const int MinPasswordLength = 8;

    /// <summary>Failed sign-ins in a row that lock an account for <see cref="LockoutDuration"/>.</summary>
    public const int MaxFailedSignIns = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();

    public static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static UserResponse ToResponse(User user) => new(user.Id, user.UserName, user.DisplayName, user.Email, user.IsDisabled, user.CreatedAt);

    /// <summary>Why a password is not accepted, or null.</summary>
    public static string? CheckPassword(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"The password needs at least {MinPasswordLength} characters."
        : password.Length > 256 ? "The password can have at most 256 characters."
        : null;

    /// <summary>A trimmed e-mail address, null for an empty one, or false when it is not an address.</summary>
    public static bool TryEmail(string? value, out string? email)
    {
        email = value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        return email is null || (email.Length <= 256 && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email);
    }

    public static User New(Guid tenantId, string userName, string? displayName, string? email, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        UserName = userName.Trim(),
        NormalizedUserName = Normalize(userName),
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
        Email = email,
        SecurityStamp = NewSecurityStamp(),
        CreatedAt = now,
    };

    public static async Task<User?> FindForSignInAsync(IdentityDbContext database, string tenantIdentifier, string userName, CancellationToken cancellationToken)
    {
        var db = database;
        var identifier = tenantIdentifier;
        var normalized = Normalize(userName);
        var active = TenantStatuses.Active;
        var ct = cancellationToken;
        var tenantId = await db.Tenants.Where(t => t.Identifier == identifier && t.Status == active).Select(t => t.Id).FirstOrDefaultAsync(ct);
        return tenantId == Guid.Empty
            ? null
            : await db.Users.Where(u => u.TenantId == tenantId && u.NormalizedUserName == normalized && u.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    /// <summary>A user that is not deleted.</summary>
    public static Task<User?> FindAsync(IdentityDbContext database, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = userId;
        var ct = cancellationToken;
        return db.Users.Where(u => u.TenantId == tenant && u.Id == id && u.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    public static Task<bool> ExistsAsync(IdentityDbContext database, Guid tenantId, string normalizedUserName, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var normalized = normalizedUserName;
        var ct = cancellationToken;
        return db.Users.AnyAsync(u => u.TenantId == tenant && u.NormalizedUserName == normalized, ct);
    }

    /// <summary>Ends a user's sessions (refresh tokens and, through the security stamp, access tokens) and optionally API tokens.</summary>
    public static async Task EndSessionsAsync(IdentityDbContext database, User user, bool revokeApiTokens, DateTimeOffset now, CancellationToken cancellationToken)
    {
        user.SecurityStamp = NewSecurityStamp();
        if (revokeApiTokens)
        {
            var db = database;
            var tenant = user.TenantId;
            var id = user.Id;
            var ct = cancellationToken;
            foreach (var token in await db.ApiTokens.Where(t => t.TenantId == tenant && t.UserId == id && t.RevokedAt == null).ToListAsync(ct))
            {
                token.RevokedAt = now;
            }
        }
    }

    internal static ProblemHttpResult LastAdministrator() =>
        ApiErrors.Conflict("lastAdministrator", "The organization would be left without an enabled administrator.");

    public static void Map(IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/v1.0/users").WithTags("Users");
        users.MapGet("", ListAsync).RequireScope(IdentityScopes.UserRead).WithName("ListUsers");
        users.MapGet("/{id:guid}", GetAsync).RequireScope(IdentityScopes.UserRead).WithName("GetUser");
        users.MapPost("", CreateAsync).RequireScope(IdentityScopes.UserManage).WithName("CreateUser");
        users.MapPatch("/{id:guid}", UpdateAsync).RequireScope(IdentityScopes.UserManage).WithName("UpdateUser");
        users.MapDelete("/{id:guid}", DeleteAsync).RequireScope(IdentityScopes.UserManage).WithName("DeleteUser");
        users.MapPost("/{id:guid}/password", SetPasswordAsync).RequireScope(IdentityScopes.UserManage).WithName("SetUserPassword");
    }

    private static async Task<Ok<Page<UserResponse>>> ListAsync(HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        List<User> users;
        if (page.After is { } after)
        {
            users = await db.Users.Where(u => u.TenantId == tenant && u.DeletedAt == null && u.Id.CompareTo(after) > 0).OrderBy(u => u.Id).Take(take).ToListAsync(ct);
        }
        else
        {
            users = await db.Users.Where(u => u.TenantId == tenant && u.DeletedAt == null).OrderBy(u => u.Id).Take(take).ToListAsync(ct);
        }

        return TypedResults.Ok(Page.Create([.. users.Select(ToResponse)], page, request, u => u.Id));
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> GetAsync(Guid id, Caller caller, IdentityDbContext db, CancellationToken cancellationToken) =>
        await FindAsync(db, caller.TenantId, id, cancellationToken) is { } user ? TypedResults.Ok(ToResponse(user)) : ApiErrors.NotFound();

    private static async Task<Results<Created<UserResponse>, ValidationProblem>> CreateAsync(
        CreateUserRequest body, Caller caller, IUserDirectory directory, IdentityDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var id = await directory.CreateUserAsync(caller.TenantId, new NewUser(body.UserName ?? "", body.Password, body.DisplayName, body.Email), cancellationToken);
            var user = await FindAsync(db, caller.TenantId, id, cancellationToken);
            return TypedResults.Created($"/v1.0/users/{id}", ToResponse(user!));
        }
        catch (UserCreationException ex)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["user"] = [.. ex.Errors] });
        }
    }

    private static async Task<Results<Ok<UserResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateUserRequest body, Caller caller, IdentityDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (body.DisplayName is { Length: > 256 })
        {
            return ApiErrors.Validation("displayName", "The display name can have at most 256 characters.");
        }

        if (!TryEmail(body.Email, out var email))
        {
            return ApiErrors.Validation("email", "The e-mail field is not a valid e-mail address.");
        }

        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        var disabling = body.IsDisabled == true && !user.IsDisabled;
        if (disabling && id == caller.UserId)
        {
            return ApiErrors.Conflict("cannotChangeSelf", "You cannot disable your own account.");
        }

        if (disabling && !await AdministratorGuard.RemainsAsync(db, caller.TenantId, withoutUser: id, cancellationToken: cancellationToken))
        {
            return LastAdministrator();
        }

        if (body.DisplayName is { } name)
        {
            user.DisplayName = name.Trim().Length == 0 ? user.UserName : name.Trim();
        }

        if (body.Email is not null)
        {
            user.Email = email;
        }

        user.IsDisabled = body.IsDisabled ?? user.IsDisabled;
        if (disabling)
        {
            await EndSessionsAsync(db, user, revokeApiTokens: true, time.GetUtcNow(), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(user));
    }

    /// <summary>
    /// Deletes a user: the row stays so that authors and person values still resolve, but it is anonymized (user
    /// name <c>deleted-{id}</c>, no e-mail or password), leaves its groups and roles, and every token and session
    /// ends. Other modules remove its grants and memberships (<see cref="PrincipalDeleted"/>).
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, Caller caller, IdentityDbContext database, IOutbox outbox, TimeProvider time, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        if (id == caller.UserId)
        {
            return ApiErrors.Conflict("cannotChangeSelf", "You cannot delete your own account.");
        }

        if (!await AdministratorGuard.RemainsAsync(db, caller.TenantId, withoutUser: id, cancellationToken: cancellationToken))
        {
            return LastAdministrator();
        }

        var now = time.GetUtcNow();
        user.DeletedAt = now;
        user.IsDisabled = true;
        user.UserName = $"deleted-{id:N}";
        user.NormalizedUserName = Normalize(user.UserName);
        user.DisplayName = "Deleted user";
        user.Email = null;
        user.PasswordHash = "";
        await EndSessionsAsync(db, user, revokeApiTokens: true, now, cancellationToken);
        var tenant = caller.TenantId;
        var userId = id;
        var userType = PrincipalTypes.User;
        var ct = cancellationToken;
        db.GroupMembers.RemoveRange(await db.GroupMembers.Where(m => m.TenantId == tenant && m.UserId == userId).ToListAsync(ct));
        db.RoleAssignments.RemoveRange(await db.RoleAssignments.Where(a => a.TenantId == tenant && a.PrincipalType == userType && a.PrincipalId == userId).ToListAsync(ct));
        db.Preferences.RemoveRange(await db.Preferences.Where(p => p.TenantId == tenant && p.UserId == userId).ToListAsync(ct));
        await outbox.SaveChangesAsync(db, [new PrincipalDeleted { TenantId = tenant, UserId = caller.UserId, PrincipalId = id, IsGroup = false }], ct);
        return TypedResults.NoContent();
    }

    /// <summary>Sets a new password (administrator reset): unlocks the account and ends its sessions (API tokens stay).</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetPasswordAsync(
        Guid id, SetPasswordRequest body, Caller caller, IdentityDbContext db, IPasswordHasher<User> hasher, TimeProvider time, CancellationToken cancellationToken)
    {
        if (CheckPassword(body.Password) is { } error)
        {
            return ApiErrors.Validation("password", error);
        }

        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        user.PasswordHash = hasher.HashPassword(user, body.Password!);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        await EndSessionsAsync(db, user, revokeApiTokens: false, time.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
