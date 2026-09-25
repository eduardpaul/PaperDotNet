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
    // ---- Helpers ------------------------------------------------------------

    /// <summary>Moves <paramref name="term"/> under <paramref name="parentId"/> and rewrites the paths of its subtree.</summary>
    private static async Task<ValidationProblem?> MoveAsync(TaxonomyDbContext db, Term term, Guid? parentId, CancellationToken ct)
    {
        string? parentPath = null;
        if (parentId is { } id)
        {
            var parent = await db.Terms.AsNoTracking()
                .Where(t => t.Id == id && t.TermSetId == term.TermSetId && t.MergedIntoId == null)
                .Select(t => new { t.Path })
                .FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                return Invalid("parentId", "The parent must be a term of the same term set.");
            }

            if (parent.Path.StartsWith(term.Path, StringComparison.Ordinal))
            {
                return Invalid("parentId", "A term cannot be moved under itself or one of its descendants.");
            }

            parentPath = parent.Path;
        }

        var oldPath = term.Path;
        var newPath = TermRules.PathOf(parentPath, term.Id);
        var descendants = await db.Terms.Where(t => t.Path.StartsWith(oldPath) && t.Id != term.Id).ToListAsync(ct);
        foreach (var descendant in descendants)
        {
            descendant.Path = string.Concat(newPath, descendant.Path.AsSpan(oldPath.Length));
        }

        term.ParentId = parentId;
        term.Path = newPath;
        return null;
    }

    private static async Task<bool> SiblingExistsAsync(TaxonomyDbContext db, Guid setId, Guid? parentId, string name, Guid? except, CancellationToken ct)
    {
        var normalized = TermRules.Normalize(name);
        return await db.Terms.AnyAsync(
            t => t.TermSetId == setId && t.ParentId == parentId && t.NormalizedName == normalized && t.MergedIntoId == null && t.Id != except, ct);
    }

    private static ValidationProblem? ValidateDetails(string? color, IReadOnlyList<TermLabelDto>? labels, IReadOnlyList<string>? synonyms)
    {
        var errors = new Dictionary<string, string[]>();
        if (color is not null && !Color().IsMatch(color))
        {
            errors["color"] = ["A color like #1f77b4 is expected."];
        }

        if (labels is not null)
        {
            if (labels.Count > MaxLabels || labels.Any(l => RequestValidation.Validate(l) is not null))
            {
                errors["labels"] = [$"At most {MaxLabels} labels, each with a language tag and a name."];
            }
            else if (labels.GroupBy(l => l.Language, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            {
                errors["labels"] = ["One label per language."];
            }
        }

        if (synonyms is not null && (synonyms.Count > MaxSynonyms || synonyms.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > TermRules.NameMaxLength)))
        {
            errors["synonyms"] = [$"At most {MaxSynonyms} non-empty synonyms of up to {TermRules.NameMaxLength} characters."];
        }

        return errors.Count == 0 ? null : ApiErrors.Validation(errors);
    }

    private static void ApplyDetails(Term term, string? description, string? color, IReadOnlyList<TermLabelDto>? labels, IReadOnlyList<string>? synonyms, int sortOrder)
    {
        term.Description = description;
        term.Color = color?.ToLowerInvariant();
        term.SortOrder = sortOrder;
        if (labels is not null)
        {
            term.Labels = labels.Select(l => new TermLabel { Language = l.Language.Trim(), Name = l.Name.Trim() }).ToList();
        }

        if (synonyms is not null)
        {
            term.Synonyms = synonyms.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        term.RefreshSearchText();
    }

    private static async Task<List<TermResponse>> ToResponsesAsync(TaxonomyDbContext db, IReadOnlyList<Term> terms, CancellationToken ct)
    {
        var ids = terms.Select(t => t.Id).ToList();
        var withChildren = (await db.Terms.AsNoTracking()
            .Where(t => t.ParentId != null && ids.Contains(t.ParentId.Value) && t.MergedIntoId == null)
            .Select(t => t.ParentId!.Value)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();
        return terms.Select(t => new TermResponse(
            t.Id, t.TermSetId, t.ParentId, t.Name, t.Description, t.Color,
            t.Labels.Select(l => new TermLabelDto(l.Language, l.Name)).ToList(), t.Synonyms,
            t.SortOrder, t.IsDeprecated, t.MergedIntoId, withChildren.Contains(t.Id), t.CreatedAt, t.UpdatedAt)
        { ETag = ETags.From(t.Version) }).ToList();
    }

    private static async Task<bool> HasScopeAsync(ICurrentUser user, IEffectiveScopeProvider scopes, string scope, CancellationToken ct) =>
        user.UserId is { } userId && (await scopes.GetScopesAsync(userId, ct))?.Contains(scope) == true;

    private static ProblemHttpResult? CheckIfMatch<T>(TaxonomyDbContext db, T entity, HttpRequest http)
        where T : class, IVersioned
    {
        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (version != entity.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        db.Entry(entity).Property(e => e.Version).OriginalValue = version;
        return null;
    }

    private static async Task<ProblemHttpResult?> SaveAsync(TaxonomyDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    private static ValidationProblem Invalid(string key, string message) =>
        ApiErrors.Validation(new Dictionary<string, string[]> { [key] = [message] });

    private static TermGroupResponse ToResponse(TermGroup g) => new(g.Id, g.Name, g.Description, g.IsSystem, g.CreatedAt, g.UpdatedAt) { ETag = ETags.From(g.Version) };

    private static TermSetResponse ToResponse(TermSet s) => new(s.Id, s.GroupId, s.Name, s.Description, s.IsOpen, s.IsKeywords, s.CreatedAt, s.UpdatedAt) { ETag = ETags.From(s.Version) };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Color();
}
