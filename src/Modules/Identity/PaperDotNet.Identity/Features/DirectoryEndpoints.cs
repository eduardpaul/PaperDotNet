using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record UserResponse(Guid Id, string? UserName, string? DisplayName, string? Email, bool IsDisabled, DateTimeOffset CreatedAt);

public sealed record CreateUserRequest(
    [property: Required, StringLength(256, MinimumLength = 1)] string UserName,
    [property: Required, StringLength(256, MinimumLength = 1)] string Password,
    [property: StringLength(200)] string? DisplayName,
    [property: EmailAddress, StringLength(256)] string? Email);

public sealed record GroupResponse(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt);

public sealed record CreateGroupRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(1000)] string? Description);

public sealed record AddGroupMemberRequest([property: Required] Guid UserId);

public sealed record RoleResponse(Guid Id, string Name, string? Description, bool IsBuiltIn, bool GrantsAllScopes, IReadOnlyList<string> Scopes);

public sealed record CreateRoleRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(1000)] string? Description,
    [property: Required] IReadOnlyList<string> Scopes);

public sealed record RoleAssignmentRequest([property: Required] Guid PrincipalId, PrincipalType PrincipalType);

public sealed record RoleAssignmentResponse(Guid Id, Guid RoleId, Guid PrincipalId, PrincipalType PrincipalType);

public sealed record ScopeResponse(string Name, string Description);

