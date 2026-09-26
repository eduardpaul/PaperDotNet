using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;

namespace PaperDotNet.Mcp.Features;

internal sealed class ListChildrenTool(IListItemStore items) : BuiltInTool(
    "list_children",
    "Folders and items directly inside one folder (omit folderId for the list root). " +
    "query_items lists items from every folder and skips folders; this tool is how you see the folder tree. " +
    $"Page size is top (1-{ListItemQuery.MaxTop}, default {DefaultPageSize}). Pass nextCursor back as cursor with the same filter and orderBy.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("folderId", "string", "Folder id. Omit for the list root.", false),
        ("filter", "string", "OData $filter.", false),
        ("orderBy", "string", "OData $orderby.", false),
        ("top", "integer", $"Page size (1-{ListItemQuery.MaxTop}, default {DefaultPageSize}).", false),
        ("cursor", "string", "nextCursor from the previous page.", false)),
    "list.read",
    true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var (page, error) = await items.ListChildrenAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetGuid("folderId"),
            QueryItemsTool.PageQuery(arguments), cancellationToken);
        return error is not null ? McpToolResult.Error(error) : Page(page!.Items, page.NextCursor);
    }
}

internal sealed class CreateFolderTool(IListItemStore items) : BuiltInTool(
    "create_folder",
    "Creates a folder. parentId is the folder to create it in; omit it for the list root. Fails when the list does not allow folders.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("title", "string", "Folder name.", true),
        ("parentId", "string", "Parent folder id. Omit for the list root.", false)),
    "list.write",
    false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken) =>
        FromResult(await items.CreateFolderAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredString("title"),
            arguments.GetGuid("parentId"), cancellationToken));
}

internal sealed class EnsureFolderTool(IListItemStore items) : BuiltInTool(
    "ensure_folder",
    "Returns the folder at a path of folder titles, creating any missing folder. path uses '/' between names, for example 'Invoices/2026'.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("path", "string", "Folder titles from the list root, separated by '/'.", true)),
    "list.write",
    false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var path = arguments.GetRequiredString("path").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (path.Length == 0)
        {
            return McpToolResult.Error("The path needs at least one folder name.");
        }

        var (folderId, problem) = await items.EnsureFolderAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), path, cancellationToken);
        return problem is not null ? FromResult(problem) : McpToolResult.FromJson(new { folderId, path });
    }
}

internal sealed class MoveItemTool(IListItemStore items) : BuiltInTool(
    "move_item",
    "Moves an item or folder into another folder of the same list. Omit folderId to move it to the list root.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("itemId", "string", "Item or folder id.", true),
        ("folderId", "string", "Destination folder id. Omit to move the item to the list root.", false)),
    "list.write",
    false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        return FromResult(await items.MoveAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredGuid("itemId"),
            arguments.GetGuid("folderId"), cancellationToken));
    }
}

internal sealed class DeleteItemTool(IListItemStore items) : BuiltInTool(
    "delete_item",
    "Moves an item or an empty folder to the recycle bin. Pass version from get_item so you do not delete a change you have not read. A folder that still has children is rejected.",
    McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "List id.", true),
        ("itemId", "string", "Item or folder id.", true),
        ("version", "integer", "The version you read (optional).", false)),
    "list.write",
    false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken) =>
        FromResult(await items.DeleteAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredGuid("itemId"),
            Version(arguments), cancellationToken));
}
