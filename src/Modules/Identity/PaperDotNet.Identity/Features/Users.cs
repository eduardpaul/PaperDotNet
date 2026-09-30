using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record UserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    bool IsAdmin,
    bool IsDisabled,
    DateTimeOffset CreatedAt,
    [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record MeDto(Guid Id, Guid TenantId, string UserName, string DisplayName, bool IsAdmin, IReadOnlyList<string> Scopes);

public sealed record CreateUserRequest(string UserName, string Password, string? DisplayName, bool IsAdmin = false);

/// <summary>User queries and endpoints. Queries copy their arguments into locals (precompiled queries, ADR-0039).</summary>
internal static class Users
{
    public const int MinPasswordLength = 8;

    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();

    public static UserDto ToDto(User user) =>
        new(user.Id, user.UserName, user.DisplayName, user.IsAdmin, user.IsDisabled, user.CreatedAt, ETags.From(user.Version));

    public static async Task<User?> FindForSignInAsync(IdentityDbContext database, string tenantIdentifier, string userName, CancellationToken cancellationToken)
    {
        var db = database;
        var identifier = tenantIdentifier;
        var normalized = Normalize(userName);
        var ct = cancellationToken;
        var tenantId = await db.Tenants.Where(t => t.Identifier == identifier).Select(t => t.Id).FirstOrDefaultAsync(ct);
        return tenantId == Guid.Empty
            ? null
            : await db.Users.Where(u => u.TenantId == tenantId && u.NormalizedUserName == normalized).FirstOrDefaultAsync(ct);
    }

    public static Task<User?> FindAsync(IdentityDbContext database, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = userId;
        var ct = cancellationToken;
        return db.Users.Where(u => u.TenantId == tenant && u.Id == id).FirstOrDefaultAsync(ct);
    }

    public static Task<bool> ExistsAsync(IdentityDbContext database, Guid tenantId, string normalizedUserName, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var normalized = normalizedUserName;
        var ct = cancellationToken;
        return db.Users.AnyAsync(u => u.TenantId == tenant && u.NormalizedUserName == normalized, ct);
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0").WithTags("Users");
        group.MapGet("/me", GetMeAsync).RequireAuthorization().WithName("GetMe");
        group.MapGet("/users", ListAsync).RequireScope(Scopes.UsersManage).WithName("ListUsers");
        group.MapPost("/users", CreateAsync).RequireScope(Scopes.UsersManage).WithName("CreateUser");
    }

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> GetMeAsync(Caller caller, IdentityDbContext db, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, caller.UserId, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        return TypedResults.Ok(new MeDto(user.Id, user.TenantId, user.UserName, user.DisplayName, user.IsAdmin, Scopes.For(user.IsAdmin).Split(' ')));
    }

    private static async Task<Ok<Page<UserDto>>> ListAsync(HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        List<User> users;
        if (page.After is { } after)
        {
            users = await db.Users.Where(u => u.TenantId == tenant && u.Id.CompareTo(after) > 0).OrderBy(u => u.Id).Take(take).ToListAsync(ct);
        }
        else
        {
            users = await db.Users.Where(u => u.TenantId == tenant).OrderBy(u => u.Id).Take(take).ToListAsync(ct);
        }

        return TypedResults.Ok(Page.Create([.. users.Select(ToDto)], page, request, u => u.Id));
    }

    private static async Task<Results<Created<UserDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateUserRequest body, Caller caller, IdentityDbContext db, IPasswordHasher<User> hasher, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.UserName) || body.UserName.Length > 256)
        {
            return ApiErrors.Validation("userName", "A user name of up to 256 characters is required.");
        }

        if (body.Password is null || body.Password.Length < MinPasswordLength)
        {
            return ApiErrors.Validation("password", $"The password needs at least {MinPasswordLength} characters.");
        }

        var normalized = Normalize(body.UserName);
        if (await ExistsAsync(db, caller.TenantId, normalized, cancellationToken))
        {
            return ApiErrors.Conflict("userExists", "A user with this name exists.");
        }

        var user = New(caller.TenantId, body.UserName.Trim(), body.DisplayName, body.IsAdmin);
        user.PasswordHash = hasher.HashPassword(user, body.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/users/{user.Id}", ToDto(user));
    }

    public static User New(Guid tenantId, string userName, string? displayName, bool isAdmin) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        UserName = userName,
        NormalizedUserName = Normalize(userName),
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
        IsAdmin = isAdmin,
        SecurityStamp = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
        CreatedAt = TimeProvider.System.GetUtcNow(),
    };
}

