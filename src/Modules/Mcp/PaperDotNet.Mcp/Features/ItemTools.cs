using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Mcp.Features;

/// <summary>Base for the built-in tools: name, description, schema and scope as data.</summary>
internal abstract class BuiltInTool(string name, string description, JsonElement schema, string scope, bool readOnly) : IMcpTool
{
    protected const int DefaultPageSize = 50;
    public string Name => name;

    public string Description => description;

    public JsonElement InputSchema => schema;

    public string? RequiredScope => scope;

    public bool IsReadOnly => readOnly;

    public abstract Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken);

    protected static McpToolResult FromResult(ListItemResult result) => result.Status switch
    {
        ListItemStatus.Ok => McpToolResult.FromJson(Item(result.Item!)),
        ListItemStatus.NotFound => McpToolResult.Error("The list or item was not found (or you cannot read it)."),
        ListItemStatus.Forbidden => McpToolResult.Error("You may not change this item."),
        ListItemStatus.VersionMismatch => McpToolResult.Error("The item was changed by someone else; read it again."),
        _ => McpToolResult.Error(result.Errors is { Count: > 0 } errors
            ? string.Join(" ", errors.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}")))
            : result.Message ?? "The change was rejected."),
    };

    protected static JsonObject Item(ListItemData item) => new()
    {
        ["id"] = item.Id,
        ["workspaceId"] = item.WorkspaceId,
        ["listId"] = item.ListId,
        ["contentTypeId"] = item.ContentTypeId,
        ["parentId"] = item.ParentId,
        ["isFolder"] = item.IsFolder,
        ["access"] = item.Access.ToString(),
        ["version"] = item.Version,
        ["createdAt"] = item.CreatedAt,
        ["updatedAt"] = item.UpdatedAt,
        ["fields"] = item.Fields.DeepClone(),
    };

    protected static McpToolResult Page(IReadOnlyList<ListItemData> items, string? nextCursor) =>
        McpToolResult.FromJson(new JsonObject { ["items"] = Array(items.Select(Item)), ["nextCursor"] = nextCursor });

    protected static JsonArray Array(IEnumerable<JsonNode?> nodes) => [.. nodes];

    protected static uint? Version(McpArguments arguments) =>
        arguments.GetInt32("version") is { } version and >= 0 ? (uint)version : null;
}

internal sealed class WorkspacesTool(IWorkspaceAccess workspaces, Caller caller) : BuiltInTool(
    "list_workspaces", "Lists the workspaces you are a member of, with your access level.", McpSchema.ObjectSchema(), "workspace.read", true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var mine = await workspaces.GetMembershipsAsync(caller.TenantId, caller.UserId, cancellationToken);
        var names = await workspaces.GetNamesAsync(caller.TenantId, [.. mine.Select(m => m.WorkspaceId)], cancellationToken);
        return McpToolResult.FromJson(new JsonObject
        {
            ["workspaces"] = Array(mine.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.WorkspaceId,
                ["name"] = names.GetValueOrDefault(m.WorkspaceId),
                ["access"] = m.Level.ToString(),
            })),
        });
    }
}

internal sealed class HomeTool(IListItemStore items) : BuiltInTool(
    "get_home",
    "The signed-in user's personal workspace, with its Documents library and Inbox library. They are created on first use.",
    McpSchema.ObjectSchema(),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var home = await items.EnsureHomeAsync(cancellationToken);
        return McpToolResult.FromJson(new JsonObject
        {
            ["workspaceId"] = home.WorkspaceId,
            ["documentsListId"] = home.DocumentsListId,
            ["inboxListId"] = home.InboxListId,
        });
    }
}

internal sealed class ListsTool(IListItemStore items) : BuiltInTool(
    "list_lists",
    "Lists the lists and document libraries you can read (optionally in one workspace). templateKey tells the kind: tasks, events, documents, notes. isLibrary means items can have files. Call describe_list for columns.",
    McpSchema.ObjectSchema(("workspaceId", "string", "Only lists of this workspace (id).", false)),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var lists = await items.GetListsAsync(arguments.GetGuid("workspaceId"), null, cancellationToken);
        return McpToolResult.FromJson(new JsonObject
        {
            ["lists"] = Array(lists.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id,
                ["workspaceId"] = l.WorkspaceId,
                ["name"] = l.Name,
                ["templateKey"] = l.TemplateKey,
                ["isLibrary"] = l.IsLibrary,
                ["contentTypes"] = Array(l.ContentTypes.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["key"] = c.Key })),
            })),
        });
    }
}

