using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Taxonomy.Data;

namespace PaperDotNet.Taxonomy.Features;

public sealed record PopularKeyword(Guid Id, string Name, int Usage);

public sealed record PopularKeywordsResponse(IReadOnlyList<PopularKeyword> Value);

/// <summary>Promotes a keyword into <c>termSetId</c> (under <c>parentId</c>, optional).</summary>
public sealed record PromoteKeywordRequest(Guid TermSetId, Guid? ParentId);

/// <summary>Result of a promotion: the term, and whether the keyword was merged into an existing term.</summary>
public sealed record PromoteKeywordResponse(Guid TermId, Guid TermSetId, string Name, bool Merged);

public sealed record TermSetImportResponse(Guid TermSetId, bool Created, int TermsCreated);

/// <summary>
/// Keyword curation (TAX-05) and term set import (TAX-11). Promoting keeps tagged items valid: the keyword
/// moves into the term set (same id) or, when a term of that name exists there, is merged into it; either
/// way the term stays usable in keywords fields.
/// </summary>
internal static class TermSetImport
{
    public const int MaxImportBytes = 1024 * 1024;
    private const int MaxLevels = 7;
    private const int DefaultPopular = 50;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var store = endpoints.MapV1Group("termStore", "Term store");
        store.MapGet("/keywords/popular", PopularAsync).RequireScope(TaxonomyScopes.Manage).WithName("ListPopularKeywords");
        store.MapPost("/keywords/{termId:guid}/promote", PromoteAsync).RequireScope(TaxonomyScopes.Manage).WithName("PromoteKeyword");
        store.MapPost("/groups/{groupId:guid}/import", ImportAsync).RequireScope(TaxonomyScopes.Manage).WithName("ImportTermSet")
            .Accepts<string>("text/csv");
    }

    /// <summary>Active keywords by the number of items tagged with them (most used first).</summary>
    private static async Task<Ok<PopularKeywordsResponse>> PopularAsync(int? top, TaxonomyDbContext db, ITermUsage usage, CancellationToken ct)
    {
        var set = await TermStore.EnsureKeywordsSetAsync(db, ct);
        var keywords = await db.Terms.AsNoTracking()
            .Where(t => t.TermSetId == set.Id && t.MergedIntoId == null && !t.IsDeprecated)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);
        var counts = await usage.CountAsync(keywords.Select(k => k.Id).ToList(), ct);
        var result = keywords
            .Select(k => new PopularKeyword(k.Id, k.Name, counts.GetValueOrDefault(k.Id)))
            .OrderByDescending(k => k.Usage).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(top ?? DefaultPopular, 1, 500))
            .ToList();
        return TypedResults.Ok(new PopularKeywordsResponse(result));
    }

    private static async Task<Results<Ok<PromoteKeywordResponse>, ValidationProblem, ProblemHttpResult>> PromoteAsync(
        Guid termId, PromoteKeywordRequest request, TaxonomyDbContext db, Messaging.IOutbox outbox, ITenantContext tenant, ICurrentUser user, CancellationToken ct)
    {
        var keywords = await TermStore.EnsureKeywordsSetAsync(db, ct);
        var keyword = await db.Terms.FirstOrDefaultAsync(t => t.Id == termId && t.TermSetId == keywords.Id, ct);
        if (keyword is null)
        {
            return ApiErrors.NotFound("The keyword was not found.");
        }

        if (keyword.MergedIntoId is not null || keyword.IsDeprecated)
        {
            return ApiErrors.Conflict("termMerged", "The keyword was merged or deprecated.");
        }

        var target = await db.TermSets.FirstOrDefaultAsync(s => s.Id == request.TermSetId && !s.IsKeywords, ct);
        if (target is null)
        {
            return Invalid("termSetId", "Choose a term set other than the keywords set.");
        }

        Term? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await db.Terms.FirstOrDefaultAsync(t => t.Id == parentId && t.TermSetId == target.Id && t.MergedIntoId == null, ct);
            if (parent is null)
            {
                return Invalid("parentId", "The parent must be an active term of the target term set.");
            }
        }

        // A term of that name already in the target: merge the keyword into it (stored values are rewritten).
        var existing = (await TermStore.FindByLabelAsync(db, target.Id, keyword.Name, ct)).FirstOrDefault(t => !t.IsDeprecated);
        if (existing is not null)
        {
            var into = await db.Terms.FirstAsync(t => t.Id == existing.Id, ct);
            into.AvailableAsKeyword = true;
            into.Synonyms = into.Synonyms.Concat(keyword.Synonyms).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            into.RefreshSearchText();
            keyword.MergedIntoId = into.Id;
            keyword.IsDeprecated = true;
            await outbox.SaveChangesAsync(db,
            [
                new TermMerged
                {
                    TenantId = tenant.TenantId!.Value,
                    TenantIdentifier = tenant.TenantIdentifier!,
                    UserId = user.UserId,
                    TermSetId = keywords.Id,
                    SourceTermId = keyword.Id,
                    TargetTermId = into.Id,
                },
            ], cancellationToken: ct);
            return TypedResults.Ok(new PromoteKeywordResponse(into.Id, target.Id, into.Name, true));
        }

        keyword.TermSetId = target.Id;
        keyword.ParentId = parent?.Id;
        keyword.Path = TermRules.PathOf(parent?.Path, keyword.Id);
        keyword.AvailableAsKeyword = true;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new PromoteKeywordResponse(keyword.Id, target.Id, keyword.Name, false));
    }

    /// <summary>
    /// Imports a term set from CSV in the SharePoint format: a header row, then one row per term with the
    /// columns <c>Term Set Name, Term Set Description, LCID, Available for Tagging, Term Description,
    /// Level 1 Term … Level 7 Term</c>; the first data row names the term set. Additive: an existing set of
    /// that name in the group gets the missing terms.
    /// </summary>
    private static async Task<Results<Ok<TermSetImportResponse>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        Guid groupId, HttpRequest http, TaxonomyDbContext db, TermSetProvisioner provisioner, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            return ApiErrors.NotFound();
        }

        if (http.ContentLength > MaxImportBytes)
        {
            return Invalid("csv", $"The file is larger than {MaxImportBytes / 1024} KB.");
        }

        using var reader = new StreamReader(http.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Length > MaxImportBytes)
        {
            return Invalid("csv", $"The file is larger than {MaxImportBytes / 1024} KB.");
        }

        var (template, error) = Parse(text, group.Name);
        if (template is null)
        {
            return Invalid("csv", error!);
        }

        var result = await provisioner.EnsureInGroupAsync(group.Id, template, ct);
        return TypedResults.Ok(new TermSetImportResponse(result.TermSetId, result.Created, result.TermsCreated));
    }

    /// <summary>Reads the SharePoint term set CSV into a template (the CSV parser comes with .NET).</summary>
    internal static (TermSetTemplate? Template, string? Error) Parse(string csv, string groupName)
    {
        using var parser = new TextFieldParser(new StringReader(csv)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        if (parser.EndOfData)
        {
            return (null, "The file is empty.");
        }

        var header = parser.ReadFields() ?? [];
        var level1 = Array.FindIndex(header, h => string.Equals(h.Trim(), "Level 1 Term", StringComparison.OrdinalIgnoreCase));
        if (level1 < 0 || header.Length < 2)
        {
            return (null, "The header must contain 'Term Set Name', 'Term Set Description' and 'Level 1 Term' … columns (SharePoint format).");
        }

        var descriptionColumn = Array.FindIndex(header, h => string.Equals(h.Trim(), "Term Description", StringComparison.OrdinalIgnoreCase));
        string? setName = null, setDescription = null;
        var roots = new List<MutableTerm>();
        var line = 1;
        try
        {
            while (!parser.EndOfData)
            {
                line++;
                var row = parser.ReadFields() ?? [];
                if (row.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                if (setName is null)
                {
                    setName = row.ElementAtOrDefault(0)?.Trim();
                    setDescription = row.ElementAtOrDefault(1)?.Trim() is { Length: > 0 } d ? d : null;
                }

                var siblings = roots;
                MutableTerm? term = null;
                for (var level = 0; level < MaxLevels && level1 + level < row.Length; level++)
                {
                    var name = row[level1 + level].Trim();
                    if (name.Length == 0)
                    {
                        break;
                    }

                    if (name.Length > TermRules.NameMaxLength)
                    {
                        return (null, $"Line {line}: a term name is longer than {TermRules.NameMaxLength} characters.");
                    }

                    term = siblings.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (term is null)
                    {
                        term = new MutableTerm(name);
                        siblings.Add(term);
                    }

                    siblings = term.Children;
                }

                if (term is not null && descriptionColumn >= 0 && row.ElementAtOrDefault(descriptionColumn)?.Trim() is { Length: > 0 } description)
                {
                    term.Description = description;
                }
            }
        }
        catch (MalformedLineException ex)
        {
            return (null, $"Line {ex.LineNumber}: the line is not valid CSV.");
        }

        if (string.IsNullOrWhiteSpace(setName))
        {
            return (null, "The first data row must name the term set (column 'Term Set Name').");
        }

        return (new TermSetTemplate(groupName, setName, roots.Select(t => t.ToTemplate()).ToList(), setDescription), null);
    }

    private static ValidationProblem Invalid(string field, string message) =>
        ApiErrors.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private sealed class MutableTerm(string name)
    {
        public string Name { get; } = name;

        public string? Description { get; set; }

        public List<MutableTerm> Children { get; } = [];

        public TermTemplate ToTemplate() => new(Name, null, Children.Select(c => c.ToTemplate()).ToList(), Description);
    }
}

/// <summary>Creates term sets and missing terms from templates (TAX-11); never removes or renames.</summary>
internal sealed class TermSetProvisioner(TaxonomyDbContext db, IEnumerable<TermSetTemplate> extensionSets) : ITermSetProvisioning
{
    private const int MaxDepth = 7;

    public async Task<TermSetProvisioningResult> EnsureAsync(TermSetTemplate termSet, CancellationToken cancellationToken)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == termSet.GroupName, cancellationToken);
        if (group is null)
        {
            group = new TermGroup { Id = Ids.New(), Name = termSet.GroupName };
            db.Groups.Add(group);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await EnsureInGroupAsync(group.Id, termSet, cancellationToken);
    }

    public async Task ProvisionExtensionAsync(string extensionId, CancellationToken cancellationToken)
    {
        foreach (var template in extensionSets.Where(s => s.ExtensionId == extensionId))
        {
            await EnsureAsync(template, cancellationToken);
        }
    }

    public async Task<TermSetProvisioningResult> EnsureInGroupAsync(Guid groupId, TermSetTemplate template, CancellationToken ct)
    {
        var set = template.Key is { } key
            ? await db.TermSets.FirstOrDefaultAsync(s => s.Key == key, ct)
            : await db.TermSets.FirstOrDefaultAsync(s => s.GroupId == groupId && s.Name == template.Name, ct);
        var created = set is null;
        if (set is null)
        {
            set = new TermSet
            {
                Id = Ids.New(),
                GroupId = groupId,
                Name = template.Name.Trim(),
                Description = template.Description,
                IsOpen = template.IsOpen,
                Key = template.Key,
                ExtensionId = template.ExtensionId,
            };
            db.TermSets.Add(set);
        }

        var existing = created ? [] : await db.Terms.Where(t => t.TermSetId == set.Id && t.MergedIntoId == null).ToListAsync(ct);
        var count = 0;
        void Add(IReadOnlyList<TermTemplate> terms, Term? parent, int depth)
        {
            if (depth >= MaxDepth)
            {
                return;
            }

            foreach (var template in terms)
            {
                var name = template.Name.Trim();
                if (name.Length is 0 or > TermRules.NameMaxLength)
                {
                    continue;
                }

                var term = existing.FirstOrDefault(t => t.ParentId == parent?.Id && t.NormalizedName == TermRules.Normalize(name));
                if (term is null)
                {
                    term = TermRules.NewTerm(set.Id, parent?.Id, parent?.Path, name);
                    term.Description = template.Description;
                    existing.Add(term);
                    db.Terms.Add(term);
                    count++;
                }

                var synonyms = (template.Synonyms ?? []).Where(s => !term.Synonyms.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                if (synonyms.Count > 0)
                {
                    term.Synonyms = [.. term.Synonyms, .. synonyms];
                    term.RefreshSearchText();
                }

                Add(template.Children ?? [], term, depth + 1);
            }
        }

        Add(template.Terms, null, 0);
        await db.SaveChangesAsync(ct);
        return new TermSetProvisioningResult(set.Id, created, count);
    }
}
