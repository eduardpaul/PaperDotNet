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
    "relate_items", "Adds an optional typed/directed relationship. related=false removes an untyped link; remove_item_relationship removes a specific edge. Requires write access to both items; safe to repeat.",
    McpSchema.ObjectSchema(
        ("itemId", "string", "Stable item id.", true),
        ("otherId", "string", "Other content item's stable id.", true),
        ("related", "boolean", "true to add, false to remove an untyped link (default true).", false),
        ("type", "string", "Optional relationship type id or label (open vocabulary).", false),
        ("directed", "boolean", "Optional direction; must match the selected type.", false)), "list.write", false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken) =>
        FromResult(arguments.GetBoolean("related") == false
            ? await items.RelateAsync(arguments.GetRequiredGuid("itemId"), arguments.GetRequiredGuid("otherId"), false, cancellationToken)
            : await items.AddRelationshipAsync(arguments.GetRequiredGuid("itemId"), arguments.GetRequiredGuid("otherId"), new RelationshipOptions(arguments.GetString("type"), arguments.GetBoolean("directed")), cancellationToken));
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

internal sealed class ItemRelationshipsTool(IListItemStore items) : BuiltInTool(
    "list_item_relationships", "Lists graph edges with predicates, direction and readable peers. Follow nextCursor for more.",
    McpSchema.ObjectSchema(("itemId", "string", "Stable item id.", true), ("type", "string", "Optional type id or label.", false),
        ("direction", "string", "both, incoming or outgoing.", false), ("top", "integer", "Page size, maximum 500.", false), ("cursor", "string", "Previous nextCursor.", false)), "list.read", true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments args, CancellationToken ct)
    {
        Guid? after = null;
        if (args.GetString("cursor") is { } cursor)
        {
            if (!PageRequest.TryDecodeCursor(cursor, out var id)) return McpToolResult.Error("The cursor is invalid.");
            after = id;
        }
        try
        {
            var page = await items.GetRelationshipsAsync(args.GetRequiredGuid("itemId"), args.GetString("type"), args.GetString("direction"), args.GetInt32("top") ?? DefaultPageSize, after, ct);
            return page is null ? McpToolResult.Error("The item was not found.") : McpToolResult.FromJson(new { value = page.Items, nextCursor = page.NextCursor });
        }
        catch (ArgumentException ex) { return McpToolResult.Error(ex.Message); }
    }
}

internal sealed class RemoveItemRelationshipTool(IListItemStore items) : BuiltInTool(
    "remove_item_relationship", "Removes one graph edge by id. Requires write access to both endpoints.",
    McpSchema.ObjectSchema(("itemId", "string", "Either endpoint's stable id.", true), ("relationshipId", "string", "Edge id from list_item_relationships.", true)), "list.write", false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments args, CancellationToken ct) =>
        FromResult(await items.RemoveRelationshipAsync(args.GetRequiredGuid("itemId"), args.GetRequiredGuid("relationshipId"), ct));
}

internal sealed class RelationshipTypesTool(IListItemStore items) : BuiltInTool(
    "list_relationship_types", "Lists the open taxonomy vocabulary of predicates, with direction, inverse labels and limits.",
    McpSchema.ObjectSchema(), "list.read", true)
{
    public override async Task<McpToolResult> CallAsync(McpArguments args, CancellationToken ct) => McpToolResult.FromJson(await items.GetRelationshipTypesAsync(ct));
}

internal sealed class CreateRelationshipTypeTool(IListItemStore items) : BuiltInTool(
    "create_relationship_type", "Creates a taxonomy predicate with immutable direction, optional inverse label and endpoint limits. Safe to repeat with the same settings.",
    McpSchema.ObjectSchema(("name", "string", "Predicate name.", true), ("directed", "boolean", "Directed (default false).", false),
        ("inverseLabel", "string", "Label at the target endpoint.", false), ("maxIncoming", "integer", "Maximum incoming edges per item (1–1000).", false),
        ("maxOutgoing", "integer", "Maximum outgoing edges per item (1–1000).", false)), "list.write", false)
{
    public override async Task<McpToolResult> CallAsync(McpArguments args, CancellationToken ct)
    {
        try
        {
            return McpToolResult.FromJson(await items.EnsureRelationshipTypeAsync(new RelationshipTypeOptions(args.GetRequiredString("name"), args.GetBoolean("directed") ?? false,
            args.GetString("inverseLabel"), args.GetInt32("maxIncoming"), args.GetInt32("maxOutgoing")), ct));
        }
        catch (ArgumentException ex) { return McpToolResult.Error(ex.Message); }
    }
}
