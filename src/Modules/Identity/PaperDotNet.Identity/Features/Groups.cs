using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;
using PaperDotNet.Messaging;

namespace PaperDotNet.Identity.Features;

public sealed record GroupResponse(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record CreateGroupRequest(string? Name, string? Description);

/// <summary>Changes to a group; an empty description removes it.</summary>
public sealed record UpdateGroupRequest(string? Name, string? Description);

public sealed record AddGroupMemberRequest(Guid UserId);

/// <summary>A group to put inside another group: its members become members of that group too.</summary>
public sealed record AddNestedGroupRequest(Guid GroupId);

/// <summary>Groups, their members and groups inside groups (ADR-0035).</summary>
internal static class Groups
{
    public static GroupResponse ToResponse(Group g) => new(g.Id, g.Name, g.Description, g.CreatedAt, ETags.From(g.Version));

    public static void Map(IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/v1.0/groups").WithTags("Groups");
        groups.MapGet("", ListAsync).RequireScope(IdentityScopes.GroupRead).WithName("ListGroups");
        groups.MapPost("", CreateAsync).RequireScope(IdentityScopes.GroupManage).WithName("CreateGroup");
        groups.MapGet("/{id:guid}", GetAsync).RequireScope(IdentityScopes.GroupRead).WithName("GetGroup");
        groups.MapPatch("/{id:guid}", UpdateAsync).RequireScope(IdentityScopes.GroupManage).WithName("UpdateGroup");
        groups.MapDelete("/{id:guid}", DeleteAsync).RequireScope(IdentityScopes.GroupManage).WithName("DeleteGroup");
        groups.MapGet("/{id:guid}/members", ListMembersAsync).RequireScope(IdentityScopes.GroupRead).WithName("ListGroupMembers");
        groups.MapPost("/{id:guid}/members", AddMemberAsync).RequireScope(IdentityScopes.GroupManage).WithName("AddGroupMember");
        groups.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync).RequireScope(IdentityScopes.GroupManage).WithName("RemoveGroupMember");
        groups.MapGet("/{id:guid}/groups", ListNestedAsync).RequireScope(IdentityScopes.GroupRead).WithName("ListNestedGroups");
        groups.MapPost("/{id:guid}/groups", AddNestedAsync).RequireScope(IdentityScopes.GroupManage).WithName("AddNestedGroup");
        groups.MapDelete("/{id:guid}/groups/{memberGroupId:guid}", RemoveNestedAsync).RequireScope(IdentityScopes.GroupManage).WithName("RemoveNestedGroup");
    }

    public static Task<Group?> FindAsync(IdentityDbContext database, Guid tenantId, Guid groupId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = groupId;
        var ct = cancellationToken;
        return db.Groups.Where(g => g.TenantId == tenant && g.Id == id).FirstOrDefaultAsync(ct);
    }

    private static Task<bool> NameTakenAsync(IdentityDbContext database, Guid tenantId, string name, Guid? except, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var groupName = name;
        var other = except ?? Guid.Empty;
        var ct = cancellationToken;
        return db.Groups.AnyAsync(g => g.TenantId == tenant && g.Name == groupName && g.Id != other, ct);
    }

    private static async Task<Ok<Page<GroupResponse>>> ListAsync(HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        List<Group> groups;
        if (page.After is { } after)
        {
            groups = await db.Groups.Where(g => g.TenantId == tenant && g.Id.CompareTo(after) > 0).OrderBy(g => g.Id).Take(take).ToListAsync(ct);
        }
        else
        {
            groups = await db.Groups.Where(g => g.TenantId == tenant).OrderBy(g => g.Id).Take(take).ToListAsync(ct);
        }

        return TypedResults.Ok(Page.Create([.. groups.Select(ToResponse)], page, request, g => g.Id));
    }

    private static async Task<Results<Ok<GroupResponse>, ProblemHttpResult>> GetAsync(Guid id, Caller caller, IdentityDbContext db, HttpResponse response, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } group)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, group.Version);
        return TypedResults.Ok(ToResponse(group));
    }

    private static async Task<Results<Created<GroupResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateGroupRequest body, Caller caller, IdentityDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (body.Name?.Trim() is not { Length: > 0 and <= 200 } name)
        {
            return ApiErrors.Validation("name", "A name of up to 200 characters is required.");
        }

        if (body.Description is { Length: > 1000 })
        {
            return ApiErrors.Validation("description", "The description can have at most 1000 characters.");
        }

        if (await NameTakenAsync(db, caller.TenantId, name, null, cancellationToken))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A group named '{name}' already exists.");
        }

        var group = new Group { Id = Ids.New(), TenantId = caller.TenantId, Name = name, Description = body.Description is { Length: > 0 } d ? d : null, CreatedAt = time.GetUtcNow() };
        db.Groups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/groups/{group.Id}", ToResponse(group));
    }

    private static async Task<Results<Ok<GroupResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateGroupRequest body, Caller caller, IdentityDbContext db, HttpRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim();
        if (name is { Length: 0 or > 200 })
        {
            return ApiErrors.Validation("name", "The name needs 1 to 200 characters.");
        }

        if (body.Description is { Length: > 1000 })
        {
            return ApiErrors.Validation("description", "The description can have at most 1000 characters.");
        }

        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } group)
        {
            return ApiErrors.NotFound();
        }

        if (ETags.TryGetIfMatch(request, out var expected) && expected != group.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (name is not null && name != group.Name)
        {
            if (await NameTakenAsync(db, caller.TenantId, name, id, cancellationToken))
            {
                return ApiErrors.Conflict("nameAlreadyExists", $"A group named '{name}' already exists.");
            }

            group.Name = name;
        }

        if (body.Description is { } description)
        {
            group.Description = description.Length == 0 ? null : description;
        }

        await db.SaveChangesAsync(cancellationToken);
        ETags.Set(response, group.Version);
        return TypedResults.Ok(ToResponse(group));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, Caller caller, IdentityDbContext database, IOutbox outbox, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is not { } group)
        {
            return ApiErrors.NotFound();
        }

        if (!await AdministratorGuard.RemainsAsync(db, caller.TenantId, withoutGroup: id, cancellationToken: cancellationToken))
        {
            return Users.LastAdministrator();
        }

        var tenant = caller.TenantId;
        var groupId = id;
        var groupType = PrincipalTypes.Group;
        var ct = cancellationToken;
        db.Groups.Remove(group);
        db.GroupMembers.RemoveRange(await db.GroupMembers.Where(m => m.TenantId == tenant && m.GroupId == groupId).ToListAsync(ct));
        db.GroupNestings.RemoveRange(await db.GroupNestings.Where(n => n.TenantId == tenant && (n.GroupId == groupId || n.MemberGroupId == groupId)).ToListAsync(ct));
        db.RoleAssignments.RemoveRange(await db.RoleAssignments.Where(a => a.TenantId == tenant && a.PrincipalType == groupType && a.PrincipalId == groupId).ToListAsync(ct));
        await outbox.SaveChangesAsync(db, [new PrincipalDeleted { TenantId = tenant, UserId = caller.UserId, PrincipalId = id, IsGroup = true }], ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<UserResponse>>, ProblemHttpResult>> ListMembersAsync(Guid id, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var tenant = caller.TenantId;
        var groupId = id;
        var ct = cancellationToken;
        var members = await db.Users
            .Where(u => u.TenantId == tenant && u.DeletedAt == null && db.GroupMembers.Any(m => m.TenantId == tenant && m.GroupId == groupId && m.UserId == u.Id))
            .OrderBy(u => u.Id)
            .ToListAsync(ct);
        return TypedResults.Ok(members.Select(Users.ToResponse).ToList());
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> AddMemberAsync(
        Guid id, AddGroupMemberRequest body, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is null || await Users.FindAsync(db, caller.TenantId, body.UserId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var tenant = caller.TenantId;
        var groupId = id;
        var user = body.UserId;
        var ct = cancellationToken;
        if (!await db.GroupMembers.AnyAsync(m => m.TenantId == tenant && m.GroupId == groupId && m.UserId == user, ct))
        {
            db.GroupMembers.Add(new GroupMember { TenantId = tenant, GroupId = groupId, UserId = user });
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveMemberAsync(
        Guid id, Guid userId, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var groupId = id;
        var user = userId;
        var ct = cancellationToken;
        var member = await db.GroupMembers.Where(m => m.TenantId == tenant && m.GroupId == groupId && m.UserId == user).FirstOrDefaultAsync(ct);
        if (member is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await AdministratorGuard.RemainsAsync(db, tenant, withoutMembership: (id, userId), cancellationToken: ct))
        {
            return Users.LastAdministrator();
        }

        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The groups directly inside a group (ADR-0035).</summary>
    private static async Task<Results<Ok<List<GroupResponse>>, ProblemHttpResult>> ListNestedAsync(Guid id, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var tenant = caller.TenantId;
        var groupId = id;
        var ct = cancellationToken;
        var groups = await db.Groups
            .Where(g => g.TenantId == tenant && db.GroupNestings.Any(n => n.TenantId == tenant && n.GroupId == groupId && n.MemberGroupId == g.Id))
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
        return TypedResults.Ok(groups.Select(ToResponse).ToList());
    }

    /// <summary>Puts a group inside another; a cycle or more than <see cref="GroupGraph.MaxDepth"/> levels is a conflict.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> AddNestedAsync(
        Guid id, AddNestedGroupRequest body, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller.TenantId, id, cancellationToken) is null || await FindAsync(db, caller.TenantId, body.GroupId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var nestings = (await db.GroupNestings.AsNoTracking().Where(n => n.TenantId == tenant).Select(n => new NestingPair(n.GroupId, n.MemberGroupId)).ToListAsync(ct))
            .Select(n => (n.GroupId, n.MemberGroupId))
            .ToList();
        if (nestings.Contains((id, body.GroupId)))
        {
            return TypedResults.NoContent();
        }

        if (GroupGraph.CheckNesting(id, body.GroupId, nestings) is { } problem)
        {
            return ApiErrors.Conflict("invalidNesting", problem);
        }

        db.GroupNestings.Add(new GroupNesting { TenantId = tenant, GroupId = id, MemberGroupId = body.GroupId });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveNestedAsync(
        Guid id, Guid memberGroupId, Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var groupId = id;
        var member = memberGroupId;
        var ct = cancellationToken;
        var nesting = await db.GroupNestings.Where(n => n.TenantId == tenant && n.GroupId == groupId && n.MemberGroupId == member).FirstOrDefaultAsync(ct);
        if (nesting is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await AdministratorGuard.RemainsAsync(db, tenant, withoutNesting: (id, memberGroupId), cancellationToken: ct))
        {
            return Users.LastAdministrator();
        }

        db.GroupNestings.Remove(nesting);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
