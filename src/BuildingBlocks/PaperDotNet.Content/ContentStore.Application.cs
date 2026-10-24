using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

public sealed record ContentGrant(string PrincipalType, string PrincipalId, string Action);
public sealed record StoredContentBlob(string ObjectKey, string Filename, string ContentType, long Size, string Sha256);

public sealed partial class ContentStore
{
    public Task<ResourcesRow> SetGrantsAsync(string actor, string resourceId, long expectedVersion, IReadOnlyList<ContentGrant> grants,
        bool inherit, CancellationToken cancellationToken = default) => WriteAsync(resourceId, async ct =>
        {
            var resource = await RequireAsync(actor, resourceId, "manage", ct);
            CheckVersion(resource, expectedVersion);
            if (inherit && resource.Kind == "workspace") throw new ArgumentException("Workspaces cannot inherit permissions.");
            if (grants.Any(x => x.PrincipalType is not ("user" or "group") || string.IsNullOrWhiteSpace(x.PrincipalId)
                || x.Action is not ("read" or "read_draft" or "write" or "publish" or "manage")) || grants.Distinct().Count() != grants.Count)
                throw new ArgumentException("Invalid or duplicate permission grants.", nameof(grants));
            if (!inherit && resource.Kind == "workspace" && grants.All(x => x.Action != "manage"))
                throw new ArgumentException("A workspace needs at least one owner.", nameof(grants));
            db.Set<GrantsRow>().RemoveRange(await db.Set<GrantsRow>().Where(x => x.ResourceId == resource.Id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            if (!inherit)
                db.Set<GrantsRow>().AddRange(grants.Select(x => new GrantsRow
                {
                    Id = NewId(),
                    CreatedAt = Now,
                    ResourceId = resource.Id,
                    PrincipalType = x.PrincipalType,
                    PrincipalId = x.PrincipalId,
                    Action = x.Action
                }));
            resource.InheritPermissions = inherit;
            resource.ScopeId = inherit
                ? (await db.Resources.SingleAsync(x => x.Id == resource.ParentId, ct)).ScopeId ?? resource.ParentId
                : resource.Id;
            resource.Version++;
            var frontier = new[] { resource.Id };
            while (frontier.Length > 0)
            {
                var children = await db.Resources.Where(x => x.ParentId != null && frontier.Contains(x.ParentId) && x.InheritPermissions).ToListAsync(ct);
                foreach (var child in children) child.ScopeId = resource.ScopeId;
                frontier = children.Select(x => x.Id).ToArray();
            }
            await AuditAsync(actor, resource, "permissions.update", new { inherit }, ct);
            return resource;
        }, cancellationToken);

    /// <summary>Records uploaded bytes and their new content revision in one organization transaction.</summary>
    public Task<ResourcesRow> AttachBlobAsync(string actor, string itemId, long expectedVersion, StoredContentBlob blob,
        CancellationToken cancellationToken = default) => WriteAsync(itemId, async ct =>
        {
            var item = await RequireAsync(actor, itemId, "write", ct);
            CheckVersion(item, expectedVersion);
            var collection = await db.Resources.SingleAsync(x => x.Id == item.ContainerId, ct);
            if (item.Kind != "item" || collection.Kind != "library") throw new ArgumentException("Files belong to library items.");
            if (blob.ContentType is not ("application/pdf" or "image/tiff" or "image/jpeg" or "image/png" or "image/webp")
                || blob.Size < 0 || blob.Sha256.Length != 64) throw new ArgumentException("Invalid file metadata.", nameof(blob));
            var number = (await db.Set<BlobsRow>().Where(x => x.ItemId == item.Id).MaxAsync(x => (long?)x.Version, ct) ?? 0) + 1;
            var saved = new BlobsRow
            {
                Id = NewId(),
                CreatedAt = Now,
                ItemId = item.Id,
                Version = number,
                ObjectKey = blob.ObjectKey,
                Filename = blob.Filename,
                ContentType = blob.ContentType,
                Size = blob.Size,
                Sha256 = blob.Sha256
            };
            db.Set<BlobsRow>().Add(saved);
            await db.SaveChangesAsync(ct);
            var previous = await db.ItemRevisions.SingleAsync(x => x.Id == item.HeadRevisionId, ct);
            item.Version++;
            await ReviseAsync(actor, item, collection, new ContentInput(item.Name, previous.ContentTypeId,
                Decode<JsonElement>(previous.Payload), Decode<string[]>(previous.Keywords), saved.Id), previous, ct);
            await AuditAsync(actor, item, "item.content", new { blob_id = saved.Id }, ct);
            return item;
        }, cancellationToken);
}
