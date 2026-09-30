using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Workspaces.Contracts;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Workspaces.Features;

/// <summary>A workspace and what the caller may do there (<c>manage</c> for owners and administrators, <c>contribute</c>, <c>read</c>).</summary>
public sealed record WorkspaceResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsPersonal,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    WorkspaceAccessLevel Access,
    [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record CreateWorkspaceRequest(string? Name, string? Description);

/// <summary>PATCH body: only the properties sent are changed.</summary>
public sealed record UpdateWorkspaceRequest(string? Name, string? Description);

public sealed record WorkspaceMemberResponse(Guid UserId, string Role);

/// <summary>Adds a member or changes their role: <c>owner</c>, <c>member</c> (the default) or <c>visitor</c>.</summary>
public sealed record AddWorkspaceMemberRequest(Guid UserId, string? Role);

/// <summary>Workspaces and their members. Changes need <c>If-Match</c>; nothing leaves a workspace without an owner.</summary>
internal static class WorkspaceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/workspaces").WithTags("Workspaces");
        group.MapGet("", ListAsync).RequireScope(WorkspaceScopes.Read).WithName("ListWorkspaces");
        group.MapPost("", CreateAsync).RequireScope(WorkspaceScopes.Create).WithName("CreateWorkspace");
        group.MapGet("/{workspaceId:guid}", GetAsync).RequireScope(WorkspaceScopes.Read).WithName("GetWorkspace");
        group.MapPatch("/{workspaceId:guid}", UpdateAsync).RequireScope(WorkspaceScopes.Read).WithName("UpdateWorkspace");
        group.MapDelete("/{workspaceId:guid}", DeleteAsync).RequireScope(WorkspaceScopes.Read).WithName("DeleteWorkspace");
        group.MapGet("/{workspaceId:guid}/members", ListMembersAsync).RequireScope(WorkspaceScopes.Read).WithName("ListWorkspaceMembers");
        group.MapPost("/{workspaceId:guid}/members", AddMemberAsync).RequireScope(WorkspaceScopes.Read).WithName("AddWorkspaceMember");
        group.MapDelete("/{workspaceId:guid}/members/{userId:guid}", RemoveMemberAsync).RequireScope(WorkspaceScopes.Read).WithName("RemoveWorkspaceMember");
    }

    private static WorkspaceResponse ToResponse(Workspace w, WorkspaceAccessLevel access) =>
        new(w.Id, w.Name, w.Description, w.PersonalOwnerId is not null, w.CreatedAt, w.UpdatedAt, access, ETags.From(w.Version));

    private static async Task<Ok<Page<WorkspaceResponse>>> ListAsync(
        HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller,
        WorkspaceAccess access, WorkspacesDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var levels = (await access.GetMembershipsAsync(caller.TenantId, caller.UserId, cancellationToken)).ToDictionary(m => m.WorkspaceId, m => m.Level);
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var after = page.After ?? Guid.Empty;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var all = await access.IsAdministratorAsync(tenant, user, ct);

        // Static queries only (precompiled): administrators see every workspace, others those they are members of.
        var workspaces = all
            ? await db.Workspaces.Where(w => w.TenantId == tenant && w.DeletedAt == null && w.Id.CompareTo(after) > 0).OrderBy(w => w.Id).Take(take).ToListAsync(ct)
            : await db.Workspaces
                .Where(w => w.TenantId == tenant && w.DeletedAt == null && w.Id.CompareTo(after) > 0
                            && db.Members.Any(m => m.TenantId == tenant && m.WorkspaceId == w.Id && m.UserId == user))
                .OrderBy(w => w.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create(
            [.. workspaces.Select(w => ToResponse(w, levels.GetValueOrDefault(w.Id, WorkspaceAccessLevel.Read)))], page, request, w => w.Id));
    }

    private static async Task<Results<Created<WorkspaceResponse>, ValidationProblem>> CreateAsync(
        CreateWorkspaceRequest body, Caller caller, WorkspacesDbContext db, HttpResponse response, CancellationToken cancellationToken)
    {
        if (Validate(body.Name, body.Description, nameRequired: true) is { } invalid)
        {
            return invalid;
        }

        var workspace = new Workspace { Id = Ids.New(), TenantId = caller.TenantId, Name = body.Name!.Trim(), Description = body.Description };
        db.Workspaces.Add(workspace);
        db.Members.Add(new WorkspaceMember { TenantId = caller.TenantId, WorkspaceId = workspace.Id, UserId = caller.UserId, Role = WorkspaceRoles.Owner });
        await db.SaveChangesAsync(cancellationToken);
        ETags.Set(response, workspace.Version);
        return TypedResults.Created($"/v1.0/workspaces/{workspace.Id}", ToResponse(workspace, WorkspaceAccessLevel.Manage));
    }

    private static async Task<Results<Ok<WorkspaceResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Caller caller, WorkspaceAccess access, HttpResponse response, CancellationToken cancellationToken)
    {
        var level = await access.GetPermissionAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken);
        if (level == WorkspaceAccessLevel.None || await access.FindAsync(caller.TenantId, workspaceId, cancellationToken) is not { } workspace)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, workspace.Version);
        return TypedResults.Ok(ToResponse(workspace, level));
    }

    private static async Task<Results<Ok<WorkspaceResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, UpdateWorkspaceRequest body, Caller caller, WorkspaceAccess access, WorkspacesDbContext db,
        HttpRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        if (Validate(body.Name, body.Description, nameRequired: false) is { } invalid)
        {
            return invalid;
        }

        var (workspace, problem) = await LoadForChangeAsync(workspaceId, caller, access, request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        workspace!.Name = body.Name?.Trim() ?? workspace.Name;
        workspace.Description = body.Description ?? workspace.Description;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, workspace.Version);
        return TypedResults.Ok(ToResponse(workspace, WorkspaceAccessLevel.Manage));
    }

    /// <summary>Moves a workspace to the recycle bin (its lists go with it); personal workspaces stay.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Caller caller, WorkspaceAccess access, WorkspacesDbContext db, HttpRequest request, CancellationToken cancellationToken)
    {
        var (workspace, problem) = await LoadForChangeAsync(workspaceId, caller, access, request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (workspace!.PersonalOwnerId is not null)
        {
            return ApiErrors.Conflict("personalWorkspace", "A personal workspace cannot be deleted.");
        }

        db.Workspaces.Remove(workspace);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<WorkspaceMemberResponse>>, ProblemHttpResult>> ListMembersAsync(
        Guid workspaceId, Caller caller, WorkspaceAccess access, WorkspacesDbContext database, CancellationToken cancellationToken)
    {
        if (await access.GetPermissionAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken) == WorkspaceAccessLevel.None)
        {
            return ApiErrors.NotFound();
        }

        var db = database;
        var tenant = caller.TenantId;
        var workspace = workspaceId;
        var ct = cancellationToken;
        var members = await db.Members.Where(m => m.TenantId == tenant && m.WorkspaceId == workspace).OrderBy(m => m.UserId).ToListAsync(ct);
        return TypedResults.Ok(members.Select(m => new WorkspaceMemberResponse(m.UserId, m.Role)).ToList());
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> AddMemberAsync(
        Guid workspaceId, AddWorkspaceMemberRequest body, Caller caller, WorkspaceAccess access, WorkspacesDbContext database, IUserDirectory users, CancellationToken cancellationToken)
    {
        var role = body.Role ?? WorkspaceRoles.Member;
        if (!WorkspaceRoles.IsValid(role))
        {
            return ApiErrors.Validation("role", "The role is owner, member or visitor.");
        }

        if (!await access.CanManageAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken)
            || await access.FindAsync(caller.TenantId, workspaceId, cancellationToken) is not { } workspace)
        {
            return ApiErrors.NotFound();
        }

        if (workspace.PersonalOwnerId is not null)
        {
            return ApiErrors.Conflict("personalWorkspace", "Members cannot be added to a personal workspace.");
        }

        if (!await users.IsActiveAsync(caller.TenantId, body.UserId, cancellationToken))
        {
            return ApiErrors.Validation("userId", "Unknown or disabled user.");
        }

        var db = database;
        var member = await FindMemberAsync(db, caller.TenantId, workspaceId, body.UserId, cancellationToken);
        if (member is null)
        {
            db.Members.Add(new WorkspaceMember { TenantId = caller.TenantId, WorkspaceId = workspaceId, UserId = body.UserId, Role = role });
        }
        else
        {
            if (role != WorkspaceRoles.Owner && await IsLastOwnerAsync(db, member, cancellationToken))
            {
                return LastOwner();
            }

            member.Role = role;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Removes a member; the last owner stays, so someone can always manage the workspace.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveMemberAsync(
        Guid workspaceId, Guid userId, Caller caller, WorkspaceAccess access, WorkspacesDbContext db, CancellationToken cancellationToken)
    {
        if (!await access.CanManageAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken)
            || await FindMemberAsync(db, caller.TenantId, workspaceId, userId, cancellationToken) is not { } member)
        {
            return ApiErrors.NotFound();
        }

        if (await IsLastOwnerAsync(db, member, cancellationToken))
        {
            return LastOwner();
        }

        db.Members.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Task<WorkspaceMember?> FindMemberAsync(WorkspacesDbContext database, Guid tenantId, Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var workspace = workspaceId;
        var user = userId;
        var ct = cancellationToken;
        return db.Members.Where(m => m.TenantId == tenant && m.WorkspaceId == workspace && m.UserId == user).FirstOrDefaultAsync(ct);
    }

    private static async Task<bool> IsLastOwnerAsync(WorkspacesDbContext database, WorkspaceMember member, CancellationToken cancellationToken)
    {
        if (member.Role != WorkspaceRoles.Owner)
        {
            return false;
        }

        var db = database;
        var tenant = member.TenantId;
        var workspace = member.WorkspaceId;
        var user = member.UserId;
        var owner = WorkspaceRoles.Owner;
        var ct = cancellationToken;
        return !await db.Members.AnyAsync(m => m.TenantId == tenant && m.WorkspaceId == workspace && m.UserId != user && m.Role == owner, ct);
    }

    private static ProblemHttpResult LastOwner() =>
        ApiErrors.Conflict("lastOwner", "A workspace needs at least one owner. Make someone else an owner first.");

    private static ValidationProblem? Validate(string? name, string? description, bool nameRequired) =>
        (nameRequired || name is not null) && name?.Trim() is not { Length: > 0 and <= 200 }
            ? ApiErrors.Validation("name", "A name of 1 to 200 characters is required.")
            : description is { Length: > 2000 }
                ? ApiErrors.Validation("description", "The description can have at most 2000 characters.")
                : null;

    /// <summary>Loads a workspace the caller may change, enforcing <c>If-Match</c>.</summary>
    private static async Task<(Workspace? Workspace, ProblemHttpResult? Problem)> LoadForChangeAsync(
        Guid workspaceId, Caller caller, WorkspaceAccess access, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!await access.CanManageAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken)
            || await access.FindAsync(caller.TenantId, workspaceId, cancellationToken) is not { } workspace)
        {
            return (null, ApiErrors.NotFound());
        }

        if (!ETags.TryGetIfMatch(request, out var version))
        {
            return (null, ApiErrors.PreconditionRequired());
        }

        return workspace.Version != version ? (null, ApiErrors.PreconditionFailed()) : (workspace, null);
    }
}

/// <summary>Removes a deleted user's workspace memberships (IAM-14; a Wolverine handler, generated ahead of time).</summary>
public static class WorkspacePrincipalSubscriber
{
    public static async Task Handle(PrincipalDeleted e, WorkspacesDbContext database, CancellationToken cancellationToken)
    {
        if (e.IsGroup)
        {
            return;
        }

        var db = database;
        var tenant = e.TenantId;
        var user = e.PrincipalId;
        var ct = cancellationToken;
        db.Members.RemoveRange(await db.Members.Where(m => m.TenantId == tenant && m.UserId == user).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
