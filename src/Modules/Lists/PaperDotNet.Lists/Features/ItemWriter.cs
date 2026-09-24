using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Features;

/// <summary>Outcome of an item write: the item, or validation errors, or a conflict.</summary>
internal sealed record ItemWriteResult(ListItem? Item, Dictionary<string, string[]>? Errors = null, string? Conflict = null)
{
    public static ItemWriteResult Invalid(string key, string message) => new(null, new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>
/// All item writes go through here: field validation and normalization, folders
/// and content types. Event handlers (phase 1c) hook in at this point.
/// </summary>
internal sealed class ItemWriter(ListsDbContext db, FieldTypeRegistry fieldTypes, IUserDirectory users) : IFieldValidationContext
{
    public const int TitleMaxLength = 1024;
    private const int MaxFolderDepth = 64;

    public async Task<ItemWriteResult> CreateAsync(
        ListSchema schema, Guid? contentTypeId, Guid? parentId, bool isFolder, JsonElement? fields, CancellationToken ct)
    {
        var contentType = contentTypeId is { } id ? schema.FindContentType(id) : schema.DefaultContentType;
        if (contentType is null)
        {
            return ItemWriteResult.Invalid("contentTypeId", "The content type is not used by this list.");
        }

        if (isFolder && !schema.List.AllowFolders)
        {
            return ItemWriteResult.Invalid("isFolder", "This list does not allow folders.");
        }

        if (await ValidateParentAsync(schema, parentId, null, ct) is { } parentError)
        {
            return parentError;
        }

        var item = new ListItem
        {
            Id = Ids.New(),
            ListId = schema.List.Id,
            ContentTypeId = contentType.Id,
            ParentId = parentId,
            IsFolder = isFolder,
            Title = string.Empty,
        };

        var errors = new Dictionary<string, string[]>();
        var values = await NormalizeAsync(isFolder ? [] : contentType.Fields, fields, existing: null, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        ApplyValues(item, values);
        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return new ItemWriteResult(item);
    }

    /// <summary>Merges <paramref name="fields"/> into the item (null removes a value).</summary>
    public async Task<ItemWriteResult> UpdateAsync(
        ListSchema schema, ListItem item, Guid? contentTypeId, Optional<Guid?> parentId, JsonElement? fields, CancellationToken ct)
    {
        var contentType = schema.FindContentType(contentTypeId ?? item.ContentTypeId);
        if (contentType is null)
        {
            return ItemWriteResult.Invalid("contentTypeId", "The content type is not used by this list.");
        }

        if (parentId.HasValue)
        {
            if (await ValidateParentAsync(schema, parentId.Value, item, ct) is { } parentError)
            {
                return parentError;
            }

            item.ParentId = parentId.Value;
        }

        var existing = JsonNode.Parse(item.Fields)!.AsObject();
        existing["title"] = item.Title;
        if (contentType.Id != item.ContentTypeId)
        {
            // Changing the content type keeps only the values its fields define.
            var allowed = contentType.Fields.Select(f => f.Name).Append("title").ToHashSet(StringComparer.Ordinal);
            foreach (var name in existing.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToList())
            {
                existing.Remove(name);
            }
        }

        var errors = new Dictionary<string, string[]>();
        var values = await NormalizeAsync(item.IsFolder ? [] : contentType.Fields, fields, existing, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        item.ContentTypeId = contentType.Id;
        ApplyValues(item, values);
        await db.SaveChangesAsync(ct);
        return new ItemWriteResult(item);
    }

    public async Task<ItemWriteResult> DeleteAsync(ListItem item, CancellationToken ct)
    {
        if (item.IsFolder && await db.Items.AnyAsync(i => i.ParentId == item.Id, ct))
        {
            return new ItemWriteResult(null, Conflict: "The folder is not empty.");
        }

        db.Items.Remove(item);
        await db.SaveChangesAsync(ct);
        return new ItemWriteResult(item);
    }

    public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken) => users.IsActiveAsync(userId, cancellationToken);

    public Task<bool> ItemExistsAsync(Guid listId, Guid itemId, CancellationToken cancellationToken) =>
        db.Items.AnyAsync(i => i.ListId == listId && i.Id == itemId && !i.IsFolder, cancellationToken);

    /// <summary>
    /// Validates and normalizes field values. <paramref name="existing"/> is the current
    /// values (update) or null (create: required fields and defaults apply).
    /// </summary>
    private async Task<JsonObject> NormalizeAsync(
        IReadOnlyList<FieldDefinition> definitions, JsonElement? input, JsonObject? existing, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var result = existing ?? [];
        var byName = definitions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        if (input is { } body)
        {
            if (body.ValueKind != JsonValueKind.Object)
            {
                errors["fields"] = ["fields must be a JSON object."];
                return result;
            }

            foreach (var property in body.EnumerateObject())
            {
                if (property.Name == "title")
                {
                    if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        errors["fields.title"] = ["title is required and must be text."];
                    }
                    else if (property.Value.GetString()!.Length > TitleMaxLength)
                    {
                        errors["fields.title"] = [$"At most {TitleMaxLength} characters are allowed."];
                    }
                    else
                    {
                        result["title"] = property.Value.GetString()!.Trim();
                    }

                    continue;
                }

                if (!byName.TryGetValue(property.Name, out var definition))
                {
                    errors[$"fields.{property.Name}"] = ["Unknown field for this content type."];
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    result.Remove(property.Name);
                    continue;
                }

                var normalized = await fieldTypes.Find(definition.Type)!.NormalizeAsync(property.Value, definition, this, ct);
                if (normalized.Error is not null)
                {
                    errors[$"fields.{property.Name}"] = [normalized.Error];
                }
                else
                {
                    result[property.Name] = normalized.Value;
                }
            }
        }

        if (existing is null)
        {
            foreach (var definition in definitions.Where(d => d.DefaultValue is not null && !result.ContainsKey(d.Name)))
            {
                using var defaultValue = JsonDocument.Parse(definition.DefaultValue!);
                var normalized = await fieldTypes.Find(definition.Type)!.NormalizeAsync(defaultValue.RootElement, definition, this, ct);
                if (normalized.Error is null)
                {
                    result[definition.Name] = normalized.Value;
                }
            }
        }

        if (!result.ContainsKey("title") && !errors.ContainsKey("fields.title"))
        {
            errors["fields.title"] = ["title is required."];
        }

        foreach (var definition in definitions.Where(d => d.Required && !result.ContainsKey(d.Name)))
        {
            errors.TryAdd($"fields.{definition.Name}", ["This field is required."]);
        }

        return result;
    }

    private static void ApplyValues(ListItem item, JsonObject values)
    {
        item.Title = values["title"]!.GetValue<string>();
        values.Remove("title");
        item.Fields = values.ToJsonString();
    }

    private async Task<ItemWriteResult?> ValidateParentAsync(ListSchema schema, Guid? parentId, ListItem? moving, CancellationToken ct)
    {
        if (parentId is not { } id)
        {
            return null;
        }

        var parent = await db.Items.AsNoTracking()
            .Where(i => i.Id == id && i.ListId == schema.List.Id && i.IsFolder)
            .Select(i => new { i.Id, i.ParentId })
            .FirstOrDefaultAsync(ct);
        if (parent is null)
        {
            return ItemWriteResult.Invalid("parentId", "The parent must be a folder in the same list.");
        }

        if (moving is { IsFolder: true })
        {
            // A folder cannot move into itself or one of its descendants.
            Guid? current = parent.Id;
            for (var depth = 0; current is not null && depth < MaxFolderDepth; depth++)
            {
                if (current == moving.Id)
                {
                    return ItemWriteResult.Invalid("parentId", "A folder cannot be moved into itself.");
                }

                var next = current;
                current = await db.Items.AsNoTracking().Where(i => i.Id == next).Select(i => i.ParentId).FirstOrDefaultAsync(ct);
            }
        }

        return null;
    }
}

/// <summary>Distinguishes "not sent" from "sent as null" in PATCH bodies.</summary>
internal readonly record struct Optional<T>(bool HasValue, T Value)
{
    public static Optional<T> None => default;

    public static Optional<T> Of(T value) => new(true, value);
}
