using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Messaging;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Outcome of an item write: the item, validation errors, a conflict or a cancellation by a mutator.</summary>
internal sealed record ItemWriteResult(
    ListItem? Item, Dictionary<string, string[]>? Errors = null, string? Conflict = null, string? Cancelled = null, bool Forbidden = false)
{
    public static readonly ItemWriteResult Denied = new(null, Forbidden: true);

    public static ItemWriteResult Invalid(string key, string message) => new(null, new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>
/// All item writes go through here: field validation and normalization, folders, content types, item mutators
/// (ADR-0023) and integration events published through the transactional outbox. Queries copy their arguments into
/// locals (precompiled queries, ADR-0039).
/// </summary>
internal sealed class ItemWriter(
    ListsDbContext db,
    FieldTypeRegistry fieldTypes,
    IUserDirectory users,
    IEnumerable<IItemMutator> mutators,
    IOutbox outbox,
    ILiveEvents live,
    TimeProvider time)
{
    public const int TitleMaxLength = 1024;
    private const int MaxFolderDepth = 64;

    /// <summary>Creates the item with the given <paramref name="itemId"/>.</summary>
    public async Task<ItemWriteResult> CreateAsync(
        ListCaller caller, ListSchema schema, Guid? contentTypeId, Guid? parentId, bool isFolder, JsonElement? fields, Guid itemId, CancellationToken ct)
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

        var (parentError, scopeId) = await ValidateParentAsync(caller, schema, parentId, null, ct);
        if (parentError is not null)
        {
            return parentError;
        }

        // Folders hold values of their content type's fields too (LST-19), but required fields and defaults are for items.
        var definitions = contentType.Fields;
        var errors = new Dictionary<string, string[]>();
        var context = new ValidationContext(db, users, caller.TenantId);
        var values = await NormalizeAsync(context, definitions, fields, [], applyDefaults: !isFolder, enforceRequired: !isFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        var item = new ListItem
        {
            Id = itemId,
            TenantId = caller.TenantId,
            ListId = schema.List.Id,
            ContentTypeId = contentType.Id,
            ParentId = parentId,
            IsFolder = isFolder,
            ScopeId = scopeId,
            CreatedBy = caller.UserId,
        };

        var snapshot = values.ToJsonString();
        var mutation = new ItemMutationContext
        {
            Kind = ItemEventKind.Adding,
            Scope = Scope(schema, item),
            ItemId = item.Id,
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            After = values,
        };
        if (await MutateAsync(mutation, ct) is { } cancelled)
        {
            return cancelled;
        }

        values = await FinalizeAsync(context, definitions, snapshot, mutation.After!, !isFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        ApplyValues(item, values);
        db.Items.Add(item);
        var changed = Values(item).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        await AddVersionAsync(caller, schema, item, changed, ct);
        await outbox.SaveChangesAsync(db, [Event(Change.Added, caller, item, schema.List.WorkspaceId, changed)], ct);
        await PublishChangedAsync("added", caller, schema, item, ct);
        return new ItemWriteResult(item);
    }

    /// <summary>Merges <paramref name="fields"/> into the item (null removes a value), and moves it when <paramref name="parentId"/> is given.</summary>
    public async Task<ItemWriteResult> UpdateAsync(
        ListCaller caller, ListSchema schema, ListItem item, Guid? contentTypeId, Optional<Guid?> parentId, JsonElement? fields, CancellationToken ct)
    {
        var contentType = schema.FindContentType(contentTypeId ?? item.ContentTypeId);
        if (contentType is null)
        {
            return ItemWriteResult.Invalid("contentTypeId", "The content type is not used by this list.");
        }

        var newScopeId = item.ScopeId;
        if (parentId.HasValue)
        {
            var (parentError, parentScope) = await ValidateParentAsync(caller, schema, parentId.Value, item, ct);
            if (parentError is not null)
            {
                return parentError;
            }

            newScopeId = item.HasUniquePermissions ? item.ScopeId : parentScope;
            if (item.IsFolder && newScopeId != item.ScopeId)
            {
                // Moving a folder between permission scopes moves everything inside it too (ADR-0035): that comes with
                // item permissions (T08); until then all of a list's items share its scope.
                return new ItemWriteResult(null, Conflict: "The folder cannot move to a place with other permissions.");
            }
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

        var definitions = contentType.Fields;
        var errors = new Dictionary<string, string[]>();
        var context = new ValidationContext(db, users, caller.TenantId);
        var values = await NormalizeAsync(context, definitions, fields, current, applyDefaults: false, enforceRequired: !item.IsFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        var snapshot = values.ToJsonString();
        var mutation = new ItemMutationContext
        {
            Kind = ItemEventKind.Updating,
            Scope = Scope(schema, item),
            ItemId = item.Id,
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Before = (JsonObject)before.DeepClone(),
            After = values,
        };
        if (await MutateAsync(mutation, ct) is { } cancelled)
        {
            return cancelled;
        }

        values = await FinalizeAsync(context, definitions, snapshot, mutation.After!, !item.IsFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        if (parentId.HasValue)
        {
            item.ParentId = parentId.Value;
            item.ScopeId = newScopeId;
        }

        item.ContentTypeId = contentType.Id;
        ApplyValues(item, values);
        item.UpdatedBy = caller.UserId;
        var changed = ChangedFields(before, Values(item));
        if (changed.Count > 0)
        {
            await AddVersionAsync(caller, schema, item, changed, ct);
        }

        await outbox.SaveChangesAsync(db, [Event(Change.Updated, caller, item, schema.List.WorkspaceId, changed)], ct);
        await PublishChangedAsync("updated", caller, schema, item, ct);
        return new ItemWriteResult(item);
    }

    /// <summary>Moves the item to the recycle bin (a folder only when it is empty).</summary>
    public async Task<ItemWriteResult> DeleteAsync(ListCaller caller, ListSchema schema, ListItem item, CancellationToken ct)
    {
        if (item.IsFolder && await HasChildrenAsync(caller.TenantId, item.Id, ct))
        {
            return new ItemWriteResult(null, Conflict: "The folder is not empty.");
        }

        var mutation = new ItemMutationContext
        {
            Kind = ItemEventKind.Deleting,
            Scope = Scope(schema, item),
            ItemId = item.Id,
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Before = Values(item),
        };
        if (await MutateAsync(mutation, ct) is { } cancelled)
        {
            return cancelled;
        }

        db.Items.Remove(item);
        await outbox.SaveChangesAsync(db, [Event(Change.Deleted, caller, item, schema.List.WorkspaceId, [])], ct);
        await PublishChangedAsync("deleted", caller, schema, item, ct);
        return new ItemWriteResult(item);
    }

    /// <summary>
    /// Restores an item from the recycle bin. It returns to its folder when that folder is active, otherwise to the
    /// list root.
    /// </summary>
    public async Task RestoreAsync(ListCaller caller, ListSchema schema, ListItem item, CancellationToken ct)
    {
        if (item.ParentId is { } parentId && await FindAsync(caller.TenantId, schema.List.Id, parentId, ct) is not { IsFolder: true })
        {
            // Until item permissions are ported (T08) every item of a list has the list's scope.
            item.ParentId = null;
            if (!item.HasUniquePermissions)
            {
                item.ScopeId = schema.List.Id;
            }
        }

        item.DeletedAt = null;
        item.DeletedBy = null;
        item.UpdatedBy = caller.UserId;
        await outbox.SaveChangesAsync(db, [Event(Change.Restored, caller, item, schema.List.WorkspaceId, [])], ct);
        await PublishChangedAsync("restored", caller, schema, item, ct);
    }

    /// <summary>Deletes items of the recycle bin (tracked, of lists in <paramref name="workspaceId"/>) and their versions permanently.</summary>
    public async Task PurgeAsync(ListCaller caller, Guid workspaceId, IReadOnlyCollection<ListItem> deletedItems, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        foreach (var item in deletedItems)
        {
            var id = item.Id;
            context.ItemVersions.RemoveRange(await context.ItemVersions.Where(v => v.TenantId == tenant && v.ItemId == id).ToListAsync(ct));
        }

        // Removing an item that is already in the recycle bin deletes it (SaveChangesGuard).
        context.Items.RemoveRange(deletedItems);
        await outbox.SaveChangesAsync(context, [.. deletedItems.Select(item => (IntegrationEvent)Event(Change.Purged, caller, item, workspaceId, []))], ct);
    }

    /// <summary>Snapshots the item as a new version when the list keeps history, trimming the oldest versions.</summary>
    private async Task AddVersionAsync(ListCaller caller, ListSchema schema, ListItem item, IReadOnlyList<string> changed, CancellationToken cancellationToken)
    {
        if (schema.List.Versioning == ListVersionings.Off || item.IsFolder)
        {
            return;
        }

        var context = db;
        var tenant = caller.TenantId;
        var id = item.Id;
        var ct = cancellationToken;
        var last = await context.ItemVersions.Where(v => v.TenantId == tenant && v.ItemId == id).MaxAsync(v => (int?)v.Number, ct) ?? 0;
        context.ItemVersions.Add(new ItemVersion
        {
            Id = Ids.New(),
            TenantId = tenant,
            ItemId = id,
            ListId = item.ListId,
            Number = last + 1,
            ContentTypeId = item.ContentTypeId,
            Title = item.Title,
            Fields = item.Fields,
            ChangedFields = JsonSerializer.Serialize(changed, ListsJson.Default.IReadOnlyListString),
            CreatedAt = time.GetUtcNow(),
            CreatedBy = caller.UserId,
        });

        var keepFrom = last + 2 - schema.List.MaxVersions;
        if (keepFrom > 1)
        {
            context.ItemVersions.RemoveRange(await context.ItemVersions.Where(v => v.TenantId == tenant && v.ItemId == id && v.Number < keepFrom).ToListAsync(ct));
        }
    }

    private Task<bool> HasChildrenAsync(Guid tenantId, Guid folderId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var folder = folderId;
        var ct = cancellationToken;
        return context.Items.AnyAsync(i => i.TenantId == tenant && i.ParentId == folder && i.DeletedAt == null, ct);
    }

    /// <summary>All values of an item, including <c>title</c>.</summary>
    internal static JsonObject Values(ListItem item)
    {
        var values = JsonNode.Parse(item.Fields)!.AsObject();
        values["title"] = item.Title;
        return values;
    }

    private async Task<ItemWriteResult?> MutateAsync(ItemMutationContext context, CancellationToken ct)
    {
        foreach (var mutator in mutators.OrderBy(m => m.Sequence))
        {
            if (!await mutator.AppliesToAsync(context.Scope, ct))
            {
                continue;
            }

            await (context.Kind switch
            {
                ItemEventKind.Adding => mutator.ItemAddingAsync(context, ct),
                ItemEventKind.Updating => mutator.ItemUpdatingAsync(context, ct),
                _ => mutator.ItemDeletingAsync(context, ct),
            });
            if (context.IsCancelled)
            {
                return new ItemWriteResult(null, Cancelled: context.CancelMessage);
            }
        }

        return null;
    }

    /// <summary>Re-validates the values only when a mutator changed them.</summary>
    private async Task<JsonObject> FinalizeAsync(
        ValidationContext context, IReadOnlyList<FieldDefinition> definitions, string validatedSnapshot, JsonObject mutated, bool enforceRequired,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var json = mutated.ToJsonString();
        if (json == validatedSnapshot)
        {
            return mutated;
        }

        using var document = JsonDocument.Parse(json);
        return await NormalizeAsync(context, definitions, document.RootElement, [], applyDefaults: false, enforceRequired, errors, ct);
    }

    /// <summary>
    /// Validates and normalizes <paramref name="input"/> on top of <paramref name="baseValues"/> (null in the input
    /// removes a value). <c>title</c> is always required; required fields when <paramref name="enforceRequired"/>.
    /// </summary>
    private async Task<JsonObject> NormalizeAsync(
        ValidationContext context, IReadOnlyList<FieldDefinition> definitions, JsonElement? input, JsonObject baseValues, bool applyDefaults,
        bool enforceRequired, Dictionary<string, string[]> errors, CancellationToken ct)
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

                if (fieldTypes.Find(definition.Type) is not { } type)
                {
                    errors[$"fields.{property.Name}"] = [$"The field type '{definition.Type}' is not available."];
                    continue;
                }

                var normalized = await type.NormalizeAsync(property.Value, definition, context, ct);
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
                if (fieldTypes.Find(definition.Type) is { } type
                    && await type.NormalizeAsync(defaultValue.RootElement, definition, context, ct) is { Error: null } normalized)
                {
                    result[definition.Name] = normalized.Value;
                }
            }
        }

        if (!result.ContainsKey("title") && !errors.ContainsKey("fields.title"))
        {
            errors["fields.title"] = ["title is required."];
        }

        foreach (var definition in definitions.Where(d => enforceRequired && d.Required && !result.ContainsKey(d.Name)))
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
        new(schema.List.WorkspaceId, schema.List.Id, schema.List.Name, item.ContentTypeId, item.IsFolder)
        {
            ContentTypeName = schema.FindContentType(item.ContentTypeId)?.Name,
            ContentTypeKey = schema.FindContentType(item.ContentTypeId)?.Key,
            ListTemplate = schema.List.TemplateKey,
        };

    /// <summary>Tells connected clients who can read the item (the principals of its scope, ADR-0035) that it changed.</summary>
    private async Task PublishChangedAsync(string kind, ListCaller caller, ListSchema schema, ListItem item, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = caller.TenantId;
        var scope = item.ScopeId;
        var ct = cancellationToken;
        var audience = await context.AclEntries.AsNoTracking().Where(e => e.TenantId == tenant && e.ScopeId == scope).Select(e => e.PrincipalId).ToListAsync(ct);
        live.Publish(new LiveEvent("item.changed", caller.TenantId, null, new JsonObject
        {
            ["kind"] = kind,
            ["workspaceId"] = schema.List.WorkspaceId,
            ["listId"] = schema.List.Id,
            ["itemId"] = item.Id,
        })
        {
            Audience = audience,
        });
    }

    private enum Change
    {
        Added,
        Updated,
        Deleted,
        Restored,
        Purged,
    }

    private static ItemEvent Event(Change kind, ListCaller caller, ListItem item, Guid workspaceId, IReadOnlyList<string> changed) => kind switch
    {
        Change.Added => new ItemAdded
        {
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Depth = caller.Depth,
            WorkspaceId = workspaceId,
            ListId = item.ListId,
            ItemId = item.Id,
            ContentTypeId = item.ContentTypeId,
            IsFolder = item.IsFolder,
            ChangedFields = changed,
            Title = item.Title,
        },
        Change.Updated => new ItemUpdated
        {
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Depth = caller.Depth,
            WorkspaceId = workspaceId,
            ListId = item.ListId,
            ItemId = item.Id,
            ContentTypeId = item.ContentTypeId,
            IsFolder = item.IsFolder,
            ChangedFields = changed,
            Title = item.Title,
        },
        Change.Deleted => new ItemDeleted
        {
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Depth = caller.Depth,
            WorkspaceId = workspaceId,
            ListId = item.ListId,
            ItemId = item.Id,
            ContentTypeId = item.ContentTypeId,
            IsFolder = item.IsFolder,
            ChangedFields = changed,
            Title = item.Title,
        },
        Change.Restored => new ItemRestored
        {
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Depth = caller.Depth,
            WorkspaceId = workspaceId,
            ListId = item.ListId,
            ItemId = item.Id,
            ContentTypeId = item.ContentTypeId,
            IsFolder = item.IsFolder,
            ChangedFields = changed,
            Title = item.Title,
        },
        _ => new ItemPurged
        {
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Depth = caller.Depth,
            WorkspaceId = workspaceId,
            ListId = item.ListId,
            ItemId = item.Id,
            ContentTypeId = item.ContentTypeId,
            IsFolder = item.IsFolder,
            ChangedFields = changed,
            Title = item.Title,
        },
    };

    /// <summary>
    /// Checks the target folder (null = list root) and that the caller may contribute there; returns the permission
    /// scope items placed there inherit.
    /// </summary>
    private async Task<(ItemWriteResult? Error, Guid ScopeId)> ValidateParentAsync(
        ListCaller caller, ListSchema schema, Guid? parentId, ListItem? moving, CancellationToken ct)
    {
        var listScope = schema.List.Id;
        if (parentId is not { } id)
        {
            return (schema.Access.ListLevel < WorkspaceAccessLevel.Contribute ? ItemWriteResult.Denied : null, listScope);
        }

        var parent = await FindAsync(caller.TenantId, schema.List.Id, id, ct);
        if (parent is not { IsFolder: true } || schema.Access.Level(parent.ScopeId) < WorkspaceAccessLevel.Read)
        {
            return (ItemWriteResult.Invalid("parentId", "The parent must be a folder in the same list."), listScope);
        }

        if (schema.Access.Level(parent.ScopeId) < WorkspaceAccessLevel.Contribute)
        {
            return (ItemWriteResult.Denied, listScope);
        }

        if (moving is { IsFolder: true })
        {
            // A folder cannot move into itself or one of its descendants.
            Guid? current = parent.Id;
            for (var depth = 0; current is not null && depth < MaxFolderDepth; depth++)
            {
                if (current == moving.Id)
                {
                    return (ItemWriteResult.Invalid("parentId", "A folder cannot be moved into itself."), listScope);
                }

                current = (await FindAsync(caller.TenantId, schema.List.Id, current.Value, ct))?.ParentId;
            }
        }

        return (null, parent.ScopeId);
    }

    /// <summary>An item of the list that is not deleted, not tracked.</summary>
    public Task<ListItem?> FindAsync(Guid tenantId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var id = itemId;
        var ct = cancellationToken;
        return context.Items.AsNoTracking().Where(i => i.TenantId == tenant && i.ListId == list && i.Id == id && i.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    /// <summary>Lookups for field validation, within the item's tenant.</summary>
    private sealed class ValidationContext(ListsDbContext db, IUserDirectory users, Guid tenantId) : IFieldValidationContext
    {
        public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken) => users.IsActiveAsync(tenantId, userId, cancellationToken);

        public Task<bool> ItemExistsAsync(Guid listId, Guid itemId, CancellationToken cancellationToken)
        {
            var context = db;
            var tenant = tenantId;
            var list = listId;
            var id = itemId;
            var ct = cancellationToken;
            return context.Items.AnyAsync(i => i.TenantId == tenant && i.ListId == list && i.Id == id && !i.IsFolder && i.DeletedAt == null, ct);
        }

        /// <summary>Terms come with the Taxonomy module; until then no term resolves.</summary>
        public Task<Guid?> ResolveTermAsync(Guid? termSetId, string value, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
    }
}

/// <summary>Distinguishes "not sent" from "sent as null" in PATCH bodies.</summary>
internal readonly record struct Optional<T>(bool HasValue, T Value)
{
    public static Optional<T> None => default;

    public static Optional<T> Of(T value) => new(true, value);
}
