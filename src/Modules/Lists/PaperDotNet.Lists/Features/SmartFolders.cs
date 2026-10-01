using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>One level of metadata navigation: a field, optionally by <c>year</c> or <c>month</c> (date fields).</summary>
public sealed record SmartFolderGroupBy(string Field, string? By = null);

/// <summary>
/// What a smart folder shows (TAX-08). All parts are optional and combine with "and":
/// <c>lists</c> (list names), <c>listTemplates</c> (e.g. <c>tasks</c>), <c>contentTypes</c> (names or keys),
/// <c>terms</c> (term ids, with their child terms; <c>termMatch</c> <c>all</c> or <c>any</c>), and an OData
/// <c>filter</c> over fields (with <c>@me</c>, <c>@today</c>, …). <c>groupBy</c> adds virtual sub-folders (TAX-10).
/// </summary>
public sealed record SmartFolderDefinition(
    IReadOnlyList<string>? Lists = null,
    IReadOnlyList<string>? ListTemplates = null,
    IReadOnlyList<string>? ContentTypes = null,
    IReadOnlyList<Guid>? Terms = null,
    string? TermMatch = null,
    string? Filter = null,
    IReadOnlyList<SmartFolderGroupBy>? GroupBy = null,
    bool IncludeFolders = false);

/// <summary>Create/update body. <c>personal</c> folders belong to the caller; others to <c>workspaceId</c> (Manage needed).</summary>
public sealed record SmartFolderRequest(string? Name, string? Description, Guid? WorkspaceId, bool Personal, SmartFolderDefinition? Definition);

public sealed record SmartFolderResponse(
    Guid Id, string Name, string? Description, Guid? WorkspaceId, bool Personal, SmartFolderDefinition Definition,
    DateTimeOffset CreatedAt, Guid? CreatedBy, DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

/// <summary>An item in a smart folder, with where it lives.</summary>
public sealed record SmartFolderEntry(Guid WorkspaceId, string ListName, ItemResponse Item);

public sealed record SmartFolderGroup(string? Value, string Label, int Count);

public sealed record SmartFolderGroupsResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<SmartFolderGroup> Value, string? Field, string? By);

/// <summary>
/// Drop to classify (TAX-09): an existing item (<c>itemId</c>) gets the folder's terms and the values of its
/// <c>eq</c> conditions (and of the sub-folder <c>path</c>); or a new item is created with them (<c>fields</c>).
/// </summary>
public sealed record SmartFolderDropRequest(Guid WorkspaceId, Guid ListId, Guid? ItemId, JsonObject? Fields, IReadOnlyList<string?>? Path);

