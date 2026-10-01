using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Messaging;
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
/// way the term stays usable in keywords fields. Popular keywords are counted from the search index.
/// </summary>
internal static class TermSetImport
{
    public const int MaxImportBytes = 1024 * 1024;
    private const int MaxLevels = 7;
    private const int DefaultPopular = 50;

    public static void Map(IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/v1.0/termStore").WithTags("Term store");
        store.MapGet("/keywords/popular", PopularAsync).RequireScope(TaxonomyScopes.Manage).WithName("ListPopularKeywords")
            .WithDescription("Active keywords by the number of items tagged with them (most used first; ?top=, default 50).");
        store.MapPost("/keywords/{termId:guid}/promote", PromoteAsync).RequireScope(TaxonomyScopes.Manage).WithName("PromoteKeyword");
        store.MapPost("/groups/{groupId:guid}/import", ImportAsync).RequireScope(TaxonomyScopes.Manage).WithName("ImportTermSet")
            .Accepts<string>("text/csv")
            .WithDescription("Imports a term set from CSV in the SharePoint format (up to 1 MB). Additive: an existing set of that name gets the missing terms.");
    }

    private static async Task<Ok<PopularKeywordsResponse>> PopularAsync(int? top, Caller caller, TaxonomyDbContext database, ITermUsage usage, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var setId = (await TermStore.EnsureKeywordsSetAsync(db, tenant, ct)).Id;
        var keywords = await db.Terms.AsNoTracking()
            .Where(t => t.TenantId == tenant && t.TermSetId == setId && t.MergedIntoId == null && !t.IsDeprecated)
            .ToListAsync(ct);
        var counts = await usage.CountAsync(tenant, [.. keywords.Select(k => k.Id)], ct);
        var result = keywords
            .Select(k => new PopularKeyword(k.Id, k.Name, counts.GetValueOrDefault(k.Id)))
            .OrderByDescending(k => k.Usage).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(top ?? DefaultPopular, 1, 500))
            .ToList();
        return TypedResults.Ok(new PopularKeywordsResponse(result));
    }

    private static async Task<Results<Ok<PromoteKeywordResponse>, ValidationProblem, ProblemHttpResult>> PromoteAsync(
        Guid termId, PromoteKeywordRequest request, Caller caller, TaxonomyDbContext database, IOutbox outbox, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var keywords = await TermStore.EnsureKeywordsSetAsync(db, tenant, ct);
        var keyword = await TermStore.FindTermAsync(db, tenant, termId, ct);
        if (keyword is null || keyword.TermSetId != keywords.Id)
        {
            return ApiErrors.NotFound("The keyword was not found.");
        }

        if (keyword.MergedIntoId is not null || keyword.IsDeprecated)
        {
            return ApiErrors.Conflict("termMerged", "The keyword was merged or deprecated.");
        }

        var target = await TermStore.FindSetAsync(db, tenant, request.TermSetId, ct);
        if (target is null || target.IsKeywords)
        {
            return TermStoreEndpoints.Invalid("termSetId", "Choose a term set other than the keywords set.");
        }

        Term? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await TermStore.FindTermAsync(db, tenant, parentId, ct);
            if (parent is null || parent.TermSetId != target.Id || parent.MergedIntoId is not null)
            {
                return TermStoreEndpoints.Invalid("parentId", "The parent must be an active term of the target term set.");
            }
        }

        // A term of that name already in the target: merge the keyword into it (stored values are rewritten).
        var existing = (await TermStore.FindByLabelAsync(db, tenant, target.Id, keyword.Name, ct)).FirstOrDefault(t => !t.IsDeprecated);
        if (existing is not null)
        {
            var into = (await TermStore.FindTermAsync(db, tenant, existing.Id, ct))!;
            into.AvailableAsKeyword = true;
            into.SetSynonyms(into.GetSynonyms().Concat(keyword.GetSynonyms()).Distinct(StringComparer.OrdinalIgnoreCase));
            into.RefreshSearchText();
            keyword.MergedIntoId = into.Id;
            keyword.IsDeprecated = true;
            await outbox.SaveChangesAsync(db,
                [new TermMerged { TenantId = tenant, UserId = caller.UserId, TermSetId = keywords.Id, SourceTermId = keyword.Id, TargetTermId = into.Id }], ct);
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
        Guid groupId, HttpRequest http, Caller caller, TaxonomyDbContext db, CancellationToken ct)
    {
        var group = await TermStore.FindGroupAsync(db, caller.TenantId, groupId, ct);
        if (group is null)
        {
            return ApiErrors.NotFound();
        }

        if (http.ContentLength > MaxImportBytes)
        {
            return TermStoreEndpoints.Invalid("csv", $"The file is larger than {MaxImportBytes / 1024} KB.");
        }

        using var reader = new StreamReader(http.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxImportBytes + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (length > MaxImportBytes)
        {
            return TermStoreEndpoints.Invalid("csv", $"The file is larger than {MaxImportBytes / 1024} KB.");
        }

        var (template, error) = Parse(new string(buffer, 0, length), group.Name);
        if (template is null)
        {
            return TermStoreEndpoints.Invalid("csv", error!);
        }

        var result = await TermSetProvisioner.EnsureInGroupAsync(db, caller.TenantId, group.Id, template, ct);
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

        return (new TermSetTemplate(groupName, setName, [.. roots.Select(t => t.ToTemplate())], setDescription), null);
    }

    private sealed class MutableTerm(string name)
    {
        public string Name { get; } = name;

        public string? Description { get; set; }

        public List<MutableTerm> Children { get; } = [];

        public TermTemplate ToTemplate() => new(Name, null, [.. Children.Select(c => c.ToTemplate())], Description);
    }
}