internal sealed class DescribeListTool(IListItemStore items) : BuiltInTool(
    "describe_list",
    "Columns and content types of one list. title is required on every item. The first content type is the default for create_item. Call this before creating or editing items so field names, types and choices are known.",
    McpSchema.ObjectSchema(("workspaceId", "string", "Workspace id.", true), ("listId", "string", "List id.", true)),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var list = await items.DescribeListAsync(arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), cancellationToken);
        if (list is null)
        {
            return McpToolResult.Error("The list was not found (or you cannot read it).");
        }

        return McpToolResult.FromJson(new JsonObject
        {
            ["id"] = list.Id,
            ["workspaceId"] = list.WorkspaceId,
            ["name"] = list.Name,
            ["description"] = list.Description,
            ["templateKey"] = list.TemplateKey,
            ["isLibrary"] = list.IsLibrary,
            ["allowFolders"] = list.AllowFolders,
            ["access"] = list.Access.ToString(),
            ["contentTypes"] = Array(list.ContentTypes.Select((contentType, index) => (JsonNode)new JsonObject
            {
                ["id"] = contentType.Id,
                ["name"] = contentType.Name,
                ["key"] = contentType.Key,
                ["description"] = contentType.Description,
                ["isDefault"] = index == 0,
                ["fields"] = Array(contentType.Fields.Select(Field).Prepend(Field(new ListFieldInfo("title", "Title", "text", true, false, "Required on every item.")))),
            })),
        });
    }

    private static JsonNode Field(ListFieldInfo field) => new JsonObject
    {
        ["name"] = field.Name,
        ["displayName"] = field.DisplayName,
        ["type"] = field.Type,
        ["required"] = field.Required,
        ["allowMultiple"] = field.AllowMultiple,
        ["description"] = field.Description,
        ["maxLength"] = field.MaxLength,
        ["minimum"] = field.Minimum,
        ["maximum"] = field.Maximum,
        ["choices"] = field.Choices is { } choices ? Array(choices.Select(c => (JsonNode)c)) : null,
    };
}

internal sealed class QueryItemsTool(IListItemStore items) : BuiltInTool(
    "query_items",
    "Reads items of a list in every folder (folders themselves are omitted; use list_children for those). " +
    "filter and orderBy are OData. Examples: fields/status eq 'notStarted', fields/dueDate le @next7Days. " +
    "Call describe_list for the real field names and choices. Date aliases: @me, @today, @next7Days, @last30Days. " +
    $"Returns at most top items (1-{ListItemQuery.MaxTop}, default {DefaultPageSize}). " +
    "When nextCursor is present, call again with cursor set to it and the same filter and orderBy.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("filter", "string", "OData $filter, e.g. fields/title eq 'x'.", false),
        ("orderBy", "string", "OData $orderby, e.g. fields/dueDate asc.", false),
        ("top", "integer", $"Page size (1-{ListItemQuery.MaxTop}, default {DefaultPageSize}).", false),
        ("cursor", "string", "nextCursor from the previous page. Omit to start at the first page.", false)),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var (page, error) = await items.QueryPageAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), PageQuery(arguments), cancellationToken);
        return error is not null ? McpToolResult.Error(error) : Page(page!.Items, page.NextCursor);
    }

    internal static ListItemQuery PageQuery(McpArguments arguments) => new(
        arguments.GetString("filter"), arguments.GetString("orderBy"), Math.Clamp(arguments.GetInt32("top") ?? DefaultPageSize, 1, ListItemQuery.MaxTop), arguments.GetString("cursor"));
}

internal sealed class GetItemTool(IListItemStore items) : BuiltInTool(
    "get_item",
    "Reads one item with its fields, content type, version and your access level.",
    McpSchema.ObjectSchema(("workspaceId", "string", "Workspace id.", true), ("listId", "string", "List id.", true), ("itemId", "string", "Item id.", true)),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredGuid("itemId"), cancellationToken);
        return item is null ? McpToolResult.Error("The item was not found (or you cannot read it).") : McpToolResult.FromJson(Item(item));
    }
}

internal sealed class CreateItemTool(IListItemStore items) : BuiltInTool(
    "create_item",
    "Creates an item (a task, an event, a list entry) with the given field values; fields must include title.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("fields", "object", "Field values by field name. Call describe_list for the names, types and choices. title is required.", true),
        ("contentTypeId", "string", "Content type id from describe_list (default: the list's first).", false),
        ("parentId", "string", "Folder id to create the item in. Omit for the list root.", false)),
    "list.write",
    false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken) =>
        FromResult(await items.CreateAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredObject("fields"), arguments.GetGuid("contentTypeId"),
            arguments.GetGuid("parentId"), cancellationToken));
}

internal sealed class UpdateItemTool(IListItemStore items) : BuiltInTool(
    "update_item",
    "Changes fields of an item (only the given fields; null removes a value). Pass version from get_item to avoid overwriting someone else's change.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("itemId", "string", "Item id.", true),
        ("fields", "object", "Field values to set.", true),
        ("version", "integer", "The version you read (optional).", false)),
    "list.write",
    false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken) =>
        FromResult(await items.UpdateAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredGuid("itemId"), arguments.GetRequiredObject("fields"),
            Version(arguments), cancellationToken));
}
