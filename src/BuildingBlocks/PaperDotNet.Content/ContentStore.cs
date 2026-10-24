using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

/// <summary>Transactional content lifecycle over PaperGo's organization database. One context per operation scope.</summary>
public sealed partial class ContentStore(ContentDbContext db, IContentAuthorization authorization, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private DateTime Now => db.NormalizeTimestamp(clock.GetUtcNow().UtcDateTime);
    private static string NewId() => Guid.CreateVersion7().ToString();
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidOperationException("Invalid stored JSON.");

    public Task<ResourcesRow> CreateWorkspaceAsync(string actor, string name, CancellationToken cancellationToken = default) =>
        WriteAsync("", async ct =>
        {
            ValidateName(name);
            var id = NewId();
            var resource = new ResourcesRow
            {
                Id = id,
                WorkspaceId = id,
                ScopeId = id,
                Kind = "workspace",
                Name = name,
                InheritPermissions = false,
                CreatedAt = Now,
                UpdatedAt = Now,
                CreatedBy = actor,
                UpdatedBy = actor
            };
            await authorization.AuthorizeAsync(actor, resource, "manage", ct);
            db.Resources.Add(resource);
            db.Set<GrantsRow>().Add(new GrantsRow
            {
                Id = NewId(),
                CreatedAt = Now,
                ResourceId = resource.Id,
                PrincipalType = "user",
                PrincipalId = actor,
                Action = "manage"
            });
            return resource;
        }, cancellationToken);

    public Task<ResourcesRow> CreateCollectionAsync(string actor, string workspaceId, string name, bool library = false,
        bool publishingEnabled = false, string versioning = "off", long maxVersions = 50, CancellationToken cancellationToken = default) =>
        WriteAsync(workspaceId, ct => CreateCollectionInTransactionAsync(actor, workspaceId, name, library, publishingEnabled, versioning, maxVersions, ct), cancellationToken);

    /// <summary>Creates the collection and its first effective schema in one transaction.</summary>
    public Task<ResourcesRow> CreateCollectionAsync(string actor, string workspaceId, string name, EffectiveSchema initialSchema,
        bool library = false, bool publishingEnabled = false, CancellationToken cancellationToken = default) =>
        WriteAsync(workspaceId, async ct =>
        {
            var collection = await CreateCollectionInTransactionAsync(actor, workspaceId, name, library, publishingEnabled, "off", 50, ct);
            await db.SaveChangesAsync(ct);
            var definition = await SaveCatalogAsync(collection, initialSchema, ct);
            var revision = new SchemaRevisionsRow
            {
                Id = NewId(),
                ContainerId = collection.Id,
                CreatedAt = Now,
                CreatedBy = actor,
                RevisionNumber = 1,
                Definition = Encode(definition)
            };
            db.SchemaRevisions.Add(revision);
            await db.SaveChangesAsync(ct);
            collection.SchemaHeadId = revision.Id;
            return collection;
        }, cancellationToken);

    private async Task<ResourcesRow> CreateCollectionInTransactionAsync(string actor, string workspaceId, string name, bool library,
        bool publishingEnabled, string versioning, long maxVersions, CancellationToken ct)
    {
        ValidateName(name);
        ValidateHistory(versioning, maxVersions);
        var workspace = await RequireAsync(actor, workspaceId, "manage", ct);
        if (workspace.Kind != "workspace") throw new InvalidOperationException("Collections require a workspace.");
        var resource = new ResourcesRow
        {
            Id = NewId(),
            WorkspaceId = workspace.Id,
            ParentId = workspace.Id,
            ScopeId = workspace.ScopeId ?? workspace.Id,
            Kind = library ? "library" : "list",
            Name = name,
            PublishingEnabled = publishingEnabled,
            Versioning = versioning,
            MaxVersions = maxVersions,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedBy = actor,
            UpdatedBy = actor
        };
        db.Resources.Add(resource);
        return resource;
    }

    public Task<SchemaRevisionsRow> RecordSchemaAsync(string actor, string collectionId, long expectedVersion,
        EffectiveSchema definition, CancellationToken cancellationToken = default) =>
        WriteAsync(collectionId, async ct =>
        {
            var collection = await RequireAsync(actor, collectionId, "manage", ct);
            CheckVersion(collection, expectedVersion);
            RequireCollection(collection);
            definition = await SaveCatalogAsync(collection, definition, ct);
            var number = (await db.SchemaRevisions.Where(x => x.ContainerId == collection.Id)
                .MaxAsync(x => (long?)x.RevisionNumber, ct) ?? 0) + 1;
            var revision = new SchemaRevisionsRow
            {
                Id = NewId(),
                CreatedAt = Now,
                ContainerId = collection.Id,
                RevisionNumber = number,
                Definition = Encode(definition),
                CreatedBy = actor
            };
            db.SchemaRevisions.Add(revision);
            await db.SaveChangesAsync(ct);
            if (collection.SchemaHeadId is not null) collection.Version++;
            collection.SchemaHeadId = revision.Id;
            collection.UpdatedAt = Now;
            collection.UpdatedBy = actor;
            await db.SaveChangesAsync(ct);
            var projections = await db.ItemSurfaces.Where(x => x.ContainerId == collection.Id).ToListAsync(ct);
            foreach (var projection in projections)
            {
                var item = await db.Resources.SingleAsync(x => x.Id == projection.ItemId, ct);
                if (item.DeletedAt is not null) continue;
                var content = await db.ItemRevisions.SingleAsync(x => x.Id == projection.RevisionId, ct);
                await ProjectAsync(item, content, projection.Surface, ct);
            }
            return revision;
        }, cancellationToken);

    public Task<ResourcesRow> CreateItemAsync(string actor, string parentId, ContentInput input, bool folder = false,
        CancellationToken cancellationToken = default) => WriteAsync(parentId, async ct =>
        {
            var parent = await RequireAsync(actor, parentId, "write", ct);
            var collection = parent.Kind is "list" or "library" ? parent : await db.Resources.SingleAsync(x => x.Id == parent.ContainerId, ct);
            RequireCollection(collection);
            if (parent.Kind is not ("list" or "library" or "folder") || parent.WorkspaceId != collection.WorkspaceId || collection.DeletedAt is not null)
                throw new InvalidOperationException("Invalid item parent.");
            if (folder && !collection.AllowFolders) throw new InvalidOperationException("Folders are disabled.");
            var item = new ResourcesRow
            {
                Id = NewId(),
                WorkspaceId = collection.WorkspaceId,
                ParentId = parent.Id,
                ContainerId = collection.Id,
                ScopeId = parent.ScopeId ?? parent.Id,
                Kind = folder ? "folder" : "item",
                Name = input.Name,
                ContentTypeId = input.ContentTypeId,
                CreatedAt = Now,
                UpdatedAt = Now,
                CreatedBy = actor,
                UpdatedBy = actor
            };
            db.Resources.Add(item);
            await db.SaveChangesAsync(ct);
            await ReviseAsync(actor, item, collection, input, null, ct);
            await AuditAsync(actor, item, item.Kind + ".create", new { revision_id = item.HeadRevisionId }, ct);
            return item;
        }, cancellationToken);

    public Task<ResourcesRow> EditAsync(string actor, string itemId, long expectedVersion, ContentInput input,
        CancellationToken cancellationToken = default) => WriteAsync(itemId, async ct =>
        {
            var item = await RequireAsync(actor, itemId, "write", ct);
            CheckVersion(item, expectedVersion);
            RequireContent(item);
            var collection = await db.Resources.SingleAsync(x => x.Id == item.ContainerId, ct);
            var previous = await db.ItemRevisions.SingleAsync(x => x.Id == item.HeadRevisionId, ct);
            item.Version++;
            await ReviseAsync(actor, item, collection, input, previous, ct);
            await AuditAsync(actor, item, item.Kind + ".update", new { revision_id = item.HeadRevisionId }, ct);
            return item;
        }, cancellationToken);

    private async Task ReviseAsync(string actor, ResourcesRow item, ResourcesRow collection, ContentInput input,
        ItemRevisionsRow? previous, CancellationToken ct)
    {
        ValidateName(input.Name);
        var schema = await db.SchemaRevisions.SingleOrDefaultAsync(x => x.Id == collection.SchemaHeadId, ct)
            ?? throw new InvalidOperationException("The collection needs a schema revision.");
        var definition = Decode<EffectiveSchema>(schema.Definition);
        var payload = await ValidateValuesAsync(definition, input, ct, previous, item.Kind == "folder");
        var keywords = (input.Keywords ?? []).ToArray();
        await ValidateKeywordsAsync(keywords, ct);
        var blobId = input.BlobId ?? previous?.BlobId;
        if (item.Kind == "folder" && blobId is not null) throw new InvalidOperationException("Folders cannot have a blob.");
        var blob = blobId is null ? null : await db.Set<BlobsRow>().SingleOrDefaultAsync(x => x.Id == blobId && x.ItemId == item.Id && x.Availability == "available", ct)
            ?? throw new InvalidOperationException("Blob must be available and belong to this item.");
        var filename = "";
        if (blob is not null)
        {
            filename = input.BlobId is not null ? blob.Filename : previous?.Filename ?? blob.Filename;
            if (input.BlobId is null && previous is not null && input.Name != previous.Name)
            {
                filename = input.Name;
                if (Path.GetExtension(filename).Length == 0) filename += Path.GetExtension(previous.Filename);
            }
            if (filename is "." or ".." || filename.Contains('/') || filename.Contains('\\') || filename.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(filename) || filename.Length > 1024)
                throw new ArgumentException("Invalid attachment filename.", nameof(input));
        }
        var revision = new ItemRevisionsRow
        {
            Id = NewId(),
            CreatedAt = Now,
            ItemId = item.Id,
            ContainerId = collection.Id,
            SchemaRevisionId = schema.Id,
            ContentTypeId = input.ContentTypeId,
            RevisionNumber = item.NextRevisionNumber,
            Name = input.Name,
            Keywords = Encode(keywords),
            Payload = payload,
            CreatedBy = actor,
            BlobId = blobId,
            Filename = filename
        };
        db.ItemRevisions.Add(revision);
        await db.SaveChangesAsync(ct);
        item.HeadRevisionId = revision.Id;
        item.NextRevisionNumber++;
        item.Name = revision.Name;
        item.Keywords = revision.Keywords;
        item.Values = revision.Payload;
        item.ContentTypeId = revision.ContentTypeId;
        item.UpdatedAt = revision.CreatedAt;
        item.UpdatedBy = actor;
        item.NameKey = collection.Kind == "library" ? input.Name.ToLowerInvariant() : null;
        await ReplaceResourceKeywordsAsync(item, keywords, ct);
        if (collection.PublishingEnabled) await ProjectAsync(item, revision, "head", ct);
        if (item.Kind == "folder")
        {
            await ProjectAsync(item, revision, "published", ct);
            item.PublishedRevisionId = revision.Id;
        }
        await SignalAsync(actor, item, $"{(item.Kind == "folder" ? "folder" : "item")}.{(previous is null ? "created" : "updated")}",
            new { revision_id = revision.Id, revision_number = revision.RevisionNumber, content_type_id = revision.ContentTypeId, blob_id = revision.BlobId }, true, ct);
        if (item.Kind == "item")
        {
            if (!collection.PublishingEnabled) await PublishRevisionAsync(actor, item, revision, false, ct);
            if (collection.Versioning == "major") await RetainHistoryAsync(item, collection, revision, previous, ct);
        }
    }

    public async Task<ContentView> ReadAsync(string actor, string itemId, string surface = "published", CancellationToken cancellationToken = default)
    {
        ValidateSurface(surface);
        var item = await RequireAsync(actor, itemId, "read", cancellationToken);
        RequireContent(item);
        var collection = await db.Resources.AsNoTracking().SingleAsync(x => x.Id == item.ContainerId, cancellationToken);
        if (surface == "head" && collection.PublishingEnabled)
            await authorization.AuthorizeAsync(actor, item, "read_draft", cancellationToken);
        var revisionId = surface == "head" ? item.HeadRevisionId : item.PublishedRevisionId;
        if (revisionId is null) throw new KeyNotFoundException("Item has no revision on this surface.");
        var revision = await db.ItemRevisions.AsNoTracking().SingleAsync(x => x.Id == revisionId, cancellationToken);
        var schema = await db.SchemaRevisions.AsNoTracking().SingleAsync(x => x.Id == revision.SchemaRevisionId, cancellationToken);
        // Return the chosen snapshot only; a published reader must never receive mutable head data or pointers.
        var visible = Decode<ResourcesRow>(Encode(item));
        visible.Name = revision.Name;
        visible.Values = revision.Payload;
        visible.Keywords = revision.Keywords;
        visible.ContentTypeId = revision.ContentTypeId;
        visible.UpdatedAt = revision.CreatedAt;
        visible.UpdatedBy = revision.CreatedBy;
        if (surface == "published") { visible.HeadRevisionId = null; visible.NextRevisionNumber = 1; }
        return new ContentView(visible, revision, schema);
    }

    public async Task<IReadOnlyList<ContentView>> BrowseAsync(string actor, string collectionId, string surface = "published",
        ArchiveFilter archive = ArchiveFilter.Active, int limit = 100, string? afterId = null, string? search = null, string? parentId = null, CancellationToken cancellationToken = default)
    {
        ValidateSurface(surface);
        if (!Enum.IsDefined(archive) || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var collection = await RequireAsync(actor, collectionId, "read", cancellationToken);
        RequireCollection(collection);
        if (surface == "head" && collection.PublishingEnabled)
            await authorization.AuthorizeAsync(actor, collection, "read_draft", cancellationToken);
        var query = db.Resources.AsNoTracking().Where(x => x.ContainerId == collectionId && x.DeletedAt == null);
        if (parentId is not null) query = query.Where(x => x.ParentId == parentId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var folded = search.ToLowerInvariant();
            var physicalSurface = collection.PublishingEnabled ? surface : "published";
            query = query.Where(x => db.ItemSurfaces.Any(s => s.ItemId == x.Id && s.Surface == physicalSurface
                && (s.FoldedName.Contains(folded) || s.Payload.Contains(search))));
        }
        if (archive == ArchiveFilter.Active) query = query.Where(x => x.ArchivedAt == null);
        if (archive == ArchiveFilter.Archived) query = query.Where(x => x.ArchivedAt != null);
        query = surface == "head" ? query.Where(x => x.HeadRevisionId != null) : query.Where(x => x.PublishedRevisionId != null);
        var result = new List<ContentView>();
        var cursor = afterId;
        while (result.Count < limit)
        {
            // Database collation supplies the cursor order, as it does for PaperGo.
#pragma warning disable CA1309
            var pageQuery = cursor is null ? query : query.Where(x => string.Compare(x.Id, cursor) > 0);
#pragma warning restore CA1309
            var page = await pageQuery.OrderBy(x => x.Id).Take(100).ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            foreach (var row in page)
            {
                try { result.Add(await ReadAsync(actor, row.Id, surface, cancellationToken)); }
                catch (UnauthorizedAccessException) { continue; }
                if (result.Count == limit) break;
            }
            cursor = page[^1].Id;
        }
        return result;
    }

    private async Task<T> WriteAsync<T>(string resourceId, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("ContentStore owns its transaction.");
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Save or discard pending changes before a lifecycle command.");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.LockWorkspaceAsync(resourceId, ct);
            var result = await operation(ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ResourcesRow> RequireAsync(string actor, string id, string action, CancellationToken ct)
    {
        var resource = await db.Resources.SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct)
            ?? throw new KeyNotFoundException("Resource does not exist.");
        await authorization.AuthorizeAsync(actor, resource, action, ct);
        return resource;
    }

    private static void CheckVersion(ResourcesRow resource, long version)
    {
        if (version < 1 || resource.Version != version) throw new DbUpdateConcurrencyException("Resource version has changed.");
    }

    private static void RequireCollection(ResourcesRow resource)
    {
        if (resource.Kind is not ("list" or "library")) throw new InvalidOperationException("A collection is required.");
    }

    private static void RequireContent(ResourcesRow resource)
    {
        if (resource.Kind is not ("item" or "folder")) throw new InvalidOperationException("An item or folder is required.");
    }

    private static void ValidateSurface(string surface)
    {
        if (surface is not ("head" or "published")) throw new ArgumentException("Surface must be head or published.", nameof(surface));
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024) throw new ArgumentException("Name must contain 1 to 1024 UTF-16 units.", nameof(name));
    }

    private static void ValidateHistory(string mode, long maxVersions)
    {
        if (mode is not ("off" or "major") || maxVersions is < 1 or > 500) throw new ArgumentException("History must be off or major, retaining 1 to 500 versions.", nameof(mode));
    }
}
