using System.Buffers.Text;
using System.Globalization;
using System.Linq.Expressions;
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
using PaperDotNet.Persistence;
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
    DateTimeOffset CreatedAt, Guid? CreatedBy, DateTimeOffset UpdatedAt);

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
        var entries = await folders.ItemsAsync(folder, definition, path ?? [], after, page.Top + 1, ct);
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
        return TypedResults.Ok(new SmartFolderGroupsResponse(await folders.GroupsAsync(folder, definition, path, level, ct), level.Field, level.By));
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
        new(f.Id, f.Name, f.Description, f.WorkspaceId, f.OwnerId is not null, Definition(f), f.CreatedAt, f.CreatedBy, f.UpdatedAt);

    private static async Task<SmartFolderEntry> EntryAsync(ListsDbContext db, ListItemData item, CancellationToken ct) => new(
        item.WorkspaceId,
        await db.Lists.AsNoTracking().Where(l => l.Id == item.ListId).Select(l => l.Name).FirstAsync(ct),
        new ItemResponse(item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, item.Fields));

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

/// <summary>Runs smart folder definitions over the caller's lists.</summary>
internal sealed class SmartFolderQuery(
    ListSchemaLoader loader, ItemQueryRunner runner, IWorkspaceAccess workspaces, IListItemStore items,
    ITermStore terms, IUserDirectory users, FieldTypeRegistry fieldTypes)
{
    internal sealed record Candidate(Guid WorkspaceId, ListSchema Schema);

    internal sealed record Found(Guid WorkspaceId, string ListName, ListItem Item);

    /// <summary>The lists the folder covers that the caller can read (at most <see cref="SmartFolders.MaxLists"/>).</summary>
    public async Task<List<Candidate>> ListsAsync(SmartFolder folder, SmartFolderDefinition definition, CancellationToken ct)
    {
        var memberships = await workspaces.GetMyWorkspacesAsync(ct);
        var workspaceIds = folder.WorkspaceId is { } ws
            ? memberships.Where(m => m.WorkspaceId == ws).ToList()
            : memberships.ToList();
        var candidates = new List<Candidate>();
        foreach (var membership in workspaceIds)
        {
            foreach (var list in await loader.VisibleListsAsync(membership.WorkspaceId, membership.Level, ct))
            {
                if (definition.Lists is { Count: > 0 } names && !names.Contains(list.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (definition.ListTemplates is { Count: > 0 } templates && !templates.Contains(list.TemplateKey ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (await loader.LoadAsync(membership.WorkspaceId, list.Id, ct) is not { } schema)
                {
                    continue;
                }

                if (definition.ContentTypes is { Count: > 0 } && ContentTypeIds(schema, definition).Count == 0)
                {
                    continue;
                }

                candidates.Add(new Candidate(membership.WorkspaceId, schema));
                if (candidates.Count >= SmartFolders.MaxLists)
                {
                    return candidates;
                }
            }
        }

        return candidates;
    }

    public async Task<List<Found>> ItemsAsync(SmartFolder folder, SmartFolderDefinition definition, string?[] path, (DateTimeOffset At, Guid Id)? after, int take, CancellationToken ct)
    {
        var termInfo = await TermsAsync(definition, ct);
        var found = new List<Found>();
        foreach (var candidate in await ListsAsync(folder, definition, ct))
        {
            if (Filter(candidate.Schema, definition, termInfo) is not { } filter)
            {
                continue;
            }

            var (listItems, _) = await runner.RecentAsync(candidate.Schema, filter.Length == 0 ? null : filter, Extra(definition, path), after, take, ct);
            found.AddRange((listItems ?? []).Select(i => new Found(candidate.WorkspaceId, candidate.Schema.List.Name, i)));
        }

        return found.OrderByDescending(f => f.Item.UpdatedAt).ThenByDescending(f => f.Item.Id).Take(take).ToList();
    }

    public async Task<List<SmartFolderGroup>> GroupsAsync(SmartFolder folder, SmartFolderDefinition definition, string?[] path, SmartFolderGroupBy level, CancellationToken ct)
    {
        var termInfo = await TermsAsync(definition, ct);
        var counts = new Dictionary<string, int>();
        var empty = 0;
        FieldDefinition? sample = null;
        foreach (var candidate in await ListsAsync(folder, definition, ct))
        {
            if (Filter(candidate.Schema, definition, termInfo) is not { } filter)
            {
                continue;
            }

            if (candidate.Schema.Fields.TryGetValue(level.Field, out var field) && !SmartFolders.IsGroupable(field, fieldTypes))
            {
                continue;
            }

            sample ??= field;
            var (groups, none, _) = await runner.GroupAsync(candidate.Schema, filter.Length == 0 ? null : filter, Extra(definition, path), Key(level), ct);
            foreach (var (value, count) in groups ?? [])
            {
                counts[value] = counts.GetValueOrDefault(value) + count;
            }

            empty += none;
        }

        var labels = await LabelsAsync(sample, counts.Keys.ToList(), ct);
        var result = counts
            .Select(c => new SmartFolderGroup(c.Key, labels.GetValueOrDefault(c.Key, c.Key), c.Value))
            .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (empty > 0)
        {
            result.Add(new SmartFolderGroup(null, "(empty)", empty));
        }

        return result;
    }

    /// <summary>Applies the folder's classification to an existing or new item of one of its lists.</summary>
    public async Task<(ListItemResult? Result, bool Created, string? Error)> ClassifyAsync(
        SmartFolder folder, SmartFolderDefinition definition, SmartFolderDropRequest request, CancellationToken ct)
    {
        var candidate = (await ListsAsync(folder, definition, ct)).FirstOrDefault(c => c.Schema.List.Id == request.ListId && c.WorkspaceId == request.WorkspaceId);
        if (candidate is null)
        {
            return (null, false, "The list is not part of this smart folder.");
        }

        ListItemData? current = null;
        if (request.ItemId is { } itemId)
        {
            current = await items.GetAsync(request.WorkspaceId, request.ListId, itemId, ct);
            if (current is null)
            {
                return (new ListItemResult(ListItemStatus.NotFound), false, null);
            }
        }

        var (values, error) = await ClassificationAsync(candidate.Schema, definition, request.Path ?? [], current?.Fields ?? request.Fields!, add: true, ct);
        if (values is null)
        {
            return (null, false, error);
        }

        if (current is not null)
        {
            return (await items.UpdateAsync(request.WorkspaceId, request.ListId, current.Id, values, null, ct), false, null);
        }

        var fields = request.Fields!.DeepClone().AsObject();
        foreach (var (name, value) in values)
        {
            fields[name] = value?.DeepClone();
        }

        var contentTypeId = definition.ContentTypes is { Count: > 0 } ? ContentTypeIds(candidate.Schema, definition).FirstOrDefault() : (Guid?)null;
        return (await items.CreateAsync(request.WorkspaceId, request.ListId, fields, contentTypeId, ct), true, null);
    }

    public async Task<(ListItemResult? Result, string? Error)> UnclassifyAsync(
        SmartFolder folder, SmartFolderDefinition definition, Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var candidate = (await ListsAsync(folder, definition, ct)).FirstOrDefault(c => c.Schema.List.Id == listId && c.WorkspaceId == workspaceId);
        if (candidate is null)
        {
            return (null, "The list is not part of this smart folder.");
        }

        var current = await items.GetAsync(workspaceId, listId, itemId, ct);
        if (current is null)
        {
            return (new ListItemResult(ListItemStatus.NotFound), null);
        }

        var (values, error) = await ClassificationAsync(candidate.Schema, definition, [], current.Fields, add: false, ct);
        return values is null ? (null, error) : (await items.UpdateAsync(workspaceId, listId, itemId, values, null, ct), null);
    }

    /// <summary>
    /// Field changes that put an item into the folder (<paramref name="add"/>) or take it out: its terms go into
    /// the matching term fields, <c>eq</c> conditions and sub-folder values become field values.
    /// </summary>
    private async Task<(JsonObject? Values, string? Error)> ClassificationAsync(
        ListSchema schema, SmartFolderDefinition definition, IReadOnlyList<string?> path, JsonObject current, bool add, CancellationToken ct)
    {
        var values = new JsonObject();
        foreach (var term in await TermsAsync(definition, ct))
        {
            var field = schema.Fields.Values.FirstOrDefault(f =>
                (f.Type == ManagedMetadataFieldType.TypeName && f.TermSetId == term.TermSetId) || (f.Type == KeywordsFieldType.TypeName && term.IsKeyword));
            if (field is null)
            {
                return (null, $"The list '{schema.List.Name}' has no field for the term '{term.Name}'.");
            }

            var id = term.Id.ToString();
            if (field.AllowMultiple)
            {
                var existing = (values[field.Name] ?? current[field.Name]) is JsonArray array
                    ? array.Select(v => v?.GetValue<string>()).Where(v => v is not null).Select(v => v!).ToList()
                    : [];
                existing = add ? [.. existing.Where(v => v != id), id] : existing.Where(v => v != id).ToList();
                values[field.Name] = new JsonArray([.. existing.Select(v => (JsonNode?)JsonValue.Create(v))]);
            }
            else if (add)
            {
                values[field.Name] = id;
            }
            else if (current[field.Name]?.GetValue<string>() == id)
            {
                values[field.Name] = null;
            }
        }

        var (equalities, error) = runner.Equalities(schema, definition.Filter);
        if (equalities is null)
        {
            return (null, error);
        }

        foreach (var (name, value) in equalities)
        {
            if (add)
            {
                values[name] = value?.DeepClone();
            }
            else if (value is not null && JsonNode.DeepEquals(current[name], value) && name != "title")
            {
                values[name] = null;
            }
        }

        for (var level = 0; add && level < path.Count && level < (definition.GroupBy?.Count ?? 0); level++)
        {
            var group = definition.GroupBy![level];
            if (group.By is null && schema.Fields.ContainsKey(group.Field))
            {
                values[group.Field] = path[level] is { Length: > 0 } value ? value : null;
            }
        }

        return (values, null);
    }

    /// <summary>The OData filter of the folder for one list; null when the list cannot match (e.g. no term field).</summary>
    private static string? Filter(ListSchema schema, SmartFolderDefinition definition, IReadOnlyList<TermInfo> termInfo)
    {
        var parts = new List<string>();
        if (!definition.IncludeFolders)
        {
            parts.Add("isFolder eq false");
        }

        if (definition.ContentTypes is { Count: > 0 })
        {
            parts.Add("(" + string.Join(" or ", ContentTypeIds(schema, definition).Select(id => $"contentTypeId eq {id}")) + ")");
        }

        var termConditions = new List<string>();
        foreach (var term in termInfo)
        {
            var fields = schema.Fields.Values
                .Where(f => (f.Type == ManagedMetadataFieldType.TypeName && f.TermSetId == term.TermSetId) || (f.Type == KeywordsFieldType.TypeName && term.IsKeyword))
                .Select(f => f.AllowMultiple ? $"fields/{f.Name}/any(t: t eq {term.Id})" : $"fields/{f.Name} eq {term.Id}")
                .ToList();
            if (fields.Count > 0)
            {
                termConditions.Add("(" + string.Join(" or ", fields) + ")");
            }
            else if (definition.TermMatch != "any")
            {
                return null;
            }
        }

        if (termInfo.Count > 0)
        {
            if (termConditions.Count == 0)
            {
                return null;
            }

            parts.Add("(" + string.Join(definition.TermMatch == "any" ? " or " : " and ", termConditions) + ")");
        }

        if (!string.IsNullOrWhiteSpace(definition.Filter))
        {
            parts.Add("(" + definition.Filter + ")");
        }

        return string.Join(" and ", parts);
    }

    /// <summary>Conditions of the sub-folder <paramref name="path"/> (values of the groupBy levels).</summary>
    private static Expression<Func<ListItem, bool>>? Extra(SmartFolderDefinition definition, string?[] path)
    {
        Expression<Func<ListItem, bool>>? result = null;
        for (var level = 0; level < path.Length && level < (definition.GroupBy?.Count ?? 0); level++)
        {
            var group = definition.GroupBy![level];
            var key = Key(group);
            var parameter = key.Parameters[0];

            // "(empty)": the field is not set (the JSON functions are not null-aware in comparisons).
            Expression body = path[level] is { Length: > 0 } value
                ? Expression.Equal(key.Body, Expression.Constant(value, typeof(string)))
                : Expression.Not(Expression.Call(
                    typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.HasProperty))!,
                    Expression.Property(parameter, nameof(ListItem.Fields)), Expression.Constant(group.Field)));
            var condition = Expression.Lambda<Func<ListItem, bool>>(body, parameter);
            result = result is null ? condition : And(result, condition);
        }

        return result;
    }

    private static Expression<Func<ListItem, bool>> And(Expression<Func<ListItem, bool>> left, Expression<Func<ListItem, bool>> right)
    {
        var body = new ReplaceParameter(right.Parameters[0], left.Parameters[0]).Visit(right.Body);
        return Expression.Lambda<Func<ListItem, bool>>(Expression.AndAlso(left.Body, body), left.Parameters[0]);
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>The grouping key: the field's stored text, or its year/month prefix for dates.</summary>
    private static Expression<Func<ListItem, string?>> Key(SmartFolderGroupBy level)
    {
        // Built as a tree so the field name is a constant (JSON paths cannot be query parameters).
        var item = Expression.Parameter(typeof(ListItem), "i");
        Expression text = Expression.Call(
            typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Text))!, Expression.Property(item, nameof(ListItem.Fields)), Expression.Constant(level.Field));
        if (level.By is "year" or "month")
        {
            text = Expression.Call(text, typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!,
                Expression.Constant(0), Expression.Constant(level.By == "year" ? 4 : 7));
        }

        return Expression.Lambda<Func<ListItem, string?>>(text, item);
    }

    private static List<Guid> ContentTypeIds(ListSchema schema, SmartFolderDefinition definition) =>
        schema.ContentTypes
            .Where(c => definition.ContentTypes!.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(n, c.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Id)
            .ToList();

    private async Task<IReadOnlyList<TermInfo>> TermsAsync(SmartFolderDefinition definition, CancellationToken ct) =>
        definition.Terms is { Count: > 0 } ids ? await terms.GetTermsAsync(ids, ct) : [];

    /// <summary>Display names for group values: term and user names for managed metadata and person fields.</summary>
    private async Task<Dictionary<string, string>> LabelsAsync(FieldDefinition? field, List<string> values, CancellationToken ct)
    {
        var ids = values.Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null).Where(id => id is not null).Select(id => id!.Value).ToList();
        if (field is null || ids.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, string> names = field.Type switch
        {
            ManagedMetadataFieldType.TypeName => (await terms.GetTermsAsync(ids, ct)).ToDictionary(t => t.Id, t => t.Name),
            "person" => await users.GetUserNamesAsync(ids, ct),
            _ => new Dictionary<Guid, string>(),
        };
        return values.Where(v => Guid.TryParse(v, out var id) && names.ContainsKey(id)).ToDictionary(v => v, v => names[Guid.Parse(v)]);
    }
}