/// <summary>Smart folders (TAX-08…10): saved, rule-based views over items of many lists.</summary>
internal static class SmartFolders
{
    public const int MaxLists = 100;
    public const int MaxGroupLevels = 3;
    private const int MaxTerms = 20;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/smartFolders").WithTags("Smart folders");
        group.MapGet("", ListAsync).RequireScope(ListScopes.Read).WithName("ListSmartFolders");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Write).WithName("CreateSmartFolder");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetSmartFolder");
        group.MapPatch("/{id:guid}", UpdateAsync).RequireScope(ListScopes.Write).WithName("UpdateSmartFolder");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(ListScopes.Write).WithName("DeleteSmartFolder");
        group.MapGet("/{id:guid}/items", ItemsAsync).RequireScope(ListScopes.Read).WithName("ListSmartFolderItems")
            .WithDescription("Items of the folder across its lists, most recently changed first. path (repeated) selects a virtual sub-folder of groupBy; an empty value is the (empty) group.");
        group.MapGet("/{id:guid}/groups", GroupsAsync).RequireScope(ListScopes.Read).WithName("ListSmartFolderGroups")
            .WithDescription("The virtual sub-folders at path: the values of the next groupBy level with item counts.");
        group.MapPost("/{id:guid}/items", DropAsync).RequireScope(ListScopes.Write).WithName("AddToSmartFolder");
        group.MapDelete("/{id:guid}/items/{itemId:guid}", RemoveAsync).RequireScope(ListScopes.Write).WithName("RemoveFromSmartFolder")
            .WithDescription("Removes the folder's classification from an item (its terms, and field values equal to the folder's conditions).");
    }

    // ---- CRUD -------------------------------------------------------------------------------

    /// <summary>The caller's personal folders and the shared folders of workspaces they can read.</summary>
    private static async Task<Ok<Page<SmartFolderResponse>>> ListAsync(
        Guid? workspaceId, HttpRequest http, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, ListsDbContext database, IWorkspaceAccess workspaces, CancellationToken cancellationToken)
    {
        var readable = (await workspaces.GetMembershipsAsync(caller.TenantId, caller.UserId, cancellationToken)).Select(m => m.WorkspaceId).ToHashSet();
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var ct = cancellationToken;
        var all = await db.SmartFolders.AsNoTracking().Where(f => f.TenantId == tenant && (f.OwnerId == user || f.OwnerId == null)).OrderBy(f => f.Id).ToListAsync(ct);
        var after = page.After ?? Guid.Empty;
        var visible = all
            .Where(f => f.OwnerId == user || (f.WorkspaceId is { } ws && readable.Contains(ws)))
            .Where(f => workspaceId is null || f.WorkspaceId == workspaceId)
            .Where(f => f.Id.CompareTo(after) > 0)
            .Take(page.Top + 1)
            .Select(ToResponse)
            .ToList();
        return TypedResults.Ok(Page.Create(visible, page, http, f => f.Id));
    }

    private static async Task<Results<Ok<SmartFolderResponse>, ProblemHttpResult>> GetAsync(
        Guid id, Caller caller, ListsDbContext db, IWorkspaceAccess workspaces, HttpResponse response, CancellationToken ct)
    {
        if (await VisibleAsync(id, caller, db, workspaces, ct) is not { } folder)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, folder.Version);
        return TypedResults.Ok(ToResponse(folder));
    }

    private static async Task<Results<Created<SmartFolderResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SmartFolderRequest request, Caller caller, ListsDbContext db, IWorkspaceAccess workspaces, ITermStore terms, HttpResponse response, CancellationToken ct)
    {
        if (await ValidateAsync(request, caller.TenantId, terms, ct) is { } invalid)
        {
            return invalid;
        }

        if (request.WorkspaceId is { } ws && await workspaces.GetPermissionAsync(caller.TenantId, caller.UserId, ws, ct) is var level
            && (level == WorkspaceAccessLevel.None || (!request.Personal && level < WorkspaceAccessLevel.Manage)))
        {
            return level == WorkspaceAccessLevel.None ? ApiErrors.NotFound("The workspace was not found.") : Forbidden();
        }

        var folder = new SmartFolder
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            Name = request.Name!.Trim(),
            Description = request.Description,
            WorkspaceId = request.WorkspaceId,
            OwnerId = request.Personal ? caller.UserId : null,
            Definition = Serialize(request.Definition ?? new SmartFolderDefinition()),
        };
        db.SmartFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, folder.Version);
        return TypedResults.Created($"/v1.0/smartFolders/{folder.Id}", ToResponse(folder));
    }

    /// <summary>Changes name, description and definition (not the owner or workspace); needs <c>If-Match</c>.</summary>
    private static async Task<Results<Ok<SmartFolderResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, SmartFolderRequest request, Caller caller, ListsDbContext db, IWorkspaceAccess workspaces, ITermStore terms,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await VisibleAsync(id, caller, db, workspaces, ct, tracking: true) is not { } folder)
        {
            return ApiErrors.NotFound();
        }

        if (!await CanManageAsync(folder, caller, workspaces, ct))
        {
            return Forbidden();
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (version != folder.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (await ValidateAsync(request with { WorkspaceId = folder.WorkspaceId, Personal = folder.OwnerId is not null }, caller.TenantId, terms, ct) is { } invalid)
        {
            return invalid;
        }

        folder.Name = request.Name!.Trim();
        folder.Description = request.Description;
        folder.Definition = Serialize(request.Definition ?? new SmartFolderDefinition());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, folder.Version);
        return TypedResults.Ok(ToResponse(folder));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, Caller caller, ListsDbContext db, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        if (await VisibleAsync(id, caller, db, workspaces, ct, tracking: true) is not { } folder)
        {
            return ApiErrors.NotFound();
        }

        if (!await CanManageAsync(folder, caller, workspaces, ct))
        {
            return Forbidden();
        }

        db.SmartFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---- Contents ---------------------------------------------------------------------------

    private static async Task<Results<Ok<Page<SmartFolderEntry>>, ValidationProblem, ProblemHttpResult>> ItemsAsync(
        Guid id, string[]? path, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces, HttpRequest http, CancellationToken ct)
    {
        if (await VisibleAsync(id, caller, db, workspaces, ct) is not { } folder)
        {
            return ApiErrors.NotFound();
        }

        var definition = Definition(folder);
        if (PathError(definition, path) is { } pathError)
        {
            return ApiErrors.Validation("path", pathError);
        }

        var take = Math.Clamp(top ?? 100, 1, 1000);
        var after = skipToken is null ? null : DecodeCursor(skipToken);
        if (skipToken is not null && after is null)
        {
            return ApiErrors.Validation("$skiptoken", "The cursor is not valid. Omit it to read the first page.");
        }

        var (entries, error) = await folders.ItemsAsync(ListEndpoints.CallerOf(caller), folder, definition, path ?? [], after, take + 1, ct);
        if (error is not null)
        {
            return ApiErrors.Validation("filter", error);
        }

        var value = entries.Take(take).Select(e => new SmartFolderEntry(e.WorkspaceId, e.ListName, ItemResponse.From(e.Item))).ToList();
        string? nextLink = null;
        if (entries.Count > take)
        {
            var last = entries[take - 1].Item;
            var query = http.Query.Where(q => q.Key is not "$skiptoken")
                .SelectMany(q => q.Value.Select(v => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(v ?? "")}"))
                .Append($"$skiptoken={EncodeCursor(last.UpdatedAt, last.Id)}");
            nextLink = $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', query)}";
        }

        return TypedResults.Ok(new Page<SmartFolderEntry>(value, nextLink));
    }

    private static async Task<Results<Ok<SmartFolderGroupsResponse>, ValidationProblem, ProblemHttpResult>> GroupsAsync(
        Guid id, string[]? path, Caller caller, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        if (await VisibleAsync(id, caller, db, workspaces, ct) is not { } folder)
        {
            return ApiErrors.NotFound();
        }

        var definition = Definition(folder);
        path ??= [];
        if (PathError(definition, path) is { } pathError)
        {
            return ApiErrors.Validation("path", pathError);
        }

        var levels = definition.GroupBy ?? [];
        if (path.Length >= levels.Count)
        {
            return TypedResults.Ok(new SmartFolderGroupsResponse([], null, null));
        }

        var level = levels[path.Length];
        var (groups, error) = await folders.GroupsAsync(ListEndpoints.CallerOf(caller), folder, definition, path, level, ct);
        return error is null
            ? TypedResults.Ok(new SmartFolderGroupsResponse(groups, level.Field, level.By))
            : ApiErrors.Validation("filter", error);
    }

    private static async Task<Results<Ok<SmartFolderEntry>, Created<SmartFolderEntry>, ValidationProblem, ProblemHttpResult>> DropAsync(
        Guid id, SmartFolderDropRequest request, Caller caller, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        if (await VisibleAsync(id, caller, db, workspaces, ct) is not { } folder)
        {
            return ApiErrors.NotFound();
        }

        var definition = Definition(folder);
        if (PathError(definition, request.Path?.ToArray()) is { } pathError)
        {
            return ApiErrors.Validation("path", pathError);
        }

        if (request.ItemId is null == request.Fields is null)
        {
            return ApiErrors.Validation("itemId", "Give either itemId (an existing item) or fields (a new item).");
        }

        var (result, created, error) = await folders.ClassifyAsync(ListEndpoints.CallerOf(caller), folder, definition, request, ct);
        return result switch
        {
            null => ApiErrors.Validation("item", error!),
            { Status: ListItemStatus.Ok } => created
                ? TypedResults.Created($"/v1.0/workspaces/{request.WorkspaceId}/lists/{request.ListId}/items/{result.Item!.Id}", await EntryAsync(db, caller.TenantId, result.Item!, ct))
                : TypedResults.Ok(await EntryAsync(db, caller.TenantId, result.Item!, ct)),
            _ => Problem(result),
        };
    }

    private static async Task<Results<Ok<SmartFolderEntry>, ValidationProblem, ProblemHttpResult>> RemoveAsync(
        Guid id, Guid itemId, Guid workspaceId, Guid listId, Caller caller, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces,
        CancellationToken ct)
    {
        if (await VisibleAsync(id, caller, db, workspaces, ct) is not { } folder)
        {
            return ApiErrors.NotFound();
        }

        var (result, error) = await folders.UnclassifyAsync(ListEndpoints.CallerOf(caller), folder, Definition(folder), workspaceId, listId, itemId, ct);
        return result switch
        {
            null => ApiErrors.Validation("item", error!),
            { Status: ListItemStatus.Ok } => TypedResults.Ok(await EntryAsync(db, caller.TenantId, result.Item!, ct)),
            _ => Problem(result),
        };
    }

    // ---- Helpers ----------------------------------------------------------------------------

    internal static SmartFolderDefinition Definition(SmartFolder folder) =>
        JsonSerializer.Deserialize(folder.Definition, ListsJson.Default.SmartFolderDefinition) ?? new SmartFolderDefinition();

    internal static string Serialize(SmartFolderDefinition definition) => JsonSerializer.Serialize(definition, ListsJson.Default.SmartFolderDefinition);

    private static SmartFolderResponse ToResponse(SmartFolder f) =>
        new(f.Id, f.Name, f.Description, f.WorkspaceId, f.OwnerId is not null, Definition(f), f.CreatedAt, f.CreatedBy, f.UpdatedAt) { ETag = ETags.From(f.Version) };

    private static async Task<SmartFolderEntry> EntryAsync(ListsDbContext database, Guid tenantId, ListItemData item, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var list = item.ListId;
        var ct = cancellationToken;
        var name = await db.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.Id == list).Select(l => l.Name).FirstAsync(ct);
        return new SmartFolderEntry(item.WorkspaceId, name, new ItemResponse(
            item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, item.Fields,
            ETags.From(item.Version)));
    }

    private static async Task<SmartFolder?> VisibleAsync(Guid id, Caller caller, ListsDbContext database, IWorkspaceAccess workspaces, CancellationToken cancellationToken, bool tracking = false)
    {
        var db = database;
        var tenant = caller.TenantId;
        var folderId = id;
        var ct = cancellationToken;
        var folder = tracking
            ? await db.SmartFolders.FirstOrDefaultAsync(f => f.TenantId == tenant && f.Id == folderId, ct)
            : await db.SmartFolders.AsNoTracking().FirstOrDefaultAsync(f => f.TenantId == tenant && f.Id == folderId, ct);
        return folder switch
        {
            null => null,
            { OwnerId: { } owner } => owner == caller.UserId ? folder : null,
            { WorkspaceId: { } ws } => await workspaces.GetPermissionAsync(tenant, caller.UserId, ws, ct) > WorkspaceAccessLevel.None ? folder : null,
            _ => null,
        };
    }

    private static async Task<bool> CanManageAsync(SmartFolder folder, Caller caller, IWorkspaceAccess workspaces, CancellationToken ct) =>
        folder.OwnerId is { } owner ? owner == caller.UserId : await workspaces.GetPermissionAsync(caller.TenantId, caller.UserId, folder.WorkspaceId!.Value, ct) >= WorkspaceAccessLevel.Manage;

    private static async Task<ValidationProblem?> ValidateAsync(SmartFolderRequest request, Guid tenantId, ITermStore terms, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            errors["name"] = ["Enter a name of 1 to 200 characters."];
        }

        if (request.Description is { Length: > 2000 })
        {
            errors["description"] = ["The description can have at most 2000 characters."];
        }

        if (!request.Personal && request.WorkspaceId is null)
        {
            errors["workspaceId"] = ["Shared smart folders belong to a workspace; set personal for your own folders."];
        }

        var definition = request.Definition ?? new SmartFolderDefinition();
        if (definition.Terms is { Count: > MaxTerms })
        {
            errors["definition.terms"] = [$"Up to {MaxTerms} terms."];
        }
        else if (definition.Terms is { Count: > 0 } ids && (await terms.GetTermsAsync(tenantId, ids, ct)).Count != ids.Distinct().Count())
        {
            errors["definition.terms"] = ["Unknown term."];
        }

        if (definition.TermMatch is not (null or "all" or "any"))
        {
            errors["definition.termMatch"] = ["Use all or any."];
        }

        if (definition.Filter is { Length: > 2000 })
        {
            errors["definition.filter"] = ["The filter is too long."];
        }

        if (definition.GroupBy is { Count: > MaxGroupLevels } || (definition.GroupBy ?? []).Any(g => string.IsNullOrWhiteSpace(g.Field) || g.By is not (null or "year" or "month")))
        {
            errors["definition.groupBy"] = [$"Up to {MaxGroupLevels} levels of {{ field, by? (year or month) }}."];
        }

        return errors.Count > 0 ? ApiErrors.Validation(errors) : null;
    }

    private static string? PathError(SmartFolderDefinition definition, string?[]? path) =>
        path is { Length: > 0 } && path.Length > (definition.GroupBy?.Count ?? 0) ? "The path is deeper than the folder's groupBy levels." : null;

    private static string EncodeCursor(DateTimeOffset at, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{id:N}"));

    private static (DateTimeOffset At, Guid Id)? DecodeCursor(string token)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token)).Split('.');
            return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks) && Guid.TryParseExact(parts[1], "N", out var id)
                && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You may not change this smart folder.");

    private static ProblemHttpResult Problem(ListItemResult result) => result.Status switch
    {
        ListItemStatus.NotFound => ApiErrors.NotFound(),
        ListItemStatus.Forbidden => ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You may not change this item."),
        _ => ApiErrors.Problem(StatusCodes.Status400BadRequest, "invalidRequest",
            result.Errors is { Count: > 0 } errors ? string.Join(" ", errors.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}"))) : result.Message ?? "The change was rejected."),
    };
}
