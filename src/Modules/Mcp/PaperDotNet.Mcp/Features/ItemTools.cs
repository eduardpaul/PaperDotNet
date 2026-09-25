using System.Text.Json;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Mcp.Features;

/// <summary>Base for the built-in tools: name, description, schema and scope as data.</summary>
internal abstract class BuiltInTool(string name, string description, JsonElement schema, string scope, bool readOnly) : IMcpTool
{
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

    protected static object Item(ListItemData item) => new
    {
        item.Id,
        item.WorkspaceId,
        item.ListId,
        item.ParentId,
        item.IsFolder,
        item.Version,
        item.UpdatedAt,
        item.Fields,
    };
}

internal sealed class WorkspacesTool(IWorkspaceAccess workspaces) : BuiltInTool(
    "list_workspaces", "Lists the workspaces you are a member of, with your access level.", McpSchema.ObjectSchema(), "workspace.read", true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var mine = await workspaces.GetMyWorkspacesAsync(cancellationToken);
        var names = await workspaces.GetNamesAsync(mine.Select(m => m.WorkspaceId).ToList(), cancellationToken);
        return McpToolResult.FromJson(new
        {
            workspaces = mine.Select(m => new { id = m.WorkspaceId, name = names.GetValueOrDefault(m.WorkspaceId), access = m.Level.ToString() }),
        });
    }
}

internal sealed class ListsTool(IListItemStore items) : BuiltInTool(
    "list_lists",
    "Lists the lists and document libraries you can read (optionally in one workspace). templateKey tells the kind: tasks, events, documents, …",
    McpSchema.ObjectSchema(("workspaceId", "string", "Only lists of this workspace (id).", false)),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var lists = await items.GetListsAsync(arguments.GetGuid("workspaceId"), null, cancellationToken);
        return McpToolResult.FromJson(new
        {
            lists = lists.Select(l => new { l.Id, l.WorkspaceId, l.Name, l.TemplateKey, l.IsLibrary, contentTypes = l.ContentTypes.Select(c => c.Name) }),
        });
    }
}

internal sealed class QueryItemsTool(IListItemStore items) : BuiltInTool(
    "query_items",
    "Reads items of a list (tasks, events, documents, …). filter and orderBy use OData over the fields, e.g. filter \"fields/status eq 'open'\", orderBy \"fields/dueDate asc\".",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("filter", "string", "OData $filter, e.g. fields/title eq 'x'.", false),
        ("orderBy", "string", "OData $orderby.", false),
        ("top", "integer", "Maximum number of items (1-200, default 50).", false)),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var top = Math.Clamp(arguments.GetInt32("top") ?? 50, 1, 200);
        var (found, error) = await items.QueryAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"),
            new ListItemQuery(arguments.GetString("filter"), arguments.GetString("orderBy"), top), cancellationToken);
        return error is not null
            ? McpToolResult.Error(error)
            : McpToolResult.FromJson(new { items = found.Select(Item) });
    }
}

internal sealed class GetItemTool(IListItemStore items) : BuiltInTool(
    "get_item",
    "Reads one item with all its fields.",
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
        ("fields", "object", "Field values by field name, e.g. {\"title\": \"Call Bob\", \"dueDate\": \"2026-10-01\"}.", true),
        ("contentTypeId", "string", "Content type id (default: the list's first).", false)),
    "list.write",
    false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken) =>
        FromResult(await items.CreateAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredObject("fields"), arguments.GetGuid("contentTypeId"), cancellationToken));
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
            arguments.GetInt32("version") is { } version and >= 0 ? (uint)version : null, cancellationToken));
}