/// <summary>Users, groups, roles and the scope catalog of the organization.</summary>
internal static class DirectoryEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapV1Group("users", "Users");
        users.MapGet("", ListUsersAsync).RequireScope(IdentityScopes.UserRead).WithName("ListUsers");
        users.MapGet("/{id:guid}", GetUserAsync).RequireScope(IdentityScopes.UserRead).WithName("GetUser");
        users.MapPost("", CreateUserAsync).RequireScope(IdentityScopes.UserManage).WithName("CreateUser");

        var groups = endpoints.MapV1Group("groups", "Groups");
        groups.MapGet("", ListGroupsAsync).RequireScope(IdentityScopes.GroupRead).WithName("ListGroups");
        groups.MapPost("", CreateGroupAsync).RequireScope(IdentityScopes.GroupManage).WithName("CreateGroup");
        groups.MapGet("/{id:guid}/members", ListMembersAsync).RequireScope(IdentityScopes.GroupRead).WithName("ListGroupMembers");
        groups.MapPost("/{id:guid}/members", AddMemberAsync).RequireScope(IdentityScopes.GroupManage).WithName("AddGroupMember");
        groups.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync).RequireScope(IdentityScopes.GroupManage).WithName("RemoveGroupMember");

        var roles = endpoints.MapV1Group("roles", "Roles");
        roles.MapGet("", ListRolesAsync).RequireScope(IdentityScopes.RoleRead).WithName("ListRoles");
        roles.MapPost("", CreateRoleAsync).RequireScope(IdentityScopes.RoleManage).WithName("CreateRole");
        roles.MapGet("/{id:guid}/assignments", ListAssignmentsAsync).RequireScope(IdentityScopes.RoleRead).WithName("ListRoleAssignments");
        roles.MapPost("/{id:guid}/assignments", AssignRoleAsync).RequireScope(IdentityScopes.RoleManage).WithName("AssignRole");

        endpoints.MapV1Group("scopes", "Roles")
            .MapGet("", (IScopeCatalog catalog) =>
                TypedResults.Ok(catalog.All.OrderBy(s => s.Name).Select(s => new ScopeResponse(s.Name, s.Description)).ToList()))
            .WithName("ListScopes");
    }

    private static async Task<Ok<Page<UserResponse>>> ListUsersAsync(HttpRequest http, IdentityDbContext db, CancellationToken ct)
    {
        var page = PageRequest.From(http);
        var items = await db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null)
            .Where(u => page.After == null || u.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(u => u.Id)
            .Take(page.Top + 1)
            .Select(u => new UserResponse(u.Id, u.UserName, u.DisplayName, u.Email, u.IsDisabled, u.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(Page.Create(items, page, http, u => u.Id));
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> GetUserAsync(Guid id, IdentityDbContext db, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.DeletedAt == null)
            .Select(u => new UserResponse(u.Id, u.UserName, u.DisplayName, u.Email, u.IsDisabled, u.CreatedAt))
            .FirstOrDefaultAsync(ct);
        return user is null ? ApiErrors.NotFound() : TypedResults.Ok(user);
    }

    private static async Task<Results<Created<UserResponse>, ValidationProblem>> CreateUserAsync(
        CreateUserRequest request, IUserDirectory directory, IdentityDbContext db, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        try
        {
            var id = await directory.CreateUserAsync(new NewUser(request.UserName, request.Password, request.DisplayName, request.Email), ct);
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == id, ct);
            return TypedResults.Created(
                $"{ApiRoutes.V1}/users/{id}",
                new UserResponse(user.Id, user.UserName, user.DisplayName, user.Email, user.IsDisabled, user.CreatedAt));
        }
        catch (UserCreationException ex)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["user"] = [.. ex.Errors] });
        }
    }

    private static async Task<Ok<Page<GroupResponse>>> ListGroupsAsync(HttpRequest http, IdentityDbContext db, CancellationToken ct)
    {
        var page = PageRequest.From(http);
        var items = await db.Groups.AsNoTracking()
            .Where(g => page.After == null || g.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(g => g.Id)
            .Take(page.Top + 1)
            .Select(g => new GroupResponse(g.Id, g.Name, g.Description, g.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(Page.Create(items, page, http, g => g.Id));
    }

    private static async Task<Results<Created<GroupResponse>, ValidationProblem, ProblemHttpResult>> CreateGroupAsync(
        CreateGroupRequest request, IdentityDbContext db, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await db.Groups.AnyAsync(g => g.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A group named '{name}' already exists.");
        }

        var group = new Group { Id = Ids.New(), Name = name, Description = request.Description };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"{ApiRoutes.V1}/groups/{group.Id}", new GroupResponse(group.Id, group.Name, group.Description, group.CreatedAt));
    }

    private static async Task<Results<Ok<List<UserResponse>>, ProblemHttpResult>> ListMembersAsync(Guid id, IdentityDbContext db, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == id, ct))
        {
            return ApiErrors.NotFound();
        }

        var members = await db.GroupMembers.Where(m => m.GroupId == id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u)
            .OrderBy(u => u.Id)
            .Select(u => new UserResponse(u.Id, u.UserName, u.DisplayName, u.Email, u.IsDisabled, u.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(members);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> AddMemberAsync(
        Guid id, AddGroupMemberRequest request, IdentityDbContext db, ITenantContext tenant, HybridCache cache, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == id, ct) || !await db.Users.AnyAsync(u => u.Id == request.UserId && u.DeletedAt == null, ct))
        {
            return ApiErrors.NotFound();
        }

        if (!await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == request.UserId, ct))
        {
            db.GroupMembers.Add(new GroupMember { GroupId = id, UserId = request.UserId });
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenant.TenantId!.Value), ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveMemberAsync(
        Guid id, Guid userId, IdentityDbContext db, ITenantContext tenant, HybridCache cache, CancellationToken ct)
    {
        var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId, ct);
        if (member is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await AdministratorGuard.RemainsAsync(db, withoutMembership: (id, userId), ct: ct))
        {
            return AccountEndpoints.LastAdministrator();
        }

        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenant.TenantId!.Value), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<RoleResponse>>> ListRolesAsync(IdentityDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        return TypedResults.Ok(roles.Select(ToResponse).ToList());
    }

    private static async Task<Results<Created<RoleResponse>, ValidationProblem, ProblemHttpResult>> CreateRoleAsync(
        CreateRoleRequest request, IdentityDbContext db, IScopeCatalog catalog, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var unknown = request.Scopes.Where(s => !catalog.Contains(s)).ToList();
        if (unknown.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["scopes"] = [$"Unknown scopes: {string.Join(", ", unknown)}."] });
        }

        var name = request.Name.Trim();
        if (await db.Roles.AnyAsync(r => r.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A role named '{name}' already exists.");
        }

        var role = new Role { Id = Ids.New(), Name = name, Description = request.Description, Scopes = request.Scopes.Distinct().ToList() };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"{ApiRoutes.V1}/roles/{role.Id}", ToResponse(role));
    }

    private static async Task<Results<Ok<List<RoleAssignmentResponse>>, ProblemHttpResult>> ListAssignmentsAsync(Guid id, IdentityDbContext db, CancellationToken ct)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == id, ct))
        {
            return ApiErrors.NotFound();
        }

        var assignments = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.RoleId == id)
            .OrderBy(a => a.Id)
            .Select(a => new RoleAssignmentResponse(a.Id, a.RoleId, a.PrincipalId, a.PrincipalType))
            .ToListAsync(ct);
        return TypedResults.Ok(assignments);
    }

    private static async Task<Results<Created<RoleAssignmentResponse>, ProblemHttpResult>> AssignRoleAsync(
        Guid id, RoleAssignmentRequest request, IdentityDbContext db, ITenantContext tenant, HybridCache cache, CancellationToken ct)
    {
        var principalExists = request.PrincipalType == PrincipalType.User
            ? await db.Users.AnyAsync(u => u.Id == request.PrincipalId && u.DeletedAt == null, ct)
            : await db.Groups.AnyAsync(g => g.Id == request.PrincipalId, ct);
        if (!principalExists || !await db.Roles.AnyAsync(r => r.Id == id, ct))
        {
            return ApiErrors.NotFound();
        }

        var existing = await db.RoleAssignments.FirstOrDefaultAsync(
            a => a.RoleId == id && a.PrincipalId == request.PrincipalId && a.PrincipalType == request.PrincipalType, ct);
        var assignment = existing ?? new RoleAssignment { Id = Ids.New(), RoleId = id, PrincipalId = request.PrincipalId, PrincipalType = request.PrincipalType };
        if (existing is null)
        {
            db.RoleAssignments.Add(assignment);
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenant.TenantId!.Value), ct);
        }

        return TypedResults.Created(
            $"{ApiRoutes.V1}/roles/{id}/assignments/{assignment.Id}",
            new RoleAssignmentResponse(assignment.Id, assignment.RoleId, assignment.PrincipalId, assignment.PrincipalType));
    }

    private static RoleResponse ToResponse(Role r) => new(r.Id, r.Name, r.Description, r.IsBuiltIn, r.GrantsAllScopes, r.Scopes);
}
