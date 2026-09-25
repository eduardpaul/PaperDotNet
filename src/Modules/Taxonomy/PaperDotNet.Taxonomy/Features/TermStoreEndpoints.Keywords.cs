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
    // ---- Keywords -----------------------------------------------------------

    /// <summary>Keyword autocomplete: keywords containing <paramref name="search"/>, by name.</summary>
    private static async Task<Ok<List<TermResponse>>> SuggestKeywordsAsync(string? search, TaxonomyDbContext db, CancellationToken ct)
    {
        var set = await TermStore.EnsureKeywordsSetAsync(db, ct);
        var query = db.Terms.AsNoTracking().Where(t => t.TermSetId == set.Id && t.MergedIntoId == null && !t.IsDeprecated);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = TermRules.Normalize(search);
            query = query.Where(t => t.SearchText.Contains(text));
        }

        var terms = await query.OrderBy(t => t.NormalizedName).Take(KeywordSuggestions).ToListAsync(ct);
        return TypedResults.Ok(await ToResponsesAsync(db, terms, ct));
    }

    /// <summary>Returns the keyword with this name, creating it when new (200 existing, 201 created).</summary>
    private static async Task<Results<Ok<TermResponse>, Created<TermResponse>, ValidationProblem, ProblemHttpResult>> AddKeywordAsync(
        KeywordRequest request, TaxonomyDbContext db, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var set = await TermStore.EnsureKeywordsSetAsync(db, ct);
        var matches = await TermStore.FindByLabelAsync(db, set.Id, request.Name, ct);
        if (matches.Count > 0)
        {
            var existing = matches.OrderBy(t => t.IsDeprecated).ThenBy(t => t.Id).First();
            if (existing.IsDeprecated)
            {
                return ApiErrors.Conflict("termDeprecated", "This keyword is deprecated.");
            }

            return TypedResults.Ok((await ToResponsesAsync(db, [existing], ct))[0]);
        }

        var term = TermRules.NewTerm(set.Id, null, null, request.Name);
        db.Terms.Add(term);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"{ApiRoutes.V1}/termStore/sets/{set.Id}/terms/{term.Id}", (await ToResponsesAsync(db, [term], ct))[0]);
    }
}