/// <summary>Creates term sets and missing terms from templates (TAX-11); never removes or renames.</summary>
internal sealed class TermSetProvisioner(TaxonomyDbContext db, IEnumerable<TermSetTemplate> extensionSets) : ITermSetProvisioning
{
    private const int MaxDepth = 7;

    public async Task<TermSetProvisioningResult> EnsureAsync(Guid tenantId, TermSetTemplate termSet, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var name = termSet.GroupName;
        var ct = cancellationToken;
        var group = await context.Groups.FirstOrDefaultAsync(g => g.TenantId == tenant && g.Name == name, ct);
        if (group is null)
        {
            group = new TermGroup { Id = Ids.New(), TenantId = tenant, Name = name };
            context.Groups.Add(group);
            await context.SaveChangesAsync(ct);
        }

        return await EnsureInGroupAsync(context, tenant, group.Id, termSet, ct);
    }

    public async Task ProvisionExtensionAsync(Guid tenantId, string extensionId, CancellationToken cancellationToken)
    {
        foreach (var template in extensionSets.Where(s => s.ExtensionId == extensionId))
        {
            await EnsureAsync(tenantId, template, cancellationToken);
        }
    }

    public static async Task<TermSetProvisioningResult> EnsureInGroupAsync(
        TaxonomyDbContext database, Guid tenantId, Guid groupId, TermSetTemplate template, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var group = groupId;
        var name = template.Name.Trim();
        var ct = cancellationToken;
        var set = template.Key is { } key
            ? await db.TermSets.FirstOrDefaultAsync(s => s.TenantId == tenant && s.Key == key, ct)
            : await db.TermSets.FirstOrDefaultAsync(s => s.TenantId == tenant && s.GroupId == group && s.Name == name, ct);
        var created = set is null;
        if (set is null)
        {
            set = new TermSet
            {
                Id = Ids.New(),
                TenantId = tenant,
                GroupId = group,
                Name = name,
                Description = template.Description,
                IsOpen = template.IsOpen,
                Key = template.Key,
                ExtensionId = template.ExtensionId,
            };
            db.TermSets.Add(set);
        }

        var setId = set.Id;
        var existing = created ? [] : await db.Terms.Where(t => t.TenantId == tenant && t.TermSetId == setId && t.MergedIntoId == null).ToListAsync(ct);
        var count = 0;
        void Add(IReadOnlyList<TermTemplate> terms, Term? parent, int depth)
        {
            if (depth >= MaxDepth)
            {
                return;
            }

            foreach (var child in terms)
            {
                var termName = child.Name.Trim();
                if (termName.Length is 0 or > TermRules.NameMaxLength)
                {
                    continue;
                }

                var normalized = TermRules.Normalize(termName);
                var term = existing.FirstOrDefault(t => t.ParentId == parent?.Id && t.NormalizedName == normalized);
                if (term is null)
                {
                    term = TermRules.NewTerm(tenant, setId, parent?.Id, parent?.Path, termName);
                    term.Description = child.Description;
                    existing.Add(term);
                    db.Terms.Add(term);
                    count++;
                }

                var current = term.GetSynonyms();
                var synonyms = (child.Synonyms ?? []).Where(s => !current.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                if (synonyms.Count > 0)
                {
                    term.SetSynonyms([.. current, .. synonyms]);
                    term.RefreshSearchText();
                }

                Add(child.Children ?? [], term, depth + 1);
            }
        }

        Add(template.Terms, null, 0);
        await db.SaveChangesAsync(ct);
        return new TermSetProvisioningResult(setId, created, count);
    }
}
