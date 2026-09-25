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

public sealed record TermGroupResponse(Guid Id, string Name, string? Description, bool IsSystem, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record TermGroupRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Name,
    [property: StringLength(2000)] string? Description);

public sealed record TermSetResponse(
    Guid Id, Guid GroupId, string Name, string? Description, bool IsOpen, bool IsKeywords, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record CreateTermSetRequest(
    [property: Required] Guid GroupId,
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description,
    bool IsOpen = false);

public sealed record UpdateTermSetRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Name,
    [property: StringLength(2000)] string? Description,
    bool? IsOpen);

public sealed record TermLabelDto(
    [property: Required, StringLength(35, MinimumLength = 2)] string Language,
    [property: Required, StringLength(TermRules.NameMaxLength, MinimumLength = 1)] string Name);

public sealed record TermResponse(
    Guid Id,
    Guid TermSetId,
    Guid? ParentId,
    string Name,
    string? Description,
    string? Color,
    IReadOnlyList<TermLabelDto> Labels,
    IReadOnlyList<string> Synonyms,
    int SortOrder,
    bool IsDeprecated,
    Guid? MergedIntoId,
    bool HasChildren,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record CreateTermRequest(
    [property: Required, StringLength(TermRules.NameMaxLength, MinimumLength = 1)] string Name,
    Guid? ParentId,
    [property: StringLength(2000)] string? Description,
    string? Color,
    IReadOnlyList<TermLabelDto>? Labels,
    IReadOnlyList<string>? Synonyms,
    int SortOrder = 0);

/// <summary>PATCH body: only the properties sent are changed. <c>parentId</c> moves the term (<see cref="MoveToRoot"/> moves it to the root).</summary>
public sealed record UpdateTermRequest(
    [property: StringLength(TermRules.NameMaxLength, MinimumLength = 1)] string? Name,
    [property: StringLength(2000)] string? Description,
    string? Color,
    IReadOnlyList<TermLabelDto>? Labels,
    IReadOnlyList<string>? Synonyms,
    int? SortOrder,
    bool? IsDeprecated,
    Guid? ParentId,
    bool MoveToRoot = false);

public sealed record MergeTermRequest([property: Required] Guid TargetTermId);

public sealed record KeywordRequest([property: Required, StringLength(TermRules.NameMaxLength, MinimumLength = 1)] string Name);

/// <summary>Term store API (SharePoint/Graph <c>termStore</c>): groups → sets → hierarchical terms, plus keywords.</summary>
internal static partial class TermStoreEndpoints
{
    private const int MaxSynonyms = 50;
    private const int MaxLabels = 50;
    private const int KeywordSuggestions = 20;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var store = endpoints.MapV1Group("termStore", "Term store");
        store.MapGet("/groups", ListGroupsAsync).RequireScope(TaxonomyScopes.Read).WithName("ListTermGroups");
        store.MapPost("/groups", CreateGroupAsync).RequireScope(TaxonomyScopes.Manage).WithName("CreateTermGroup");
        store.MapGet("/groups/{groupId:guid}", GetGroupAsync).RequireScope(TaxonomyScopes.Read).WithName("GetTermGroup");
        store.MapPatch("/groups/{groupId:guid}", UpdateGroupAsync).RequireScope(TaxonomyScopes.Manage).WithName("UpdateTermGroup");
        store.MapDelete("/groups/{groupId:guid}", DeleteGroupAsync).RequireScope(TaxonomyScopes.Manage).WithName("DeleteTermGroup");

        store.MapGet("/sets", ListSetsAsync).RequireScope(TaxonomyScopes.Read).WithName("ListTermSets");
        store.MapPost("/sets", CreateSetAsync).RequireScope(TaxonomyScopes.Manage).WithName("CreateTermSet");
        store.MapGet("/sets/{setId:guid}", GetSetAsync).RequireScope(TaxonomyScopes.Read).WithName("GetTermSet");
        store.MapPatch("/sets/{setId:guid}", UpdateSetAsync).RequireScope(TaxonomyScopes.Manage).WithName("UpdateTermSet");
        store.MapDelete("/sets/{setId:guid}", DeleteSetAsync).RequireScope(TaxonomyScopes.Manage).WithName("DeleteTermSet");

        store.MapGet("/sets/{setId:guid}/terms", ListTermsAsync).RequireScope(TaxonomyScopes.Read).WithName("ListTerms");
        store.MapPost("/sets/{setId:guid}/terms", CreateTermAsync).RequireScope(TaxonomyScopes.Read).WithName("CreateTerm");
        store.MapGet("/sets/{setId:guid}/terms/{termId:guid}", GetTermAsync).RequireScope(TaxonomyScopes.Read).WithName("GetTerm");
        store.MapPatch("/sets/{setId:guid}/terms/{termId:guid}", UpdateTermAsync).RequireScope(TaxonomyScopes.Manage).WithName("UpdateTerm");
        store.MapPost("/sets/{setId:guid}/terms/{termId:guid}/merge", MergeTermAsync).RequireScope(TaxonomyScopes.Manage).WithName("MergeTerm");

        store.MapGet("/keywords", SuggestKeywordsAsync).RequireScope(TaxonomyScopes.Read).WithName("SuggestKeywords");
        store.MapPost("/keywords", AddKeywordAsync).RequireScope(TaxonomyScopes.Read).WithName("AddKeyword");
    }

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
