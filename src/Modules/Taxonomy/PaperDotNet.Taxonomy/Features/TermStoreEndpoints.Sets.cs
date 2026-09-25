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
    // ---- Term sets ----------------------------------------------------------

    private static async Task<Ok<Page<TermSetResponse>>> ListSetsAsync(
        Guid? groupId, HttpRequest http, TaxonomyDbContext db, CancellationToken ct)
    {
        await TermStore.EnsureKeywordsSetAsync(db, ct);
        var page = PageRequest.From(http);
        var items = await db.TermSets.AsNoTracking()
            .Where(s => groupId == null || s.GroupId == groupId)
            .Where(s => page.After == null || s.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(s => s.Id)
            .Take(page.Top + 1)
            .Select(s => new TermSetResponse(s.Id, s.GroupId, s.Name, s.Description, s.IsOpen, s.IsKeywords, s.CreatedAt, s.UpdatedAt) { ETag = ETags.From(s.Version) })
            .ToListAsync(ct);
        return TypedResults.Ok(Page.Create(items, page, http, s => s.Id));
    }

    private static async Task<Results<Created<TermSetResponse>, ValidationProblem, ProblemHttpResult>> CreateSetAsync(
        CreateTermSetRequest request, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);
        if (group is null)
        {
            return Invalid("groupId", "The term group does not exist.");
        }

        if (group.IsSystem)
        {
            return ApiErrors.Conflict("systemGroup", "Term sets cannot be added to the system group.");
        }

        var name = request.Name.Trim();
        if (await db.TermSets.AnyAsync(s => s.GroupId == group.Id && s.Name == name, ct))
        {
            return ApiErrors.Conflict("nameInUse", "The group already has a term set with this name.");
        }

        var set = new TermSet { Id = Ids.New(), GroupId = group.Id, Name = name, Description = request.Description, IsOpen = request.IsOpen };
        db.TermSets.Add(set);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, set.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/termStore/sets/{set.Id}", ToResponse(set));
    }

    private static async Task<Results<Ok<TermSetResponse>, ProblemHttpResult>> GetSetAsync(
        Guid setId, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        var set = await db.TermSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId, ct);
        if (set is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, set.Version);
        return TypedResults.Ok(ToResponse(set));
    }

    private static async Task<Results<Ok<TermSetResponse>, ValidationProblem, ProblemHttpResult>> UpdateSetAsync(
        Guid setId, UpdateTermSetRequest request, TaxonomyDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var set = await db.TermSets.FirstOrDefaultAsync(s => s.Id == setId, ct);
        if (set is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, set, http) is { } precondition)
        {
            return precondition;
        }

        if (set.IsKeywords && ((request.Name is not null && request.Name.Trim() != set.Name) || request.IsOpen == false))
        {
            return ApiErrors.Conflict("keywordsSet", "The keywords set cannot be renamed or closed.");
        }

        if (request.Name is { } name && name.Trim() != set.Name)
        {
            name = name.Trim();
            if (await db.TermSets.AnyAsync(s => s.GroupId == set.GroupId && s.Name == name && s.Id != setId, ct))
            {
                return ApiErrors.Conflict("nameInUse", "The group already has a term set with this name.");
            }

            set.Name = name;
        }

        set.Description = request.Description ?? set.Description;
        set.IsOpen = request.IsOpen ?? set.IsOpen;
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, set.Version);
        return TypedResults.Ok(ToResponse(set));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteSetAsync(
        Guid setId, TaxonomyDbContext db, HttpRequest http, CancellationToken ct)
    {
        var set = await db.TermSets.FirstOrDefaultAsync(s => s.Id == setId, ct);
        if (set is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, set, http) is { } precondition)
        {
            return precondition;
        }

        if (set.IsKeywords)
        {
            return ApiErrors.Conflict("keywordsSet", "The keywords set cannot be deleted.");
        }

        // Terms are never deleted (items may reference them); a set with terms stays.
        if (await db.Terms.AnyAsync(t => t.TermSetId == setId, ct))
        {
            return ApiErrors.Conflict("termSetNotEmpty", "Only empty term sets can be deleted; deprecate terms instead.");
        }

        db.TermSets.Remove(set);
        return await SaveAsync(db, ct) is { } conflict ? conflict : TypedResults.NoContent();
    }
}
