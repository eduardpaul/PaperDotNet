using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Messaging;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Taxonomy.Data;

namespace PaperDotNet.Taxonomy.Features;

public sealed record TermGroupResponse(Guid Id, string Name, string? Description, bool IsSystem, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record TermGroupRequest(string? Name, string? Description);

public sealed record TermSetResponse(
    Guid Id, Guid GroupId, string Name, string? Description, bool IsOpen, bool IsKeywords, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record CreateTermSetRequest(Guid GroupId, string? Name, string? Description, bool IsOpen = false);

public sealed record UpdateTermSetRequest(string? Name, string? Description, bool? IsOpen);

public sealed record TermLabelDto(string Language, string Name);

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
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record CreateTermRequest(
    string? Name, Guid? ParentId, string? Description, string? Color, IReadOnlyList<TermLabelDto>? Labels, IReadOnlyList<string>? Synonyms, int SortOrder = 0);

/// <summary>PATCH body: only the properties sent are changed. <c>parentId</c> moves the term (<see cref="MoveToRoot"/> moves it to the root).</summary>
public sealed record UpdateTermRequest(
    string? Name,
    string? Description,
    string? Color,
    IReadOnlyList<TermLabelDto>? Labels,
    IReadOnlyList<string>? Synonyms,
    int? SortOrder,
    bool? IsDeprecated,
    Guid? ParentId,
    bool MoveToRoot = false);

public sealed record MergeTermRequest(Guid TargetTermId);

public sealed record KeywordRequest(string? Name);

/// <summary>Term store API (SharePoint/Graph <c>termStore</c>): groups → sets → hierarchical terms, plus keywords.</summary>
internal static partial class TermStoreEndpoints
{
    private const int MaxSynonyms = 50;
    private const int MaxLabels = 50;
    private const int KeywordSuggestions = 20;
    private const int MaxTermsById = 200;

    public static void Map(IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/v1.0/termStore").WithTags("Term store");
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

        store.MapGet("/sets/{setId:guid}/terms", ListTermsAsync).RequireScope(TaxonomyScopes.Read).WithName("ListTerms")
            .WithDescription("Children of parentId (root terms when omitted), or, with search, matching terms at any level (name, labels, synonyms).");
        store.MapPost("/sets/{setId:guid}/terms", CreateTermAsync).RequireScope(TaxonomyScopes.Read).WithName("CreateTerm")
            .WithDescription("Anyone with taxonomy.read may add terms to open sets; closed sets need taxonomy.manage.");
        store.MapGet("/sets/{setId:guid}/terms/{termId:guid}", GetTermAsync).RequireScope(TaxonomyScopes.Read).WithName("GetTerm");
        store.MapPatch("/sets/{setId:guid}/terms/{termId:guid}", UpdateTermAsync).RequireScope(TaxonomyScopes.Manage).WithName("UpdateTerm");
        store.MapPost("/sets/{setId:guid}/terms/{termId:guid}/merge", MergeTermAsync).RequireScope(TaxonomyScopes.Manage).WithName("MergeTerm")
            .WithDescription("Merges the term into targetTermId: its children move to the target, its name and synonyms become synonyms of the target, and stored references are rewritten in the background.");

        store.MapGet("/terms", GetTermsByIdAsync).RequireScope(TaxonomyScopes.Read).WithName("GetTermsById")
            .WithDescription("Terms by id in any set: ?ids=a,b,c (at most 200). Unknown ids are left out.");
        store.MapGet("/keywords", SuggestKeywordsAsync).RequireScope(TaxonomyScopes.Read).WithName("SuggestKeywords");
        store.MapPost("/keywords", AddKeywordAsync).RequireScope(TaxonomyScopes.Read).WithName("AddKeyword")
            .WithDescription("Returns the keyword with this name, creating it when new (200 existing, 201 created).");
    }

    // ---- Groups -------------------------------------------------------------

    private static async Task<Ok<Page<TermGroupResponse>>> ListGroupsAsync(
        HttpRequest http, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, TaxonomyDbContext database, CancellationToken cancellationToken)
    {
        await TermStore.EnsureKeywordsSetAsync(database, caller.TenantId, cancellationToken);
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var groups = page.After is { } after
            ? await db.Groups.AsNoTracking().Where(g => g.TenantId == tenant && g.Id.CompareTo(after) > 0).OrderBy(g => g.Id).Take(take).ToListAsync(ct)
            : await db.Groups.AsNoTracking().Where(g => g.TenantId == tenant).OrderBy(g => g.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. groups.Select(ToResponse)], page, http, g => g.Id));
    }

    private static async Task<Results<Created<TermGroupResponse>, ValidationProblem, ProblemHttpResult>> CreateGroupAsync(
        TermGroupRequest request, Caller caller, TaxonomyDbContext database, HttpResponse response, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || request.Description?.Length > 2000)
        {
            return Invalid("name", "A name of 1 to 200 characters is required (descriptions up to 2000).");
        }

        var db = database;
        var tenant = caller.TenantId;
        var name = request.Name.Trim();
        var ct = cancellationToken;
        if (await db.Groups.AnyAsync(g => g.TenantId == tenant && g.Name == name, ct))
        {
            return ApiErrors.Conflict("nameInUse", "A term group with this name already exists.");
        }

        var group = new TermGroup { Id = Ids.New(), TenantId = tenant, Name = name, Description = request.Description };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, group.Version);
        return TypedResults.Created($"/v1.0/termStore/groups/{group.Id}", ToResponse(group));
    }

    private static async Task<Results<Ok<TermGroupResponse>, ProblemHttpResult>> GetGroupAsync(
        Guid groupId, Caller caller, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await TermStore.FindGroupAsync(db, caller.TenantId, groupId, ct) is not { } group)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, group.Version);
        return TypedResults.Ok(ToResponse(group));
    }

    private static async Task<Results<Ok<TermGroupResponse>, ValidationProblem, ProblemHttpResult>> UpdateGroupAsync(
        Guid groupId, TermGroupRequest request, Caller caller, TaxonomyDbContext database, HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        if ((request.Name is { } n && (n.Trim().Length is 0 or > 200)) || request.Description?.Length > 2000)
        {
            return Invalid("name", "A name of 1 to 200 characters is expected (descriptions up to 2000).");
        }

        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        if (await TermStore.FindGroupAsync(db, tenant, groupId, ct) is not { } group)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, group, http) is { } precondition)
        {
            return precondition;
        }

        if (request.Name?.Trim() is { } name && name != group.Name)
        {
            if (group.IsSystem)
            {
                return ApiErrors.Conflict("systemGroup", "The system group cannot be renamed.");
            }

            var id = groupId;
            if (await db.Groups.AnyAsync(g => g.TenantId == tenant && g.Name == name && g.Id != id, ct))
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
        Guid groupId, Caller caller, TaxonomyDbContext database, HttpRequest http, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var id = groupId;
        var ct = cancellationToken;
        if (await TermStore.FindGroupAsync(db, tenant, id, ct) is not { } group)
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

        if (await db.TermSets.AnyAsync(s => s.TenantId == tenant && s.GroupId == id, ct))
        {
            return ApiErrors.Conflict("groupNotEmpty", "Delete or move the term sets of the group first.");
        }

        db.Groups.Remove(group);
        return await SaveAsync(db, ct) is { } conflict ? conflict : TypedResults.NoContent();
    }

    // ---- Term sets ----------------------------------------------------------

    private static async Task<Ok<Page<TermSetResponse>>> ListSetsAsync(
        Guid? groupId, HttpRequest http, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, TaxonomyDbContext database, CancellationToken cancellationToken)
    {
        await TermStore.EnsureKeywordsSetAsync(database, caller.TenantId, cancellationToken);
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var after = page.After ?? Guid.Empty;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var sets = groupId is { } group
            ? await db.TermSets.AsNoTracking().Where(s => s.TenantId == tenant && s.GroupId == group && s.Id.CompareTo(after) > 0).OrderBy(s => s.Id).Take(take).ToListAsync(ct)
            : await db.TermSets.AsNoTracking().Where(s => s.TenantId == tenant && s.Id.CompareTo(after) > 0).OrderBy(s => s.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. sets.Select(ToResponse)], page, http, s => s.Id));
    }

    private static async Task<Results<Created<TermSetResponse>, ValidationProblem, ProblemHttpResult>> CreateSetAsync(
        CreateTermSetRequest request, Caller caller, TaxonomyDbContext database, HttpResponse response, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || request.Description?.Length > 2000)
        {
            return Invalid("name", "A name of 1 to 200 characters is required (descriptions up to 2000).");
        }

        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        if (await TermStore.FindGroupAsync(db, tenant, request.GroupId, ct) is not { } group)
        {
            return Invalid("groupId", "The term group does not exist.");
        }

        if (group.IsSystem)
        {
            return ApiErrors.Conflict("systemGroup", "Term sets cannot be added to the system group.");
        }

        var name = request.Name.Trim();
        var groupId = group.Id;
        if (await db.TermSets.AnyAsync(s => s.TenantId == tenant && s.GroupId == groupId && s.Name == name, ct))
        {
            return ApiErrors.Conflict("nameInUse", "The group already has a term set with this name.");
        }

        var set = new TermSet { Id = Ids.New(), TenantId = tenant, GroupId = groupId, Name = name, Description = request.Description, IsOpen = request.IsOpen };
        db.TermSets.Add(set);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, set.Version);
        return TypedResults.Created($"/v1.0/termStore/sets/{set.Id}", ToResponse(set));
    }

    private static async Task<Results<Ok<TermSetResponse>, ProblemHttpResult>> GetSetAsync(
        Guid setId, Caller caller, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await TermStore.FindSetAsync(db, caller.TenantId, setId, ct) is not { } set)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, set.Version);
        return TypedResults.Ok(ToResponse(set));
    }

    private static async Task<Results<Ok<TermSetResponse>, ValidationProblem, ProblemHttpResult>> UpdateSetAsync(
        Guid setId, UpdateTermSetRequest request, Caller caller, TaxonomyDbContext database, HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        if ((request.Name is { } n && (n.Trim().Length is 0 or > 200)) || request.Description?.Length > 2000)
        {
            return Invalid("name", "A name of 1 to 200 characters is expected (descriptions up to 2000).");
        }

        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        if (await TermStore.FindSetAsync(db, tenant, setId, ct) is not { } set)
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

        if (request.Name?.Trim() is { } name && name != set.Name)
        {
            var groupId = set.GroupId;
            var id = setId;
            if (await db.TermSets.AnyAsync(s => s.TenantId == tenant && s.GroupId == groupId && s.Name == name && s.Id != id, ct))
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
        Guid setId, Caller caller, TaxonomyDbContext database, HttpRequest http, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var id = setId;
        var ct = cancellationToken;
        if (await TermStore.FindSetAsync(db, tenant, id, ct) is not { } set)
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
        if (await db.Terms.AnyAsync(t => t.TenantId == tenant && t.TermSetId == id, ct))
        {
            return ApiErrors.Conflict("termSetNotEmpty", "Only empty term sets can be deleted; deprecate terms instead.");
        }

        db.TermSets.Remove(set);
        return await SaveAsync(db, ct) is { } conflict ? conflict : TypedResults.NoContent();
    }

    // ---- Terms --------------------------------------------------------------

    private static async Task<Results<Ok<Page<TermResponse>>, ProblemHttpResult>> ListTermsAsync(
        Guid setId, Guid? parentId, string? search, bool? includeDeprecated, HttpRequest http,
        [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, TaxonomyDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        if (await TermStore.FindSetAsync(db, tenant, setId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.Create(top, skipToken);
        var set = setId;
        var after = page.After ?? Guid.Empty;
        var take = page.Top + 1;
        var deprecated = includeDeprecated == true;
        List<Term> terms;

        // Static queries only (precompiled): one per combination of options.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = TermRules.Normalize(search);
            terms = await db.Terms.AsNoTracking()
                .Where(t => t.TenantId == tenant && t.TermSetId == set && t.MergedIntoId == null && (!t.IsDeprecated || t.IsDeprecated == deprecated) && t.SearchText.Contains(text) && t.Id.CompareTo(after) > 0)
                .OrderBy(t => t.Id).Take(take).ToListAsync(ct);
        }
        else if (parentId is { } parent)
        {
            terms = await db.Terms.AsNoTracking()
                .Where(t => t.TenantId == tenant && t.TermSetId == set && t.MergedIntoId == null && (!t.IsDeprecated || t.IsDeprecated == deprecated) && t.ParentId == parent && t.Id.CompareTo(after) > 0)
                .OrderBy(t => t.Id).Take(take).ToListAsync(ct);
        }
        else
        {
            terms = await db.Terms.AsNoTracking()
                .Where(t => t.TenantId == tenant && t.TermSetId == set && t.MergedIntoId == null && (!t.IsDeprecated || t.IsDeprecated == deprecated) && t.ParentId == null && t.Id.CompareTo(after) > 0)
                .OrderBy(t => t.Id).Take(take).ToListAsync(ct);
        }

        return TypedResults.Ok(Page.Create(await ToResponsesAsync(db, tenant, terms, ct), page, http, t => t.Id));
    }

    private static async Task<Results<Ok<List<TermResponse>>, ValidationProblem>> GetTermsByIdAsync(string? ids, Caller caller, TaxonomyDbContext db, CancellationToken ct)
    {
        var wanted = new HashSet<Guid>();
        foreach (var part in (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id))
            {
                return Invalid("ids", $"'{part}' is not a term id.");
            }

            wanted.Add(id);
        }

        if (wanted.Count is 0 or > MaxTermsById)
        {
            return Invalid("ids", $"Give 1 to {MaxTermsById} term ids, separated by commas.");
        }

        var terms = new List<Term>();
        foreach (var id in wanted)
        {
            if (await TermStore.FindTermAsync(db, caller.TenantId, id, ct) is { } term)
            {
                terms.Add(term);
            }
        }

        return TypedResults.Ok(await ToResponsesAsync(db, caller.TenantId, terms, ct));
    }

    private static async Task<Results<Ok<TermResponse>, ProblemHttpResult>> GetTermAsync(
        Guid setId, Guid termId, Caller caller, TaxonomyDbContext db, HttpResponse response, CancellationToken ct)
    {
        var term = await TermStore.FindTermAsync(db, caller.TenantId, termId, ct);
        if (term is null || term.TermSetId != setId)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, term.Version);
        return TypedResults.Ok((await ToResponsesAsync(db, caller.TenantId, [term], ct))[0]);
    }

    private static async Task<Results<Created<TermResponse>, ValidationProblem, ProblemHttpResult>> CreateTermAsync(
        Guid setId, CreateTermRequest request, Caller caller, TaxonomyDbContext db, IEffectiveScopeProvider scopes, HttpResponse response, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > TermRules.NameMaxLength || request.Description?.Length > 2000)
        {
            return Invalid("name", $"A name of 1 to {TermRules.NameMaxLength} characters is required (descriptions up to 2000).");
        }

        var tenant = caller.TenantId;
        if (await TermStore.FindSetAsync(db, tenant, setId, ct) is not { } set)
        {
            return ApiErrors.NotFound();
        }

        if (!set.IsOpen && (await scopes.GetScopesAsync(tenant, caller.UserId, ct))?.Contains(TaxonomyScopes.Manage) != true)
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
            parent = await TermStore.FindTermAsync(db, tenant, parentId, ct);
            if (parent is null || parent.TermSetId != setId || parent.MergedIntoId is not null)
            {
                return Invalid("parentId", "The parent must be a term of the same term set.");
            }
        }

        if (await TermStore.FindChildAsync(db, tenant, setId, request.ParentId, TermRules.Normalize(request.Name), ct) is not null)
        {
            return ApiErrors.Conflict("nameInUse", "A sibling term with this name already exists.");
        }

        var term = TermRules.NewTerm(tenant, setId, parent?.Id, parent?.Path, request.Name);
        ApplyDetails(term, request.Description, request.Color, request.Labels, request.Synonyms, request.SortOrder);
        db.Terms.Add(term);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, term.Version);
        return TypedResults.Created($"/v1.0/termStore/sets/{setId}/terms/{term.Id}", (await ToResponsesAsync(db, tenant, [term], ct))[0]);
    }

    private static async Task<Results<Ok<TermResponse>, ValidationProblem, ProblemHttpResult>> UpdateTermAsync(
        Guid setId, Guid termId, UpdateTermRequest request, Caller caller, TaxonomyDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if ((request.Name is { } n && (n.Trim().Length is 0 or > TermRules.NameMaxLength)) || request.Description?.Length > 2000)
        {
            return Invalid("name", $"A name of 1 to {TermRules.NameMaxLength} characters is expected (descriptions up to 2000).");
        }

        var tenant = caller.TenantId;
        var term = await TermStore.FindTermAsync(db, tenant, termId, ct);
        if (term is null || term.TermSetId != setId)
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
        if ((moving || name != term.Name)
            && await TermStore.FindChildAsync(db, tenant, setId, parentChange, TermRules.Normalize(name), ct) is { } sibling && sibling != term.Id)
        {
            return ApiErrors.Conflict("nameInUse", "A sibling term with this name already exists.");
        }

        if (moving && await MoveAsync(db, tenant, term, parentChange, ct) is { } moveError)
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
        return TypedResults.Ok((await ToResponsesAsync(db, tenant, [term], ct))[0]);
    }

    private static async Task<Results<Ok<TermResponse>, ValidationProblem, ProblemHttpResult>> MergeTermAsync(
        Guid setId, Guid termId, MergeTermRequest request, Caller caller, TaxonomyDbContext database, IOutbox outbox, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var source = await TermStore.FindTermAsync(db, tenant, termId, ct);
        if (source is null || source.TermSetId != setId)
        {
            return ApiErrors.NotFound();
        }

        if (source.MergedIntoId is not null)
        {
            return ApiErrors.Conflict("termMerged", "The term was already merged.");
        }

        var target = await TermStore.FindTermAsync(db, tenant, request.TargetTermId, ct);
        if (target is null || target.TermSetId != setId || target.Id == source.Id || target.MergedIntoId is not null || target.IsDeprecated)
        {
            return Invalid("targetTermId", "The target must be another active term of the same term set.");
        }

        if (target.Path.StartsWith(source.Path, StringComparison.Ordinal))
        {
            return Invalid("targetTermId", "A term cannot be merged into one of its descendants.");
        }

        var sourceId = source.Id;
        foreach (var child in await db.Terms.Where(t => t.TenantId == tenant && t.ParentId == sourceId && t.MergedIntoId == null).ToListAsync(ct))
        {
            if (await MoveAsync(db, tenant, child, target.Id, ct) is { } moveError)
            {
                return moveError;
            }
        }

        // Earlier merges into the source now point at the target (chains stay one hop).
        foreach (var merged in await db.Terms.Where(t => t.TenantId == tenant && t.MergedIntoId == sourceId).ToListAsync(ct))
        {
            merged.MergedIntoId = target.Id;
        }

        target.SetSynonyms(target.GetSynonyms()
            .Concat([source.Name, .. source.GetSynonyms()])
            .Where(s => !string.Equals(s, target.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSynonyms));
        target.RefreshSearchText();
        source.MergedIntoId = target.Id;
        source.IsDeprecated = true;
        await outbox.SaveChangesAsync(db,
            [new TermMerged { TenantId = tenant, UserId = caller.UserId, TermSetId = setId, SourceTermId = source.Id, TargetTermId = target.Id }], ct);
        return TypedResults.Ok((await ToResponsesAsync(db, tenant, [target], ct))[0]);
    }

    // ---- Keywords -----------------------------------------------------------

    private static async Task<Ok<List<TermResponse>>> SuggestKeywordsAsync(string? search, Caller caller, TaxonomyDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var set = await TermStore.EnsureKeywordsSetAsync(db, tenant, ct);
        var setId = set.Id;
        var take = KeywordSuggestions;
        var text = string.IsNullOrWhiteSpace(search) ? "" : TermRules.Normalize(search);
        var terms = await db.Terms.AsNoTracking()
            .Where(t => t.TenantId == tenant && t.TermSetId == setId && t.MergedIntoId == null && !t.IsDeprecated && t.SearchText.Contains(text))
            .OrderBy(t => t.NormalizedName).Take(take).ToListAsync(ct);
        return TypedResults.Ok(await ToResponsesAsync(db, tenant, terms, ct));
    }

    private static async Task<Results<Ok<TermResponse>, Created<TermResponse>, ValidationProblem, ProblemHttpResult>> AddKeywordAsync(
        KeywordRequest request, Caller caller, TaxonomyDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > TermRules.NameMaxLength)
        {
            return Invalid("name", $"A name of 1 to {TermRules.NameMaxLength} characters is required.");
        }

        var tenant = caller.TenantId;
        var set = await TermStore.EnsureKeywordsSetAsync(db, tenant, ct);
        var matches = await TermStore.FindByLabelAsync(db, tenant, set.Id, request.Name, ct);
        if (matches.Count > 0)
        {
            var existing = matches.OrderBy(t => t.IsDeprecated).ThenBy(t => t.Id).First();
            if (existing.IsDeprecated)
            {
                return ApiErrors.Conflict("termDeprecated", "This keyword is deprecated.");
            }

            return TypedResults.Ok((await ToResponsesAsync(db, tenant, [existing], ct))[0]);
        }

        var term = TermRules.NewTerm(tenant, set.Id, null, null, request.Name);
        db.Terms.Add(term);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/v1.0/termStore/sets/{set.Id}/terms/{term.Id}", (await ToResponsesAsync(db, tenant, [term], ct))[0]);
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>Moves <paramref name="term"/> under <paramref name="parentId"/> and rewrites the paths of its subtree.</summary>
    internal static async Task<ValidationProblem?> MoveAsync(TaxonomyDbContext database, Guid tenantId, Term term, Guid? parentId, CancellationToken cancellationToken)
    {
        string? parentPath = null;
        if (parentId is { } id)
        {
            var parent = await TermStore.FindTermAsync(database, tenantId, id, cancellationToken);
            if (parent is null || parent.TermSetId != term.TermSetId || parent.MergedIntoId is not null)
            {
                return Invalid("parentId", "The parent must be a term of the same term set.");
            }

            if (parent.Path.StartsWith(term.Path, StringComparison.Ordinal))
            {
                return Invalid("parentId", "A term cannot be moved under itself or one of its descendants.");
            }

            parentPath = parent.Path;
        }

        var db = database;
        var tenant = tenantId;
        var oldPath = term.Path;
        var termId = term.Id;
        var ct = cancellationToken;
        var newPath = TermRules.PathOf(parentPath, term.Id);
        foreach (var descendant in await db.Terms.Where(t => t.TenantId == tenant && t.Path.StartsWith(oldPath) && t.Id != termId).ToListAsync(ct))
        {
            descendant.Path = string.Concat(newPath, descendant.Path.AsSpan(oldPath.Length));
        }

        term.ParentId = parentId;
        term.Path = newPath;
        return null;
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
            if (labels.Count > MaxLabels
                || labels.Any(l => string.IsNullOrWhiteSpace(l.Language) || l.Language.Trim().Length is < 2 or > 35 || string.IsNullOrWhiteSpace(l.Name) || l.Name.Trim().Length > TermRules.NameMaxLength))
            {
                errors["labels"] = [$"At most {MaxLabels} labels, each with a language tag and a name."];
            }
            else if (labels.GroupBy(l => l.Language.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
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
            term.SetLabels(labels.Select(l => new TermLabel(l.Language.Trim(), l.Name.Trim())));
        }

        if (synonyms is not null)
        {
            term.SetSynonyms(synonyms.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        term.RefreshSearchText();
    }

    private static async Task<List<TermResponse>> ToResponsesAsync(TaxonomyDbContext database, Guid tenantId, IReadOnlyList<Term> terms, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        var result = new List<TermResponse>();
        foreach (var t in terms)
        {
            var id = t.Id;
            var hasChildren = await db.Terms.AnyAsync(c => c.TenantId == tenant && c.ParentId == id && c.MergedIntoId == null, ct);
            result.Add(new TermResponse(
                t.Id, t.TermSetId, t.ParentId, t.Name, t.Description, t.Color,
                [.. t.GetLabels().Select(l => new TermLabelDto(l.Language, l.Name))], t.GetSynonyms(),
                t.SortOrder, t.IsDeprecated, t.MergedIntoId, hasChildren, t.CreatedAt, t.UpdatedAt)
            { ETag = ETags.From(t.Version) });
        }

        return result;
    }

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

    internal static ValidationProblem Invalid(string key, string message) => ApiErrors.Validation(key, message);

    private static TermGroupResponse ToResponse(TermGroup g) => new(g.Id, g.Name, g.Description, g.IsSystem, g.CreatedAt, g.UpdatedAt) { ETag = ETags.From(g.Version) };

    private static TermSetResponse ToResponse(TermSet s) => new(s.Id, s.GroupId, s.Name, s.Description, s.IsOpen, s.IsKeywords, s.CreatedAt, s.UpdatedAt) { ETag = ETags.From(s.Version) };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Color();
}
