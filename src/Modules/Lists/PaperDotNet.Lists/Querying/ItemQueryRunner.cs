using Microsoft.OData;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Querying;

/// <summary>One page of items, with the total (first page, when asked) and the cursor of the next page.</summary>
internal sealed record ItemRunResult(IReadOnlyList<ListItem> Items, long? Count, string? NextCursor);

/// <summary>Parses, validates and runs item queries against a list, trimmed to what the caller may read.</summary>
internal sealed class ItemQueryRunner(IItemQueries queries, FieldTypeRegistry fieldTypes, TimeProvider time)
{
    /// <summary>Checks <c>$filter</c>/<c>$orderby</c> against the list's fields; returns an error or null.</summary>
    public string? Validate(ListSchema schema, string? filter, string? orderBy, Guid? userId) =>
        ItemQueryParser.Parse(schema.Fields, fieldTypes, [filter], orderBy, userId, time.GetUtcNow()).Error;

    public async Task<(ItemRunResult? Result, string? Error)> RunAsync(
        ListCaller caller, ListSchema schema, IEnumerable<string?> filters, string? orderBy, int top, string? skipToken, bool count,
        FolderMode folders, Guid? parentId, CancellationToken cancellationToken, Guid? itemId = null)
    {
        if (!ItemCursor.TryDecode(skipToken, out var cursor))
        {
            return (null, "The cursor is not valid. Omit it to read the first page.");
        }

        var (parsed, error) = ItemQueryParser.Parse(schema.Fields, fieldTypes, filters, orderBy, caller.UserId, time.GetUtcNow());
        if (parsed is null)
        {
            return (null, error);
        }

        if ((cursor.After is not null && parsed.OrderBy is not null) || (cursor.Offset > 0 && parsed.OrderBy is null))
        {
            return (null, "The cursor does not match this ordering. Omit it to read the first page.");
        }

        var take = Math.Clamp(top, 1, ListItemQuery.MaxTop);
        var query = new ItemQuery(
            caller.TenantId, [schema.List.Id], parsed, schema.Access.Scopes(WorkspaceAccessLevel.Read), folders, parentId, cursor, take,
            count && skipToken is null, itemId, FieldIndex.Ready(schema.List));
        try
        {
            var result = await queries.QueryAsync(query, cancellationToken);
            var next = !result.HasMore ? null
                : parsed.OrderBy is null ? ItemCursor.Keyset(result.Items[^1].Id)
                : ItemCursor.ForOffset(cursor.Offset + take);
            return (new ItemRunResult(result.Items, result.Count, next), null);
        }
        catch (Exception ex) when (ex is NotSupportedException or ODataException)
        {
            return (null, ex.Message);
        }
    }
}
