using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>One level of metadata navigation: a field, optionally by <c>year</c> or <c>month</c> (date fields).</summary>
public sealed record SmartFolderGroupBy(string Field, string? By = null);

/// <summary>
/// What a smart folder shows (TAX-08). All parts are optional and combine with "and":
/// <c>lists</c> (list names), <c>listTemplates</c> (e.g. <c>tasks</c>, <c>events</c>), <c>contentTypes</c> (names or keys),
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
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Field kinds that can be grouped by (single values stored as text).</summary>
    private static readonly HashSet<FieldValueKind> GroupableKinds = [FieldValueKind.Text, FieldValueKind.Date, FieldValueKind.DateTime, FieldValueKind.Identifier];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("smartFolders", "Smart folders");
        group.MapGet("", ListAsync).RequireScope(ListScopes.Read).WithName("ListSmartFolders");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Write).WithName("CreateSmartFolder");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetSmartFolder");
        group.MapPatch("/{id:guid}", UpdateAsync).RequireScope(ListScopes.Write).WithName("UpdateSmartFolder");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(ListScopes.Write).WithName("DeleteSmartFolder");
        group.MapGet("/{id:guid}/items", ItemsAsync).RequireScope(ListScopes.Read).WithName("ListSmartFolderItems");
        group.MapGet("/{id:guid}/groups", GroupsAsync).RequireScope(ListScopes.Read).WithName("ListSmartFolderGroups");
        group.MapPost("/{id:guid}/items", DropAsync).RequireScope(ListScopes.Write).WithName("AddToSmartFolder");
        group.MapDelete("/{id:guid}/items/{itemId:guid}", RemoveAsync).RequireScope(ListScopes.Write).WithName("RemoveFromSmartFolder");
    }

    // ---- CRUD -------------------------------------------------------------------------------

    /// <summary>The caller's personal folders and the shared folders of workspaces they can read.</summary>
    private static async Task<Ok<Page<SmartFolderResponse>>> ListAsync(
        Guid? workspaceId, ListsDbContext db, IWorkspaceAccess workspaces, ICurrentUser user, HttpRequest http, CancellationToken ct)
    {
        var readable = (await workspaces.GetMyWorkspacesAsync(ct)).Select(m => m.WorkspaceId).ToList();
        var page = PageRequest.From(http);
        var query = db.SmartFolders.AsNoTracking()
            .Where(f => f.OwnerId == user.UserId || (f.OwnerId == null && f.WorkspaceId != null && readable.Contains(f.WorkspaceId.Value)));
        if (workspaceId is { } ws)
        {
            query = query.Where(f => f.WorkspaceId == ws);
        }

        if (page.After is { } after)
        {
            query = query.Where(f => f.Id.CompareTo(after) > 0);
        }

        var folders = await query.OrderBy(f => f.Id).Take(page.Top + 1).ToListAsync(ct);
        return TypedResults.Ok(Page.Create(folders.Select(ToResponse).ToList(), page, http, f => f.Id));
    }

    private static async Task<Results<Ok<SmartFolderResponse>, ProblemHttpResult>> GetAsync(
        Guid id, ListsDbContext db, IWorkspaceAccess workspaces, ICurrentUser user, HttpResponse response, CancellationToken ct)
    {
        var folder = await VisibleAsync(id, db, workspaces, user, ct);
        if (folder is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, folder.Version);
        return TypedResults.Ok(ToResponse(folder));
    }

    private static async Task<Results<Created<SmartFolderResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SmartFolderRequest request, ListsDbContext db, IWorkspaceAccess workspaces, ITermStore terms, ICurrentUser user, HttpResponse response, CancellationToken ct)
    {
        if (await ValidateAsync(request, terms, ct) is { } invalid)
        {
            return invalid;
        }

        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Smart folders belong to users or workspaces.");
        }

        if (request.WorkspaceId is { } ws && await workspaces.GetPermissionAsync(ws, ct) is var level
            && (level == WorkspaceAccessLevel.None || (!request.Personal && level < WorkspaceAccessLevel.Manage)))
        {
            return level == WorkspaceAccessLevel.None ? ApiErrors.NotFound("The workspace was not found.") : Forbidden();
        }

        var folder = new SmartFolder
        {
            Id = Ids.New(),
            Name = request.Name!.Trim(),
            Description = request.Description,
            WorkspaceId = request.WorkspaceId,
            OwnerId = request.Personal ? userId : null,
            Definition = JsonSerializer.Serialize(request.Definition, Json),
        };
        db.SmartFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, folder.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/smartFolders/{folder.Id}", ToResponse(folder));
    }

    /// <summary>Changes name, description and definition (not the owner or workspace); needs <c>If-Match</c>.</summary>
    private static async Task<Results<Ok<SmartFolderResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, SmartFolderRequest request, ListsDbContext db, IWorkspaceAccess workspaces, ITermStore terms, ICurrentUser user,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        var folder = await VisibleAsync(id, db, workspaces, user, ct, tracking: true);
        if (folder is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await CanManageAsync(folder, workspaces, user, ct))
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

        if (await ValidateAsync(request, terms, ct) is { } invalid)
        {
            return invalid;
        }

        folder.Name = request.Name!.Trim();
        folder.Description = request.Description;
        folder.Definition = JsonSerializer.Serialize(request.Definition, Json);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, folder.Version);
        return TypedResults.Ok(ToResponse(folder));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, ListsDbContext db, IWorkspaceAccess workspaces, ICurrentUser user, CancellationToken ct)
    {
        var folder = await VisibleAsync(id, db, workspaces, user, ct, tracking: true);
        if (folder is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await CanManageAsync(folder, workspaces, user, ct))
        {
            return Forbidden();
        }

        db.SmartFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---- Contents ---------------------------------------------------------------------------

    /// <summary>
    /// Items of the folder across its lists, most recently changed first (<c>$top</c>, <c>$skiptoken</c>).
    /// <c>path</c> (repeated) selects a virtual sub-folder of <c>groupBy</c>; an empty value is the "(empty)" group.
    /// </summary>
    private static async Task<Results<Ok<Page<SmartFolderEntry>>, ValidationProblem, ProblemHttpResult>> ItemsAsync(
        Guid id, string[]? path, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces, ICurrentUser user, HttpRequest http, CancellationToken ct)
    {
        var folder = await VisibleAsync(id, db, workspaces, user, ct);
        if (folder is null)
        {
            return ApiErrors.NotFound();
        }

        var definition = Definition(folder);
        if (PathError(definition, path) is { } pathError)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["path"] = [pathError] });
        }

        var page = PageRequest.From(http);
        var after = http.Query.TryGetValue("$skiptoken", out var token) ? DecodeCursor(token.ToString()) : null;
        var (entries, error) = await folders.ItemsAsync(folder, definition, path ?? [], after, page.Top + 1, ct);
        if (error is not null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["filter"] = [error] });
        }

        var value = entries.Take(page.Top).Select(e => new SmartFolderEntry(e.WorkspaceId, e.ListName, ItemResponse.From(e.Item))).ToList();
        string? nextLink = null;
        if (entries.Count > page.Top)
        {
            var last = entries[page.Top - 1].Item;
            var query = http.Query.Where(q => q.Key is not "$skiptoken")
                .SelectMany(q => q.Value.Select(v => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(v ?? "")}"))
                .Append($"$skiptoken={EncodeCursor(last.UpdatedAt, last.Id)}");
            nextLink = $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', query)}";
        }

        return TypedResults.Ok(new Page<SmartFolderEntry>(value, nextLink));
    }

    /// <summary>The virtual sub-folders at <c>path</c>: the values of the next <c>groupBy</c> level with item counts.</summary>
    private static async Task<Results<Ok<SmartFolderGroupsResponse>, ValidationProblem, ProblemHttpResult>> GroupsAsync(
        Guid id, string[]? path, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces, ICurrentUser user, CancellationToken ct)
    {
        var folder = await VisibleAsync(id, db, workspaces, user, ct);
        if (folder is null)
        {
            return ApiErrors.NotFound();
        }

        var definition = Definition(folder);
        path ??= [];
        if (PathError(definition, path) is { } pathError)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["path"] = [pathError] });
        }

        var levels = definition.GroupBy ?? [];
        if (path.Length >= levels.Count)
        {
            return TypedResults.Ok(new SmartFolderGroupsResponse([], null, null));
        }

        var level = levels[path.Length];
        var (groups, error) = await folders.GroupsAsync(folder, definition, path, level, ct);
        return error is null
            ? TypedResults.Ok(new SmartFolderGroupsResponse(groups, level.Field, level.By))
            : ApiErrors.Validation(new Dictionary<string, string[]> { ["filter"] = [error] });
    }

    private static async Task<Results<Ok<SmartFolderEntry>, Created<SmartFolderEntry>, ValidationProblem, ProblemHttpResult>> DropAsync(
        Guid id, SmartFolderDropRequest request, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces, ICurrentUser user, CancellationToken ct)
    {
        var folder = await VisibleAsync(id, db, workspaces, user, ct);
        if (folder is null)
        {
            return ApiErrors.NotFound();
        }

        var definition = Definition(folder);
        if (PathError(definition, request.Path?.ToArray()) is { } pathError)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["path"] = [pathError] });
        }

        if (request.ItemId is null == request.Fields is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["itemId"] = ["Give either itemId (an existing item) or fields (a new item)."] });
        }

        var (result, created, error) = await folders.ClassifyAsync(folder, definition, request, ct);
        return result switch
        {
            null => ApiErrors.Validation(new Dictionary<string, string[]> { ["item"] = [error!] }),
            { Status: ListItemStatus.Ok } => created
                ? TypedResults.Created($"{ApiRoutes.V1}/workspaces/{request.WorkspaceId}/lists/{request.ListId}/items/{result.Item!.Id}", await EntryAsync(db, result.Item!, ct))
                : TypedResults.Ok(await EntryAsync(db, result.Item!, ct)),
            _ => Problem(result),
        };
    }

    /// <summary>Removes the folder's classification from an item (its terms, and field values equal to the folder's).</summary>
    private static async Task<Results<Ok<SmartFolderEntry>, ValidationProblem, ProblemHttpResult>> RemoveAsync(
        Guid id, Guid itemId, Guid workspaceId, Guid listId, ListsDbContext db, SmartFolderQuery folders, IWorkspaceAccess workspaces, ICurrentUser user,
        CancellationToken ct)
    {
        var folder = await VisibleAsync(id, db, workspaces, user, ct);
        if (folder is null)
        {
            return ApiErrors.NotFound();
        }

        var (result, error) = await folders.UnclassifyAsync(folder, Definition(folder), workspaceId, listId, itemId, ct);
        return result switch
        {
            null => ApiErrors.Validation(new Dictionary<string, string[]> { ["item"] = [error!] }),
            { Status: ListItemStatus.Ok } => TypedResults.Ok(await EntryAsync(db, result.Item!, ct)),
            _ => Problem(result),
        };
    }

    // ---- Helpers ----------------------------------------------------------------------------

    internal static SmartFolderDefinition Definition(SmartFolder folder) => ParseDefinition(folder.Definition);

    internal static SmartFolderDefinition ParseDefinition(string json) =>
        JsonSerializer.Deserialize<SmartFolderDefinition>(json, Json) ?? new SmartFolderDefinition();

    internal static string Serialize(SmartFolderDefinition definition) => JsonSerializer.Serialize(definition, Json);

    private static SmartFolderResponse ToResponse(SmartFolder f) =>
        new(f.Id, f.Name, f.Description, f.WorkspaceId, f.OwnerId is not null, Definition(f), f.CreatedAt, f.CreatedBy, f.UpdatedAt) { ETag = ETags.From(f.Version) };

    private static async Task<SmartFolderEntry> EntryAsync(ListsDbContext db, ListItemData item, CancellationToken ct) => new(
        item.WorkspaceId,
        await db.Lists.AsNoTracking().Where(l => l.Id == item.ListId).Select(l => l.Name).FirstAsync(ct),
        new ItemResponse(item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, item.Fields)
        {
            ETag = ETags.From(item.Version),
        });

    private static async Task<SmartFolder?> VisibleAsync(Guid id, ListsDbContext db, IWorkspaceAccess workspaces, ICurrentUser user, CancellationToken ct, bool tracking = false)
    {
        var folder = await (tracking ? db.SmartFolders : db.SmartFolders.AsNoTracking()).FirstOrDefaultAsync(f => f.Id == id, ct);
        return folder switch
        {
            null => null,
            { OwnerId: { } owner } => owner == user.UserId ? folder : null,
            { WorkspaceId: { } ws } => await workspaces.GetPermissionAsync(ws, ct) > WorkspaceAccessLevel.None ? folder : null,
            _ => null,
        };
    }

    private static async Task<bool> CanManageAsync(SmartFolder folder, IWorkspaceAccess workspaces, ICurrentUser user, CancellationToken ct) =>
        folder.OwnerId is { } owner ? owner == user.UserId : await workspaces.GetPermissionAsync(folder.WorkspaceId!.Value, ct) >= WorkspaceAccessLevel.Manage;

    private static async Task<ValidationProblem?> ValidateAsync(SmartFolderRequest request, ITermStore terms, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            errors["name"] = ["Enter a name of 1 to 200 characters."];
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
        else if (definition.Terms is { Count: > 0 } ids)
        {
            var found = await terms.GetTermsAsync(ids, ct);
            if (found.Count != ids.Distinct().Count())
            {
                errors["definition.terms"] = ["Unknown term."];
            }
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

    internal static bool IsGroupable(FieldDefinition field, FieldTypeRegistry types) =>
        !field.AllowMultiple && types.Find(field.Type) is { } type && GroupableKinds.Contains(type.ValueKind);
}
