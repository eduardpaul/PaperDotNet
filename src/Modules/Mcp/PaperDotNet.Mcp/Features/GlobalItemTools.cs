using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;

namespace PaperDotNet.Mcp.Features;

internal sealed class GetGlobalItemTool(IListItemStore items) : BuiltInTool(
    "get_global_item", "Resolves an item by stable id to its current workspace and list, with its fields and version.",
    McpSchema.ObjectSchema(("itemId", "string", "Stable item id.", true)), "list.read", true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var item = await items.GetByIdAsync(arguments.GetRequiredGuid("itemId"), cancellationToken);
        return item is null ? McpToolResult.Error("The item was not found (or you cannot read it).") : McpToolResult.FromJson(Item(item));
    }
}

internal sealed class RelatedItemsTool(IListItemStore items) : BuiltInTool(
    "list_related_items", "Lists readable items related to an item across lists and workspaces. Follow nextCursor for more.",
    McpSchema.ObjectSchema(
        ("itemId", "string", "Stable item id.", true),
        ("top", "integer", "Page size (default 50, maximum 500).", false),
        ("cursor", "string", "nextCursor from the previous page.", false)), "list.read", true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        Guid? after = null;
        if (arguments.GetString("cursor") is { } cursor)
        {
            if (!PageRequest.TryDecodeCursor(cursor, out var id))
            {
                return McpToolResult.Error("The cursor is invalid.");
            }

            after = id;
        }

        var page = await items.GetRelatedAsync(arguments.GetRequiredGuid("itemId"), arguments.GetInt32("top") ?? DefaultPageSize, after, cancellationToken);
        return page is null ? McpToolResult.Error("The item was not found (or you cannot read it).") : Page(page.Items, page.NextCursor);
    }
}

internal sealed class RelateItemsTool(IListItemStore items) : BuiltInTool(
    "relate_items", "Adds a symmetric relationship (or removes it with related=false). Requires write access to both items; safe to repeat.",
    McpSchema.ObjectSchema(
        ("itemId", "string", "Stable item id.", true),
        ("otherId", "string", "Other content item's stable id.", true),
        ("related", "boolean", "true to add, false to remove (default true).", false)), "list.write", false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken) =>
        FromResult(await items.RelateAsync(arguments.GetRequiredGuid("itemId"), arguments.GetRequiredGuid("otherId"),
            arguments.GetBoolean("related") ?? true, cancellationToken));
}

internal sealed class MoveItemToListTool(IListItemStore items) : BuiltInTool(
    "move_item_to_list", "Moves a content item to a compatible list, preserving id, history and links. Inherits destination permissions.",
    McpSchema.ObjectSchema(
        ("itemId", "string", "Stable item id.", true),
        ("workspaceId", "string", "Destination workspace id.", true),
        ("listId", "string", "Destination list id.", true),
        ("folderId", "string", "Destination folder id; omit for the list root.", false),
        ("version", "integer", "Version read from get_global_item.", true)), "list.write", false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        if (Version(arguments) is not { } version)
        {
            return McpToolResult.Error("Read the item first and pass its version.");
        }

        return FromResult(await items.MoveToAsync(arguments.GetRequiredGuid("itemId"), arguments.GetRequiredGuid("workspaceId"),
            arguments.GetRequiredGuid("listId"), arguments.GetGuid("folderId"), version, cancellationToken));
    }
}
