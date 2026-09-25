using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Taxonomy.Data;

namespace PaperDotNet.Taxonomy.Features;

internal static partial class TermStoreEndpoints
{
    // ---- Groups -------------------------------------------------------------

    private static async Task<Ok<Page<TermGroupResponse>>> ListGroupsAsync(HttpRequest http, TaxonomyDbContext db, CancellationToken ct)
    {
        await TermStore.EnsureKeywordsSetAsync(db, ct);
        var page = PageRequest.From(http);
        var items = await db.Groups.AsNoTracking()
            .Where(g => page.After == null || g.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(g => g.Id)
            .Take(page.Top + 1)
            .Select(g => new TermGroupResponse(g.Id, g.Name, g.Description, g.IsSystem, g.CreatedAt, g.UpdatedAt) { ETag = ETags.From(g.Version) })
            .ToListAsync(ct);
        return TypedResults.Ok(Page.Create(items, page, http, g => g.Id));
    }

    private static async Task<Results<Created<TermGroupResponse>, ValidationProblem, ProblemHttpResult>> CreateGroupAsync(
        TermGroupRequest request, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Invalid("name", "name is required.");
        }

        var name = request.Name.Trim();
        if (await db.Groups.AnyAsync(g => g.Name == name, ct))
        {
            return ApiErrors.Conflict("nameInUse", "A term group with this name already exists.");
        }

        var group = new TermGroup { Id = Ids.New(), Name = name, Description = request.Description };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, group.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/termStore/groups/{group.Id}", ToResponse(group));
    }

    private static async Task<Results<Ok<TermGroupResponse>, ProblemHttpResult>> GetGroupAsync(
        Guid groupId, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, group.Version);
        return TypedResults.Ok(ToResponse(group));
    }

    private static async Task<Results<Ok<TermGroupResponse>, ValidationProblem, ProblemHttpResult>> UpdateGroupAsync(
        Guid groupId, TermGroupRequest request, TaxonomyDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, group, http) is { } precondition)
        {
            return precondition;
        }

        if (request.Name is { } name && name.Trim() != group.Name)
        {
            if (group.IsSystem)
            {
                return ApiErrors.Conflict("systemGroup", "The system group cannot be renamed.");
            }

            name = name.Trim();
            if (await db.Groups.AnyAsync(g => g.Name == name && g.Id != groupId, ct))
            {
                return ApiErrors.Conflict("nameInUse", "A term group with this name already exists.");
            }

            group.Name = name;
        }

        group.Description = request.Description ?? group.Description;
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, group.Version);
        return TypedResults.Ok(ToResponse(group));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteGroupAsync(
        Guid groupId, TaxonomyDbContext db, HttpRequest http, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, group, http) is { } precondition)
        {
            return precondition;
        }

        if (group.IsSystem)
        {
            return ApiErrors.Conflict("systemGroup", "The system group cannot be deleted.");
        }

        if (await db.TermSets.AnyAsync(s => s.GroupId == groupId, ct))
        {
            return ApiErrors.Conflict("groupNotEmpty", "Delete or move the term sets of the group first.");
        }

        db.Groups.Remove(group);
        return await SaveAsync(db, ct) is { } conflict ? conflict : TypedResults.NoContent();
    }
}
