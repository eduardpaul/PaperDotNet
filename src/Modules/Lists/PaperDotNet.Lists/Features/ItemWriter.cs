using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Taxonomy.Contracts;
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
/// All item writes go through here: field validation and normalization, folders,
/// content types, item mutators (ADR-0023) and integration events
/// published through the transactional outbox.
/// </summary>
internal sealed class ItemWriter(
    ListsDbContext db,
    FieldTypeRegistry fieldTypes,
    IUserDirectory users,
    IEnumerable<IItemMutator> mutators,
    IOutbox outbox,
    ITermStore terms,
    ITenantContext tenant,
    ICurrentUser currentUser,
    EventCausation causation,
    TimeProvider time) : IFieldValidationContext
{
    public const int TitleMaxLength = 1024;
    private const int MaxFolderDepth = 64;

    public Task<ItemWriteResult> CreateAsync(
        ListSchema schema, Guid? contentTypeId, Guid? parentId, bool isFolder, JsonElement? fields, CancellationToken ct) =>
        CreateAsync(schema, contentTypeId, parentId, isFolder, fields, Ids.New(), ct);

    /// <summary>
    /// Creates the item with the given <paramref name="itemId"/>. With <paramref name="uniqueGrants"/> (imports) it starts
    /// with its own permissions, saved together with the item so it is never visible with inherited ones.
    /// </summary>
    public async Task<ItemWriteResult> CreateAsync(
        ListSchema schema, Guid? contentTypeId, Guid? parentId, bool isFolder, JsonElement? fields, Guid itemId, CancellationToken ct,
        IReadOnlyList<PermissionGrantDto>? uniqueGrants = null)
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

        var (parentError, scopeId) = await ValidateParentAsync(schema, parentId, null, ct);
        if (parentError is not null)
        {
            return parentError;
        }

        // Folders hold values of their content type's fields too (LST-19), but required fields and defaults are for items.
        var definitions = contentType.Fields;
        var errors = new Dictionary<string, string[]>();
        var values = await NormalizeAsync(definitions, fields, [], applyDefaults: !isFolder, enforceRequired: !isFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        var item = new ListItem
        {
            Id = itemId,
            ListId = schema.List.Id,
            ContentTypeId = contentType.Id,
            ParentId = parentId,
            IsFolder = isFolder,
            Title = string.Empty,
            ScopeId = scopeId,
        };

        var scope = Scope(schema, item);
        var snapshot = values.ToJsonString();
        var context = new ItemMutationContext { Kind = ItemEventKind.Adding, Scope = scope, ItemId = item.Id, UserId = currentUser.UserId, After = values };
        if (await MutateAsync(context, ct) is { } cancelled)
        {
            return cancelled;
        }

        values = await FinalizeAsync(definitions, snapshot, context.After!, !isFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        ApplyValues(item, values);
        if (uniqueGrants is not null)
        {
            item.HasUniquePermissions = true;
            item.ScopeId = item.Id;
            db.Grants.AddRange(uniqueGrants.DistinctBy(g => (g.PrincipalType, g.PrincipalId)).Select(g => new PermissionGrant
            {
                Id = Ids.New(),
                ListId = schema.List.Id,
                ObjectId = item.Id,
                PrincipalType = g.PrincipalType,
                PrincipalId = g.PrincipalId,
                Level = g.Level,
            }));
        }

        db.Items.Add(item);
        var changed = Values(item).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        await AddVersionAsync(schema, item, changed, ct);
        await outbox.SaveChangesAsync(db, [Event(ItemEventKind.Adding, item, schema, changed)], cancellationToken: ct);
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

        Guid? newScopeId = item.ScopeId;
        if (parentId.HasValue)
        {
            (var parentError, var parentScope) = await ValidateParentAsync(schema, parentId.Value, item, ct);
            if (parentError is not null)
            {
                return parentError;
            }

            newScopeId = item.HasUniquePermissions ? item.ScopeId : parentScope;
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
        var values = await NormalizeAsync(definitions, fields, current, applyDefaults: false, enforceRequired: !item.IsFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        var scope = Scope(schema, item);
        var snapshot = values.ToJsonString();
        var context = new ItemMutationContext
        {
            Kind = ItemEventKind.Updating,
            Scope = scope,
            ItemId = item.Id,
            UserId = currentUser.UserId,
            Before = (JsonObject)before.DeepClone(),
            After = values,
        };
        if (await MutateAsync(context, ct) is { } cancelled)
        {
            return cancelled;
        }

        values = await FinalizeAsync(definitions, snapshot, context.After!, !item.IsFolder, errors, ct);
        if (errors.Count > 0)
        {
            return new ItemWriteResult(null, errors);
        }

        var oldScopeId = item.ScopeId;
        if (parentId.HasValue)
        {
            item.ParentId = parentId.Value;
            item.ScopeId = newScopeId;
        }

        var contentTypeChanged = contentType.Id != item.ContentTypeId;
        item.ContentTypeId = contentType.Id;
        ApplyValues(item, values);
        var after = Values(item);
        var changed = ChangedFields(before, after);
        if (changed.Count > 0 || contentTypeChanged)
        {
            await AddVersionAsync(schema, item, changed, ct);
        }
        var scopeMoved = item.IsFolder && oldScopeId != item.ScopeId;
        await outbox.SaveChangesAsync(
            db, [Event(ItemEventKind.Updating, item, schema, changed)], scopeMoved ? [ScopeChange(schema, item, oldScopeId)] : null, ct);
        if (scopeMoved)
        {
            await ScopeTree.ReassignAsync(db, item.Id, oldScopeId, item.ScopeId, ct);
            await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, currentUser, schema.List.Id)], cancellationToken: ct);
        }

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
        var context = new ItemMutationContext { Kind = ItemEventKind.Deleting, Scope = scope, ItemId = item.Id, UserId = currentUser.UserId, Before = before };
        if (await MutateAsync(context, ct) is { } cancelled)
        {
            return cancelled;
        }

        db.Items.Remove(item);
        await outbox.SaveChangesAsync(db, [Event(ItemEventKind.Deleting, item, schema, [])], cancellationToken: ct);
        return new ItemWriteResult(item);
    }

    /// <summary>
    /// Restores an item from the recycle bin. It returns to its folder when that
    /// folder is active, otherwise to the list root.
    /// </summary>
    public async Task RestoreAsync(ListSchema schema, ListItem item, CancellationToken ct)
    {
        var oldScopeId = item.ScopeId;
        if (item.ParentId is { } parentId && !await db.Items.AnyAsync(i => i.Id == parentId && i.IsFolder, ct))
        {
            item.ParentId = null;
            if (!item.HasUniquePermissions)
            {
                item.ScopeId = null;
            }
        }

        item.DeletedAt = null;
        item.DeletedBy = null;
        var restored = new ItemRestored
        {
            TenantId = tenant.TenantId!.Value,
            TenantIdentifier = tenant.TenantIdentifier!,
            UserId = currentUser.UserId,
            Depth = causation.Depth,
            WorkspaceId = schema.List.WorkspaceId,
            ListId = schema.List.Id,
            ItemId = item.Id,
            ContentTypeId = item.ContentTypeId,
            IsFolder = item.IsFolder,
        };
        var scopeMoved = item.IsFolder && oldScopeId != item.ScopeId;
        await outbox.SaveChangesAsync(db, [restored], scopeMoved ? [ScopeChange(schema, item, oldScopeId)] : null, ct);
        if (scopeMoved)
        {
            await ScopeTree.ReassignAsync(db, item.Id, oldScopeId, item.ScopeId, ct);
            await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, currentUser, schema.List.Id)], cancellationToken: ct);
        }
    }

    /// <summary>Deletes recycle-bin items and their versions permanently.</summary>
    public async Task PurgeAsync(IReadOnlyCollection<ListItem> deletedItems, CancellationToken ct)
    {
        var ids = deletedItems.Select(i => i.Id).ToList();
        var listIds = deletedItems.Select(i => i.ListId).Distinct().ToList();
        var workspaces = await db.Lists.IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(l => listIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.WorkspaceId, ct);
        db.ItemVersions.RemoveRange(await db.ItemVersions.Where(v => ids.Contains(v.ItemId)).ToListAsync(ct));
        db.Items.RemoveRange(deletedItems);
        var purged = deletedItems.Select(item => (IntegrationEvent)new ItemPurged
        {
            TenantId = tenant.TenantId!.Value,
            TenantIdentifier = tenant.TenantIdentifier!,
            UserId = currentUser.UserId,
            WorkspaceId = workspaces.GetValueOrDefault(item.ListId),
            ListId = item.ListId,
            ItemId = item.Id,
            ContentTypeId = item.ContentTypeId,
            IsFolder = item.IsFolder,
        }).ToList();
        await outbox.SaveChangesAsync(db, purged, cancellationToken: ct);
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
    /// <summary>
    /// Saved with a folder whose permission scope changed: if the request stops before the items inside are reassigned
    /// (below, right after the save), the message completes it.
    /// </summary>
    private CompleteFolderScopeChange ScopeChange(ListSchema schema, ListItem folder, Guid? oldScopeId) =>
        new(schema.List.Id, folder.Id, oldScopeId, folder.ScopeId, tenant.TenantId!.Value, tenant.TenantIdentifier!, currentUser.UserId);

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
        IReadOnlyList<FieldDefinition> definitions, string validatedSnapshot, JsonObject mutated, bool enforceRequired,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var json = mutated.ToJsonString();
        if (json == validatedSnapshot)
        {
            return mutated;
        }

        using var document = JsonDocument.Parse(json);
        return await NormalizeAsync(definitions, document.RootElement, [], applyDefaults: false, enforceRequired, errors, ct);
    }

    /// <summary>
    /// Validates and normalizes <paramref name="input"/> on top of <paramref name="baseValues"/>
    /// (null in the input removes a value). <c>title</c> is always required; required fields when <paramref name="enforceRequired"/>.
    /// </summary>
    private async Task<JsonObject> NormalizeAsync(
        IReadOnlyList<FieldDefinition> definitions, JsonElement? input, JsonObject baseValues, bool applyDefaults, bool enforceRequired,
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

        foreach (var definition in definitions.Where(d => enforceRequired && d.Required && !result.ContainsKey(d.Name)))
        {
            errors.TryAdd($"fields.{definition.Name}", ["This field is required."]);
        }

        return result;
    }

    /// <summary>Snapshots the item as a new version when the list keeps history, trimming old versions.</summary>
    private async Task AddVersionAsync(ListSchema schema, ListItem item, IReadOnlyList<string> changed, CancellationToken ct)
    {
        if (schema.List.Versioning == ListVersioning.Off || item.IsFolder)
        {
            return;
        }

        var last = await db.ItemVersions.Where(v => v.ItemId == item.Id).MaxAsync(v => (int?)v.Number, ct) ?? 0;
        db.ItemVersions.Add(new ItemVersion
        {
            Id = Ids.New(),
            ItemId = item.Id,
            ListId = item.ListId,
            Number = last + 1,
            ContentTypeId = item.ContentTypeId,
            Title = item.Title,
            Fields = item.Fields,
            ChangedFields = [.. changed],
            CreatedAt = time.GetUtcNow(),
            CreatedBy = currentUser.UserId,
        });

        var keepFrom = last + 2 - schema.List.MaxVersions;
        if (keepFrom > 1)
        {
            db.ItemVersions.RemoveRange(await db.ItemVersions.Where(v => v.ItemId == item.Id && v.Number < keepFrom).ToListAsync(ct));
        }
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
                Depth = causation.Depth,
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
                Depth = causation.Depth,
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
                Depth = causation.Depth,
                WorkspaceId = schema.List.WorkspaceId,
                ListId = schema.List.Id,
                ItemId = item.Id,
                ContentTypeId = item.ContentTypeId,
                IsFolder = item.IsFolder,
                ChangedFields = changed,
            },
        };
    }

    /// <summary>
    /// Checks the target folder (null = list root) and that the user may contribute
    /// there; returns the security scope items placed there inherit.
    /// </summary>
    private async Task<(ItemWriteResult? Error, Guid? ScopeId)> ValidateParentAsync(ListSchema schema, Guid? parentId, ListItem? moving, CancellationToken ct)
    {
        if (parentId is not { } id)
        {
            return (schema.Access.Level(null) < WorkspaceAccessLevel.Contribute ? ItemWriteResult.Denied : null, null);
        }

        var parent = await db.Items.AsNoTracking()
            .Where(i => i.Id == id && i.ListId == schema.List.Id && i.IsFolder)
            .Select(i => new { i.Id, i.ParentId, i.ScopeId })
            .FirstOrDefaultAsync(ct);
        if (parent is null || schema.Access.Level(parent.ScopeId) < WorkspaceAccessLevel.Read)
        {
            return (ItemWriteResult.Invalid("parentId", "The parent must be a folder in the same list."), null);
        }

        if (schema.Access.Level(parent.ScopeId) < WorkspaceAccessLevel.Contribute)
        {
            return (ItemWriteResult.Denied, null);
        }

        if (moving is { IsFolder: true })
        {
            // A folder cannot move into itself or one of its descendants.
            Guid? current = parent.Id;
            for (var depth = 0; current is not null && depth < MaxFolderDepth; depth++)
            {
                if (current == moving.Id)
                {
                    return (ItemWriteResult.Invalid("parentId", "A folder cannot be moved into itself."), null);
                }

                var next = current;
                current = await db.Items.AsNoTracking().Where(i => i.Id == next).Select(i => i.ParentId).FirstOrDefaultAsync(ct);
            }
        }

        return (null, parent.ScopeId);
    }

}

/// <summary>Distinguishes "not sent" from "sent as null" in PATCH bodies.</summary>
internal readonly record struct Optional<T>(bool HasValue, T Value)
{
    public static Optional<T> None => default;

    public static Optional<T> Of(T value) => new(true, value);
}
