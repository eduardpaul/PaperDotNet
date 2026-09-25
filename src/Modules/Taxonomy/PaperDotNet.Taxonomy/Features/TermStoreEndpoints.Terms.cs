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
    // ---- Terms --------------------------------------------------------------

    /// <summary>
    /// Children of <paramref name="parentId"/> (root terms when omitted), or, with
    /// <paramref name="search"/>, matching terms at any level (name, labels, synonyms).
    /// </summary>
    private static async Task<Results<Ok<Page<TermResponse>>, ProblemHttpResult>> ListTermsAsync(
        Guid setId, Guid? parentId, string? search, bool? includeDeprecated, HttpRequest http, TaxonomyDbContext db, CancellationToken ct)
    {
        if (!await db.TermSets.AnyAsync(s => s.Id == setId, ct))
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.From(http);
        var query = db.Terms.AsNoTracking().Where(t => t.TermSetId == setId && t.MergedIntoId == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = TermRules.Normalize(search);
            query = query.Where(t => t.SearchText.Contains(text));
        }
        else
        {
            query = query.Where(t => t.ParentId == parentId);
        }

        if (includeDeprecated != true)
        {
            query = query.Where(t => !t.IsDeprecated);
        }

        var terms = await query
            .Where(t => page.After == null || t.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(t => t.Id)
            .Take(page.Top + 1)
            .ToListAsync(ct);
        return TypedResults.Ok(Page.Create(await ToResponsesAsync(db, terms, ct), page, http, t => t.Id));
    }

    private static async Task<Results<Ok<TermResponse>, ProblemHttpResult>> GetTermAsync(
        Guid setId, Guid termId, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        var term = await db.Terms.AsNoTracking().FirstOrDefaultAsync(t => t.Id == termId && t.TermSetId == setId, ct);
        if (term is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, term.Version);
        return TypedResults.Ok((await ToResponsesAsync(db, [term], ct))[0]);
    }

    /// <summary>Anyone with <c>taxonomy.read</c> may add terms to open sets; closed sets need <c>taxonomy.manage</c>.</summary>
    private static async Task<Results<Created<TermResponse>, ValidationProblem, ProblemHttpResult>> CreateTermAsync(
        Guid setId, CreateTermRequest request, TaxonomyDbContext db, ICurrentUser user, IEffectiveScopeProvider scopes,
        HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var set = await db.TermSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId, ct);
        if (set is null)
        {
            return ApiErrors.NotFound();
        }

        if (!set.IsOpen && !await HasScopeAsync(user, scopes, TaxonomyScopes.Manage, ct))
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "Only term store managers can add terms to a closed term set.");
        }

        if (ValidateDetails(request.Color, request.Labels, request.Synonyms) is { } detailsError)
        {
            return detailsError;
        }

        Term? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await db.Terms.AsNoTracking().FirstOrDefaultAsync(t => t.Id == parentId && t.TermSetId == setId && t.MergedIntoId == null, ct);
            if (parent is null)
            {
                return Invalid("parentId", "The parent must be a term of the same term set.");
            }
        }

        if (await SiblingExistsAsync(db, setId, request.ParentId, request.Name, null, ct))
        {
            return ApiErrors.Conflict("nameInUse", "A sibling term with this name already exists.");
        }

        var term = TermRules.NewTerm(setId, parent?.Id, parent?.Path, request.Name);
        ApplyDetails(term, request.Description, request.Color, request.Labels, request.Synonyms, request.SortOrder);
        db.Terms.Add(term);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, term.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/termStore/sets/{setId}/terms/{term.Id}", (await ToResponsesAsync(db, [term], ct))[0]);
    }

    private static async Task<Results<Ok<TermResponse>, ValidationProblem, ProblemHttpResult>> UpdateTermAsync(
        Guid setId, Guid termId, UpdateTermRequest request, TaxonomyDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var term = await db.Terms.FirstOrDefaultAsync(t => t.Id == termId && t.TermSetId == setId, ct);
        if (term is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, term, http) is { } precondition)
        {
            return precondition;
        }

        if (term.MergedIntoId is not null)
        {
            return ApiErrors.Conflict("termMerged", "The term was merged into another term and cannot be changed.");
        }

        if (ValidateDetails(request.Color, request.Labels, request.Synonyms) is { } detailsError)
        {
            return detailsError;
        }

        var parentChange = request.MoveToRoot ? (Guid?)null : request.ParentId ?? term.ParentId;
        var moving = parentChange != term.ParentId;
        var name = request.Name?.Trim() ?? term.Name;
        if ((moving || name != term.Name) && await SiblingExistsAsync(db, setId, parentChange, name, term.Id, ct))
        {
            return ApiErrors.Conflict("nameInUse", "A sibling term with this name already exists.");
        }

        if (moving && await MoveAsync(db, term, parentChange, ct) is { } moveError)
        {
            return moveError;
        }

        term.Name = name;
        term.NormalizedName = TermRules.Normalize(name);
        term.IsDeprecated = request.IsDeprecated ?? term.IsDeprecated;
        ApplyDetails(term, request.Description ?? term.Description, request.Color ?? term.Color, request.Labels, request.Synonyms, request.SortOrder ?? term.SortOrder);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, term.Version);
        return TypedResults.Ok((await ToResponsesAsync(db, [term], ct))[0]);
    }

    /// <summary>
    /// Merges the term into <see cref="MergeTermRequest.TargetTermId"/> (SharePoint "merge"):
    /// its children move to the target, its name and synonyms become synonyms of the
    /// target, and stored references are rewritten in the background (<see cref="Contracts.TermMerged"/>).
    /// </summary>
    private static async Task<Results<Ok<TermResponse>, ValidationProblem, ProblemHttpResult>> MergeTermAsync(
        Guid setId, Guid termId, MergeTermRequest request, TaxonomyDbContext db, Messaging.IOutbox outbox,
        ITenantContext tenant, ICurrentUser user, CancellationToken ct)
    {
        var source = await db.Terms.FirstOrDefaultAsync(t => t.Id == termId && t.TermSetId == setId, ct);
        if (source is null)
        {
            return ApiErrors.NotFound();
        }

        if (source.MergedIntoId is not null)
        {
            return ApiErrors.Conflict("termMerged", "The term was already merged.");
        }

        var target = await db.Terms.FirstOrDefaultAsync(t => t.Id == request.TargetTermId && t.TermSetId == setId, ct);
        if (target is null || target.Id == source.Id || target.MergedIntoId is not null || target.IsDeprecated)
        {
            return Invalid("targetTermId", "The target must be another active term of the same term set.");
        }

        if (target.Path.StartsWith(source.Path, StringComparison.Ordinal))
        {
            return Invalid("targetTermId", "A term cannot be merged into one of its descendants.");
        }

        foreach (var child in await db.Terms.Where(t => t.ParentId == source.Id && t.MergedIntoId == null).ToListAsync(ct))
        {
            if (await MoveAsync(db, child, target.Id, ct) is { } moveError)
            {
                return moveError;
            }
        }

        // Earlier merges into the source now point at the target (chains stay one hop).
        foreach (var merged in await db.Terms.Where(t => t.MergedIntoId == source.Id).ToListAsync(ct))
        {
            merged.MergedIntoId = target.Id;
        }

        target.Synonyms = target.Synonyms
            .Concat([source.Name, .. source.Synonyms])
            .Where(s => !string.Equals(s, target.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSynonyms)
            .ToList();
        target.RefreshSearchText();
        source.MergedIntoId = target.Id;
        source.IsDeprecated = true;

        var merge = new Contracts.TermMerged
        {
            TenantId = tenant.TenantId!.Value,
            TenantIdentifier = tenant.TenantIdentifier!,
            UserId = user.UserId,
            TermSetId = setId,
            SourceTermId = source.Id,
            TargetTermId = target.Id,
        };
        await outbox.SaveChangesAsync(db, [merge], cancellationToken: ct);
        return TypedResults.Ok((await ToResponsesAsync(db, [target], ct))[0]);
    }
}
