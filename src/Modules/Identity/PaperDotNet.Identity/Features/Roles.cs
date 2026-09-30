using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record RoleResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsBuiltIn,
    bool GrantsAllScopes,
    IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record CreateRoleRequest(string? Name, string? Description, IReadOnlyList<string>? Scopes);

public sealed record UpdateRoleRequest(string? Name, string? Description, IReadOnlyList<string>? Scopes);

/// <summary>A role for a user or group; <c>principalType</c> is <c>user</c> or <c>group</c>.</summary>
public sealed record RoleAssignmentRequest(Guid PrincipalId, string? PrincipalType);

public sealed record RoleAssignmentResponse(Guid Id, Guid RoleId, Guid PrincipalId, string PrincipalType);

public sealed record ScopeResponse(string Name, string Description);

/// <summary>Roles (sets of scopes), their assignments to users and groups, and the scope catalog.</summary>
internal static class Roles
{
    public static RoleResponse ToResponse(Role r) =>
        new(r.Id, r.Name, r.Description, r.IsBuiltIn, r.GrantsAllScopes, ScopeList.Parse(r.Scopes), ETags.From(r.Version));

    public static void Map(IEndpointRouteBuilder app)
    {
        var roles = app.MapGroup("/v1.0/roles").WithTags("Roles");
        roles.MapGet("", ListAsync).RequireScope(IdentityScopes.RoleRead).WithName("ListRoles");
        roles.MapPost("", CreateAsync).RequireScope(IdentityScopes.RoleManage).WithName("CreateRole");
        roles.MapPatch("/{id:guid}", UpdateAsync).RequireScope(IdentityScopes.RoleManage).WithName("UpdateRole");
        roles.MapDelete("/{id:guid}", DeleteAsync).RequireScope(IdentityScopes.RoleManage).WithName("DeleteRole");
        roles.MapGet("/{id:guid}/assignments", ListAssignmentsAsync).RequireScope(IdentityScopes.RoleRead).WithName("ListRoleAssignments");
        roles.MapPost("/{id:guid}/assignments", AssignAsync).RequireScope(IdentityScopes.RoleManage).WithName("AssignRole");
        roles.MapDelete("/{id:guid}/assignments/{assignmentId:guid}", RemoveAssignmentAsync).RequireScope(IdentityScopes.RoleManage).WithName("RemoveRoleAssignment");
        app.MapGet("/v1.0/scopes", (IScopeCatalog catalog) =>
                TypedResults.Ok(catalog.All.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new ScopeResponse(s.Name, s.Description)).ToList()))
            .RequireAuthorization().WithTags("Roles").WithName("ListScopes");
    }

    private static Task<Role?> FindAsync(IdentityDbContext database, Guid tenantId, Guid roleId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = roleId;
        var ct = cancellationToken;
        return db.Roles.Where(r => r.TenantId == tenant && r.Id == id).FirstOrDefaultAsync(ct);
    }

    private static Task<bool> NameTakenAsync(IdentityDbContext database, Guid tenantId, string name, Guid? except, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var roleName = name;
        var other = except ?? Guid.Empty;
        var ct = cancellationToken;
        return db.Roles.AnyAsync(r => r.TenantId == tenant && r.Name == roleName && r.Id != other, ct);
    }

    private static ValidationProblem? CheckScopes(IReadOnlyList<string>? scopes, IScopeCatalog catalog) =>
        scopes?.Where(s => !catalog.Contains(s)).ToList() is { Count: > 0 } unknown
            ? ApiErrors.Validation("scopes", $"Unknown scopes: {string.Join(", ", unknown)}.")
            : null;

    private static async Task<Ok<List<RoleResponse>>> ListAsync(Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var roles = await db.Roles.Where(r => r.TenantId == tenant).OrderBy(r => r.Name).ToListAsync(ct);
        return TypedResults.Ok(roles.Select(ToResponse).ToList());
    }

    private static async Task<Results<Created<RoleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateRoleRequest body, Caller caller, IdentityDbContext db, IScopeCatalog catalog, TimeProvider time, CancellationToken cancellationToken)
    {
        if (body.Name?.Trim() is not { Length: > 0 and <= 200 } name)
        {
            return ApiErrors.Validation("name", "A name of up to 200 characters is required.");
        }

        if (body.Scopes is null)
        {
            return ApiErrors.Validation("scopes", "The scopes of the role are required.");
        }

        if (CheckScopes(body.Scopes, catalog) is { } invalid)
        {
            return invalid;
        }

        if (await NameTakenAsync(db, caller.TenantId, name, null, cancellationToken))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A role named '{name}' already exists.");
        }

        var role = new Role
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            Name = name,
            Description = body.Description is { Length: > 0 } d ? d : null,
            Scopes = ScopeList.Format(body.Scopes),
            CreatedAt = time.GetUtcNow(),
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/roles/{role.Id}", ToResponse(role));
    }

    private static async Task<Results<Ok<RoleResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateRoleRequest body, Caller caller, IdentityDbContext db, IScopeCatalog catalog, HttpRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim();
        if (name is { Length: 0 or > 200 })
        {
            return ApiErrors.Validation("name", "The name needs 1 to 200 characters.");
        }

        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } role)
        {
            return ApiErrors.NotFound();
        }

        if (ETags.TryGetIfMatch(request, out var expected) && expected != role.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (role.IsBuiltIn && ((name is not null && name != role.Name) || (role.GrantsAllScopes && body.Scopes is not null)))
        {
            return ApiErrors.Conflict("builtInRole", "Built-in roles cannot be renamed, and the Administrator role always has every scope.");
        }

        if (CheckScopes(body.Scopes, catalog) is { } invalid)
        {
            return invalid;
        }

        if (name is not null && name != role.Name)
        {
            if (await NameTakenAsync(db, caller.TenantId, name, id, cancellationToken))
            {
                return ApiErrors.Conflict("nameAlreadyExists", $"A role named '{name}' already exists.");
            }

            role.Name = name;
        }

        if (body.Description is { } description)
        {
            role.Description = description.Length == 0 ? null : description;
        }

        if (body.Scopes is { } scopes)
        {
            role.Scopes = ScopeList.Format(scopes);
        }

        await db.SaveChangesAsync(cancellationToken);
        ETags.Set(response, role.Version);
        return TypedResults.Ok(ToResponse(role));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } role)
        {
            return ApiErrors.NotFound();
        }

        if (role.IsBuiltIn)
        {
            return ApiErrors.Conflict("builtInRole", "Built-in roles cannot be deleted.");
        }

        var tenant = caller.TenantId;
        var roleId = id;
        var ct = cancellationToken;
        db.Roles.Remove(role);
        db.RoleAssignments.RemoveRange(await db.RoleAssignments.Where(a => a.TenantId == tenant && a.RoleId == roleId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<RoleAssignmentResponse>>, ProblemHttpResult>> ListAssignmentsAsync(Guid id, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var tenant = caller.TenantId;
        var roleId = id;
        var ct = cancellationToken;
        var assignments = await db.RoleAssignments.Where(a => a.TenantId == tenant && a.RoleId == roleId).OrderBy(a => a.Id).ToListAsync(ct);
        return TypedResults.Ok(assignments.Select(a => new RoleAssignmentResponse(a.Id, a.RoleId, a.PrincipalId, a.PrincipalType)).ToList());
    }

    private static async Task<Results<Created<RoleAssignmentResponse>, ValidationProblem, ProblemHttpResult>> AssignAsync(
        Guid id, RoleAssignmentRequest body, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var type = body.PrincipalType ?? PrincipalTypes.User;
        if (type is not (PrincipalTypes.User or PrincipalTypes.Group))
        {
            return ApiErrors.Validation("principalType", "The principal type is 'user' or 'group'.");
        }

        var principalExists = type == PrincipalTypes.User
            ? await Users.FindAsync(db, caller.TenantId, body.PrincipalId, cancellationToken) is not null
            : await Groups.FindAsync(db, caller.TenantId, body.PrincipalId, cancellationToken) is not null;
        if (!principalExists || await FindAsync(db, caller.TenantId, id, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var tenant = caller.TenantId;
        var roleId = id;
        var principal = body.PrincipalId;
        var ct = cancellationToken;
        var existing = await db.RoleAssignments.Where(a => a.TenantId == tenant && a.RoleId == roleId && a.PrincipalId == principal && a.PrincipalType == type).FirstOrDefaultAsync(ct);
        var assignment = existing ?? new RoleAssignment { Id = Ids.New(), TenantId = tenant, RoleId = roleId, PrincipalId = principal, PrincipalType = type };
        if (existing is null)
        {
            db.RoleAssignments.Add(assignment);
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Created(
            $"/v1.0/roles/{id}/assignments/{assignment.Id}",
            new RoleAssignmentResponse(assignment.Id, assignment.RoleId, assignment.PrincipalId, assignment.PrincipalType));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAssignmentAsync(Guid id, Guid assignmentId, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var roleId = id;
        var assignmentKey = assignmentId;
        var ct = cancellationToken;
        var assignment = await db.RoleAssignments.Where(a => a.TenantId == tenant && a.Id == assignmentKey && a.RoleId == roleId).FirstOrDefaultAsync(ct);
        if (assignment is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await AdministratorGuard.RemainsAsync(db, tenant, withoutAssignment: assignmentId, cancellationToken: ct))
        {
            return Users.LastAdministrator();
        }

        db.RoleAssignments.Remove(assignment);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
