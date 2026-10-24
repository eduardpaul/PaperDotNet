using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Content;

namespace PaperDotNet.Host.Content;

/// <summary>Numeric field values cross JavaScript clients as strings; stored JSON keeps PaperGo's exact types.</summary>
internal static class ContentWire
{
    public static EffectiveSchema Schema(EffectiveSchema schema, bool incoming) => schema with
    {
        Fields = schema.Fields.Select(field => field.Options?.DefaultValue is JsonElement value
            ? field with
            {
                Options = field.Options with
                {
                    DefaultValue = Values(JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [field.Id] = value }),
                new EffectiveSchema([field], []), incoming).GetProperty(field.Id)
                }
            } : field).ToArray()
    };
    private static bool Numeric(SchemaField field) => field.Type is "integer" or "decimal" or "number" or "currency";

    public static JsonElement Values(JsonElement values, EffectiveSchema schema, bool incoming)
    {
        if (values.ValueKind != JsonValueKind.Object) throw new ArgumentException("Values must be an object.");
        var result = JsonNode.Parse(values.GetRawText())!.AsObject();
        foreach (var field in schema.Fields.Where(Numeric))
        {
            if (!result.TryGetPropertyValue(field.Id, out var value) || value is null) continue;
            if (field.Options?.Multiple == true && value is JsonArray array)
                result[field.Id] = new JsonArray(array.Select(x => Number(x, field, incoming)).ToArray());
            else result[field.Id] = Number(value, field, incoming);
        }
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonNode? Number(JsonNode? value, SchemaField field, bool incoming)
    {
        if (value is null) return null;
        var element = JsonSerializer.SerializeToElement(value);
        if (!incoming && element.ValueKind == JsonValueKind.Number) return JsonValue.Create(element.GetRawText());
        if (incoming && field.Type != "decimal" && element.ValueKind == JsonValueKind.String)
        {
            var parsed = JsonNode.Parse(element.GetString()!);
            if (parsed is null || JsonSerializer.SerializeToElement(parsed).ValueKind != JsonValueKind.Number)
                throw new ArgumentException("A numeric field requires a number.");
            return parsed;
        }
        return value.DeepClone();
    }

    public static async Task<ContentInput> InputAsync(ContentSession session, string resourceId, ContentInput input, CancellationToken ct)
    {
        var parent = await ContentEndpoints.ResourceAsync(session, resourceId, "write", ct);
        var collectionId = parent.Kind is "list" or "library" ? parent.Id : parent.ContainerId;
        var collection = await session.Db.Resources.AsNoTracking().SingleAsync(x => x.Id == collectionId, ct);
        var revision = await session.Db.SchemaRevisions.AsNoTracking().SingleAsync(x => x.Id == collection.SchemaHeadId, ct);
        var schema = JsonSerializer.Deserialize<EffectiveSchema>(revision.Definition)!;
        return input with { Values = Values(input.Values, schema, true) };
    }
}
