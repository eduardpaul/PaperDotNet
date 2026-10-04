using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;

namespace PaperDotNet.Lists.Features;

internal static class ItemSnapshots
{
    public static ItemSnapshot Capture(ListItem item, ListSchema schema) => Capture(item, schema.List, schema.FindContentType(item.ContentTypeId));

    public static ItemSnapshot Capture(ListItem item, ListDefinition list, ContentType? contentType)
    {
        var fields = JsonNode.Parse(item.Fields)!.AsObject();
        fields["title"] = item.Title;
        var types = (contentType?.Fields ?? []).ToDictionary(field => field.Name,
            field => new ItemSnapshotField(field.Type, field.AllowMultiple || field.Type == "keywords"), StringComparer.Ordinal);
        types["title"] = new("text", false);
        return new(item.Id, list.WorkspaceId, list.Id, list.Name, item.ContentTypeId, contentType?.Name, contentType?.Key,
            item.ParentId, item.IsFolder, fields, types);
    }
}
