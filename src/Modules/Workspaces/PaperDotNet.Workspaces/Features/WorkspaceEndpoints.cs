using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Workspaces.Contracts;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Workspaces.Features;

public sealed record WorkspaceResponse(Guid Id, string Name, string? Description, bool IsPersonal, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }

    /// <summary>What the caller may do here: <c>manage</c> (owners, administrators), <c>contribute</c> or <c>read</c>.</summary>
    public WorkspaceAccessLevel Access { get; init; }
}

public sealed record CreateWorkspaceRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description);

/// <summary>PATCH body: only the properties sent are changed.</summary>
public sealed record UpdateWorkspaceRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Name,
    [property: StringLength(2000)] string? Description);

public sealed record WorkspaceMemberResponse(Guid UserId, WorkspaceRole Role);

public sealed record AddWorkspaceMemberRequest([property: Required] Guid UserId, WorkspaceRole Role = WorkspaceRole.Member);

internal static class WorkspaceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("workspaces", "Workspaces");
        group.MapGet("", ListAsync).RequireScope(WorkspaceScopes.Read).WithName("ListWorkspaces");
        group.MapPost("", CreateAsync).RequireScope(WorkspaceScopes.Create).WithName("CreateWorkspace");
        group.MapGet("/{workspaceId:guid}", GetAsync).RequireScope(WorkspaceScopes.Read).WithName("GetWorkspace");
        group.MapPatch("/{workspaceId:guid}", UpdateAsync).RequireScope(WorkspaceScopes.Read).WithName("UpdateWorkspace");
        group.MapDelete("/{workspaceId:guid}", DeleteAsync).RequireScope(WorkspaceScopes.Read).WithName("DeleteWorkspace");
        group.MapGet("/{workspaceId:guid}/members", ListMembersAsync).RequireScope(WorkspaceScopes.Read).WithName("ListWorkspaceMembers");
        group.MapPost("/{workspaceId:guid}/members", AddMemberAsync).RequireScope(WorkspaceScopes.Read).WithName("AddWorkspaceMember");
        group.MapDelete("/{workspaceId:guid}/members/{userId:guid}", RemoveMemberAsync).RequireScope(WorkspaceScopes.Read).WithName("RemoveWorkspaceMember");
    }

    private static async Task<Ok<Page<WorkspaceResponse>>> ListAsync(
        HttpRequest http, WorkspaceAccess access, WorkspacesDbContext db, CancellationToken ct)
    {
        var page = PageRequest.From(http);
        var levels = (await access.GetMyWorkspacesAsync(ct)).ToDictionary(m => m.WorkspaceId, m => m.Level);
        var items = await (await access.VisibleAsync(db.Workspaces.AsNoTracking(), ct))
            .Where(w => page.After == null || w.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(w => w.Id)
            .Take(page.Top + 1)
            .Select(w => new WorkspaceResponse(w.Id, w.Name, w.Description, w.PersonalOwnerId != null, w.CreatedAt, w.UpdatedAt) { ETag = ETags.From(w.Version) })
            .ToListAsync(ct);
        items = [.. items.Select(w => w with { Access = levels.GetValueOrDefault(w.Id, WorkspaceAccessLevel.Read) })];
        return TypedResults.Ok(Page.Create(items, page, http, w => w.Id));
    }

    private static async Task<Results<Created<WorkspaceResponse>, ValidationProblem>> CreateAsync(
        CreateWorkspaceRequest request, ICurrentUser user, WorkspacesDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var workspace = new Workspace { Id = Ids.New(), Name = request.Name.Trim(), Description = request.Description };
        workspace.Members.Add(new WorkspaceMember { UserId = user.UserId!.Value, Role = WorkspaceRole.Owner });
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, workspace.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspace.Id}", ToResponse(workspace, WorkspaceAccessLevel.Manage));
    }

    private static async Task<Results<Ok<WorkspaceResponse>, ProblemHttpResult>> GetAsync(
        [FromRoute(Name = "workspaceId")] Guid id, WorkspaceAccess access, WorkspacesDbContext db, HttpResponse response, CancellationToken ct)
    {
        var workspace = await (await access.VisibleAsync(db.Workspaces.AsNoTracking(), ct)).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, workspace.Version);
        return TypedResults.Ok(ToResponse(workspace, await access.GetPermissionAsync(id, ct)));
    }

    private static async Task<Results<Ok<WorkspaceResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        [FromRoute(Name = "workspaceId")] Guid id, UpdateWorkspaceRequest request, WorkspaceAccess access, WorkspacesDbContext db,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var (workspace, problem) = await LoadForChangeAsync(id, access, db, http, ct);
        if (problem is not null)
        {
            return problem;
        }

        workspace!.Name = request.Name?.Trim() ?? workspace.Name;
        workspace.Description = request.Description ?? workspace.Description;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, workspace.Version);
        return TypedResults.Ok(ToResponse(workspace, WorkspaceAccessLevel.Manage));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        [FromRoute(Name = "workspaceId")] Guid id, WorkspaceAccess access, WorkspacesDbContext db, HttpRequest http, CancellationToken ct)
    {
        var (workspace, problem) = await LoadForChangeAsync(id, access, db, http, ct);
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
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<WorkspaceMemberResponse>>, ProblemHttpResult>> ListMembersAsync(
        [FromRoute(Name = "workspaceId")] Guid id, WorkspaceAccess access, WorkspacesDbContext db, CancellationToken ct)
    {
        if (!await (await access.VisibleAsync(db.Workspaces, ct)).AnyAsync(w => w.Id == id, ct))
        {
            return ApiErrors.NotFound();
        }

        var members = await db.Members.AsNoTracking()
            .Where(m => m.WorkspaceId == id)
            .OrderBy(m => m.UserId)
            .Select(m => new WorkspaceMemberResponse(m.UserId, m.Role))
            .ToListAsync(ct);
        return TypedResults.Ok(members);
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> AddMemberAsync(
        [FromRoute(Name = "workspaceId")] Guid id, AddWorkspaceMemberRequest request, WorkspaceAccess access, WorkspacesDbContext db, IUserDirectory users, CancellationToken ct)
    {
        if (!await access.CanManageAsync(id, ct))
        {
            return ApiErrors.NotFound();
        }

        if (await db.Workspaces.AnyAsync(w => w.Id == id && w.PersonalOwnerId != null, ct))
        {
            return ApiErrors.Conflict("personalWorkspace", "Members cannot be added to a personal workspace.");
        }

        if (!await users.IsActiveAsync(request.UserId, ct))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["userId"] = ["Unknown or disabled user."] });
        }

        var member = await db.Members.FirstOrDefaultAsync(m => m.WorkspaceId == id && m.UserId == request.UserId, ct);
        if (member is null)
        {
            db.Members.Add(new WorkspaceMember { WorkspaceId = id, UserId = request.UserId, Role = request.Role });
        }
        else
        {
            if (request.Role != WorkspaceRole.Owner && await IsLastOwnerAsync(db, member, ct))
            {
                return LastOwner();
            }

            member.Role = request.Role;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Removes a member; the last owner stays, so someone can always manage the workspace.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveMemberAsync(
        [FromRoute(Name = "workspaceId")] Guid id, Guid userId, WorkspaceAccess access, WorkspacesDbContext db, CancellationToken ct)
    {
        if (!await access.CanManageAsync(id, ct))
        {
            return ApiErrors.NotFound();
        }

        var member = await db.Members.FirstOrDefaultAsync(m => m.WorkspaceId == id && m.UserId == userId, ct);
        if (member is null)
        {
            return ApiErrors.NotFound();
        }

        if (await IsLastOwnerAsync(db, member, ct))
        {
            return LastOwner();
        }

        db.Members.Remove(member);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<bool> IsLastOwnerAsync(WorkspacesDbContext db, WorkspaceMember member, CancellationToken ct) =>
        member.Role == WorkspaceRole.Owner
        && !await db.Members.AnyAsync(m => m.WorkspaceId == member.WorkspaceId && m.UserId != member.UserId && m.Role == WorkspaceRole.Owner, ct);

    private static ProblemHttpResult LastOwner() =>
        ApiErrors.Conflict("lastOwner", "A workspace needs at least one owner. Make someone else an owner first.");

    /// <summary>Loads a workspace the caller may change, enforcing <c>If-Match</c>.</summary>
    private static async Task<(Workspace? Workspace, ProblemHttpResult? Problem)> LoadForChangeAsync(
        Guid id, WorkspaceAccess access, WorkspacesDbContext db, HttpRequest http, CancellationToken ct)
    {
        if (!await access.CanManageAsync(id, ct))
        {
            return (null, ApiErrors.NotFound());
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return (null, ApiErrors.PreconditionRequired());
        }

        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null)
        {
            return (null, ApiErrors.NotFound());
        }

        if (workspace.Version != version)
        {
            return (null, ApiErrors.PreconditionFailed());
        }

        // Make EF use the client's version for the concurrency check.
        db.Entry(workspace).Property(w => w.Version).OriginalValue = version;
        return (workspace, null);
    }

    private static WorkspaceResponse ToResponse(Workspace w, WorkspaceAccessLevel access) =>
        new(w.Id, w.Name, w.Description, w.PersonalOwnerId is not null, w.CreatedAt, w.UpdatedAt) { ETag = ETags.From(w.Version), Access = access };
}
