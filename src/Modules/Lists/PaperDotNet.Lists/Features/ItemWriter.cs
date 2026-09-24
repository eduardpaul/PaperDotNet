using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Messaging;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Outcome of an item write: the item, validation errors, a conflict or a cancellation by a receiver.</summary>
internal sealed record ItemWriteResult(ListItem? Item, Dictionary<string, string[]>? Errors = null, string? Conflict = null, string? Cancelled = null)
{
    public static ItemWriteResult Invalid(string key, string message) => new(null, new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>
/// All item writes go through here: field validation and normalization, folders,
/// content types, synchronous before/after receivers (idea 0012) and integration
/// events published through the transactional outbox.
/// </summary>
internal sealed partial class ItemWriter(
    ListsDbContext db,
    FieldTypeRegistry fieldTypes,
    IUserDirectory users,
    IEnumerable<IItemEventReceiver> receivers,
    IOutbox outbox,
    ITermStore terms,
    ITenantContext tenant,
    ICurrentUser currentUser,
    ILogger<ItemWriter> logger) : IFieldValidationContext
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

        var definitions = isFolder ? [] : contentType.Fields;
        var errors = new Dictionary<string, string[]>();
        var values = await NormalizeAsync(definitions, fields, [], applyDefaults: true, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
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

        var scope = Scope(schema, item);
        var snapshot = values.ToJsonString();
        var context = new ItemChangingContext { Kind = ItemEventKind.Adding, Scope = scope, ItemId = item.Id, UserId = currentUser.UserId, After = values };
        if (await RunBeforeAsync(context, ct) is { } cancelled)
        {
            return cancelled;
        }

        values = await FinalizeAsync(definitions, snapshot, context.After!, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        ApplyValues(item, values);
        db.Items.Add(item);
        var changed = Values(item).Select(p => p.Key).ToList();
        await outbox.SaveChangesAsync(db, [Event(ItemEventKind.Adding, item, schema, changed)], cancellationToken: ct);
        await RunAfterAsync(new ItemChangedContext(ItemEventKind.Adding, scope, item.Id, currentUser.UserId, null, Values(item), changed), ct);
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

        if (parentId.HasValue && await ValidateParentAsync(schema, parentId.Value, item, ct) is { } parentError)
        {
            return parentError;
        }

        var before = Values(item);
        var current = (JsonObject)before.DeepClone();
        if (contentType.Id != item.ContentTypeId)
        {
            // Changing the content type keeps only the values its fields define.
            var allowed = contentType.Fields.Select(f => f.Name).Append("title").ToHashSet(StringComparer.Ordinal);
            foreach (var name in current.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToList())
            {
                current.Remove(name);
            }
        }

        var definitions = item.IsFolder ? [] : contentType.Fields;
        var errors = new Dictionary<string, string[]>();
        var values = await NormalizeAsync(definitions, fields, current, applyDefaults: false, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        var scope = Scope(schema, item);
        var snapshot = values.ToJsonString();
        var context = new ItemChangingContext
        {
            Kind = ItemEventKind.Updating,
            Scope = scope,
            ItemId = item.Id,
            UserId = currentUser.UserId,
            Before = (JsonObject)before.DeepClone(),
            After = values,
        };
        if (await RunBeforeAsync(context, ct) is { } cancelled)
        {
            return cancelled;
        }

        values = await FinalizeAsync(definitions, snapshot, context.After!, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        if (parentId.HasValue)
        {
            item.ParentId = parentId.Value;
        }

        item.ContentTypeId = contentType.Id;
        ApplyValues(item, values);
        var after = Values(item);
        var changed = ChangedFields(before, after);
        await outbox.SaveChangesAsync(db, [Event(ItemEventKind.Updating, item, schema, changed)], cancellationToken: ct);
        await RunAfterAsync(new ItemChangedContext(ItemEventKind.Updating, scope, item.Id, currentUser.UserId, before, after, changed), ct);
        return new ItemWriteResult(item);
    }

    public async Task<ItemWriteResult> DeleteAsync(ListSchema schema, ListItem item, CancellationToken ct)
    {
        if (item.IsFolder && await db.Items.AnyAsync(i => i.ParentId == item.Id, ct))
        {
            return new ItemWriteResult(null, Conflict: "The folder is not empty.");
        }

        var before = Values(item);
        var scope = Scope(schema, item);
        var context = new ItemChangingContext { Kind = ItemEventKind.Deleting, Scope = scope, ItemId = item.Id, UserId = currentUser.UserId, Before = before };
        if (await RunBeforeAsync(context, ct) is { } cancelled)
        {
            return cancelled;
        }

        db.Items.Remove(item);
        await outbox.SaveChangesAsync(db, [Event(ItemEventKind.Deleting, item, schema, [])], cancellationToken: ct);
        await RunAfterAsync(new ItemChangedContext(ItemEventKind.Deleting, scope, item.Id, currentUser.UserId, before, null, []), ct);
        return new ItemWriteResult(item);
    }

    public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken) => users.IsActiveAsync(userId, cancellationToken);

    public Task<bool> ItemExistsAsync(Guid listId, Guid itemId, CancellationToken cancellationToken) =>
        db.Items.AnyAsync(i => i.ListId == listId && i.Id == itemId && !i.IsFolder, cancellationToken);

    public async Task<Guid?> ResolveTermAsync(Guid? termSetId, string value, CancellationToken cancellationToken)
    {
        var setId = termSetId ?? (await terms.GetKeywordsSetAsync(cancellationToken)).Id;
        return await terms.ResolveAsync(setId, value, allowCreate: true, cancellationToken);
    }

    /// <summary>All values of an item, including <c>title</c>.</summary>
    internal static JsonObject Values(ListItem item)
    {
        var values = JsonNode.Parse(item.Fields)!.AsObject();
        values["title"] = item.Title;
        return values;
    }

    private IEnumerable<IItemEventReceiver> ReceiversFor(ItemEventScope scope) =>
        receivers.Where(r => r.AppliesTo(scope)).OrderBy(r => r.Sequence);

    private async Task<ItemWriteResult?> RunBeforeAsync(ItemChangingContext context, CancellationToken ct)
    {
        foreach (var receiver in ReceiversFor(context.Scope))
        {
            await (context.Kind switch
            {
                ItemEventKind.Adding => receiver.ItemAddingAsync(context, ct),
                ItemEventKind.Updating => receiver.ItemUpdatingAsync(context, ct),
                _ => receiver.ItemDeletingAsync(context, ct),
            });
            if (context.IsCancelled)
            {
                return new ItemWriteResult(null, Cancelled: context.CancelMessage);
            }
        }

        return null;
    }

    private async Task RunAfterAsync(ItemChangedContext context, CancellationToken ct)
    {
        foreach (var receiver in ReceiversFor(context.Scope))
        {
            try
            {
                await (context.Kind switch
                {
                    ItemEventKind.Adding => receiver.ItemAddedAsync(context, ct),
                    ItemEventKind.Updating => receiver.ItemUpdatedAsync(context, ct),
                    _ => receiver.ItemDeletedAsync(context, ct),
                });
            }
#pragma warning disable CA1031 // After receivers must never undo or fail a committed change.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogAfterReceiverFailed(ex, receiver.GetType().Name, context.ItemId);
            }
        }
    }

    /// <summary>Re-validates the values only when a before receiver changed them.</summary>
    private async Task<JsonObject> FinalizeAsync(
        IReadOnlyList<FieldDefinition> definitions, string validatedSnapshot, JsonObject afterReceivers, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var json = afterReceivers.ToJsonString();
        if (json == validatedSnapshot)
        {
            return afterReceivers;
        }

        using var document = JsonDocument.Parse(json);
        return await NormalizeAsync(definitions, document.RootElement, [], applyDefaults: false, errors, ct);
    }

    /// <summary>
    /// Validates and normalizes <paramref name="input"/> on top of <paramref name="baseValues"/>
    /// (null in the input removes a value). Required fields and <c>title</c> are enforced.
    /// </summary>
    private async Task<JsonObject> NormalizeAsync(
        IReadOnlyList<FieldDefinition> definitions, JsonElement? input, JsonObject baseValues, bool applyDefaults,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var result = baseValues;
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

        if (applyDefaults)
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
        var copy = (JsonObject)values.DeepClone();
        item.Title = copy["title"]!.GetValue<string>();
        copy.Remove("title");
        item.Fields = copy.ToJsonString();
    }

    private static List<string> ChangedFields(JsonObject before, JsonObject after) =>
        before.Select(p => p.Key).Union(after.Select(p => p.Key))
            .Where(key => !JsonNode.DeepEquals(before[key], after[key]))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static ItemEventScope Scope(ListSchema schema, ListItem item) =>
        new(schema.List.WorkspaceId, schema.List.Id, schema.List.Name, item.ContentTypeId, item.IsFolder);

    private ItemEvent Event(ItemEventKind kind, ListItem item, ListSchema schema, IReadOnlyList<string> changed)
    {
        var tenantId = tenant.TenantId!.Value;
        var tenantIdentifier = tenant.TenantIdentifier!;
        return kind switch
        {
            ItemEventKind.Adding => new ItemAdded
            {
                TenantId = tenantId,
                TenantIdentifier = tenantIdentifier,
                UserId = currentUser.UserId,
                WorkspaceId = schema.List.WorkspaceId,
                ListId = schema.List.Id,
                ItemId = item.Id,
                ContentTypeId = item.ContentTypeId,
                IsFolder = item.IsFolder,
                ChangedFields = changed,
            },
            ItemEventKind.Updating => new ItemUpdated
            {
                TenantId = tenantId,
                TenantIdentifier = tenantIdentifier,
                UserId = currentUser.UserId,
                WorkspaceId = schema.List.WorkspaceId,
                ListId = schema.List.Id,
                ItemId = item.Id,
                ContentTypeId = item.ContentTypeId,
                IsFolder = item.IsFolder,
                ChangedFields = changed,
            },
            _ => new ItemDeleted
            {
                TenantId = tenantId,
                TenantIdentifier = tenantIdentifier,
                UserId = currentUser.UserId,
                WorkspaceId = schema.List.WorkspaceId,
                ListId = schema.List.Id,
                ItemId = item.Id,
                ContentTypeId = item.ContentTypeId,
                IsFolder = item.IsFolder,
                ChangedFields = changed,
            },
        };
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

    [LoggerMessage(Level = LogLevel.Error, Message = "After-event receiver {Receiver} failed for item {ItemId}.")]
    private partial void LogAfterReceiverFailed(Exception exception, string receiver, Guid itemId);
}

/// <summary>Distinguishes "not sent" from "sent as null" in PATCH bodies.</summary>
internal readonly record struct Optional<T>(bool HasValue, T Value)
{
    public static Optional<T> None => default;

    public static Optional<T> Of(T value) => new(true, value);
}
