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
}
