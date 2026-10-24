using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

public sealed record ImportedContentRevision(string Id, string Name, string ContentTypeId, string Payload, long Number,
    DateTime CreatedAt, string CreatedBy, IReadOnlyList<string> ChangedFields, string Keywords, string? BlobId, string Filename);
public sealed record ImportedContentItem(ResourcesRow Resource, IReadOnlyList<ImportedContentRevision> History,
    ImportedContentRevision Head, IReadOnlyList<BlobsRow> Blobs);
public sealed record ImportedCollectionSnapshot(ResourcesRow Resource, EffectiveSchema Schema, IReadOnlyList<ImportedContentItem> Items);
public sealed record ContentImportBatch(ResourcesRow Workspace, IReadOnlyList<ImportedCollectionSnapshot> Collections,
    IReadOnlyList<IdentityUsersRow> Users, IReadOnlyList<IdentityGroupsRow> Groups, IReadOnlyList<IdentityMembershipsRow> Memberships,
    IReadOnlyList<TermGroupsRow> TermGroups, IReadOnlyList<TermSetsRow> TermSets, IReadOnlyList<TermsRow> Terms,
    IReadOnlyList<GrantsRow> Grants, string SourceHash);

public sealed partial class ContentStore
{
    /// <summary>Imports a reviewed source snapshot atomically; existing resources are never overwritten.</summary>
    public Task<ResourcesRow> ImportAsync(string actor, ContentImportBatch batch, Func<CancellationToken, Task<bool>> sourceUnchanged,
        CancellationToken cancellationToken = default) => WriteAsync("", async ct =>
        {
            if (await db.Resources.AnyAsync(x => x.Id == batch.Workspace.Id, ct)) throw new InvalidOperationException("The destination already contains this workspace.");
            await authorization.AuthorizeAsync(actor, batch.Workspace, "manage", ct);
            foreach (var user in batch.Users)
                if (!await db.Set<IdentityUsersRow>().AnyAsync(x => x.Id == user.Id, ct)) db.Set<IdentityUsersRow>().Add(user);
            foreach (var group in batch.Groups)
                if (!await db.Set<IdentityGroupsRow>().AnyAsync(x => x.Id == group.Id, ct)) db.Set<IdentityGroupsRow>().Add(group);
            await db.SaveChangesAsync(ct);
            foreach (var membership in batch.Memberships)
                if (!await db.Set<IdentityMembershipsRow>().AnyAsync(x => x.GroupId == membership.GroupId && x.PrincipalType == membership.PrincipalType && x.PrincipalId == membership.PrincipalId, ct))
                    db.Set<IdentityMembershipsRow>().Add(membership);
            foreach (var group in batch.TermGroups)
                if (!await db.Set<TermGroupsRow>().AnyAsync(x => x.Id == group.Id, ct)) db.Set<TermGroupsRow>().Add(group);
            await db.SaveChangesAsync(ct);
            foreach (var set in batch.TermSets)
                if (!await db.Set<TermSetsRow>().AnyAsync(x => x.Id == set.Id, ct)) db.Set<TermSetsRow>().Add(set);
            await db.SaveChangesAsync(ct);
            foreach (var term in batch.Terms)
                if (!await db.Set<TermsRow>().AnyAsync(x => x.Id == term.Id, ct)) db.Set<TermsRow>().Add(term);
            db.Resources.Add(batch.Workspace);
            await db.SaveChangesAsync(ct);
            foreach (var collection in batch.Collections)
            {
                var container = collection.Resource;
                db.Resources.Add(container);
                await db.SaveChangesAsync(ct);
                var definition = await SaveCatalogAsync(container, collection.Schema, ct);
                var schema = new SchemaRevisionsRow
                {
                    Id = NewId(),
                    CreatedAt = Now,
                    ContainerId = container.Id,
                    RevisionNumber = 1,
                    CreatedBy = actor,
                    Definition = Encode(definition)
                };
                db.SchemaRevisions.Add(schema);
                await db.SaveChangesAsync(ct);
                container.SchemaHeadId = schema.Id;
                db.Resources.AddRange(collection.Items.Select(x => x.Resource));
                await db.SaveChangesAsync(ct);
                db.Set<BlobsRow>().AddRange(collection.Items.SelectMany(x => x.Blobs));
                await db.SaveChangesAsync(ct);
                foreach (var item in collection.Items)
                {
                    foreach (var history in item.History)
                    {
                        var revision = ImportedRevision(item.Resource, history, schema.Id);
                        db.ItemRevisions.Add(revision);
                        db.ItemHistories.Add(new ItemHistoriesRow
                        {
                            Id = NewId(),
                            CreatedAt = history.CreatedAt,
                            ItemId = item.Resource.Id,
                            RevisionId = revision.Id,
                            Number = history.Number,
                            ChangedFields = Encode(history.ChangedFields)
                        });
                    }
                    var head = ImportedRevision(item.Resource, item.Head, schema.Id);
                    db.ItemRevisions.Add(head);
                    await db.SaveChangesAsync(ct);
                    item.Resource.HeadRevisionId = head.Id;
                    item.Resource.PublishedRevisionId = head.Id;
                    item.Resource.NextRevisionNumber = head.RevisionNumber + 1;
                    if (item.Resource.DeletedAt is null)
                    {
                        await ReplaceResourceKeywordsAsync(item.Resource, Decode<string[]>(head.Keywords), ct);
                        await ProjectAsync(item.Resource, head, "published", ct);
                        if (item.Resource.Kind == "item")
                        {
                            db.Publications.Add(new PublicationsRow
                            {
                                Id = NewId(),
                                CreatedAt = head.CreatedAt,
                                ItemId = item.Resource.Id,
                                Version = item.Resource.Version,
                                PublishedBy = head.CreatedBy,
                                RevisionId = head.Id,
                                Snapshot = Encode(new
                                {
                                    id = item.Resource.Id,
                                    name = head.Name,
                                    keywords = Decode<JsonElement>(head.Keywords),
                                    values = Decode<JsonElement>(head.Payload),
                                    revision_id = head.Id,
                                    revision_number = head.RevisionNumber,
                                    schema_revision_id = schema.Id,
                                    filename = head.Filename,
                                    blob_id = head.BlobId
                                })
                            });
                        }
                    }
                }
            }
            db.Set<GrantsRow>().AddRange(batch.Grants);
            await AuditAsync(actor, batch.Workspace, "content.migrate", new
            {
                source_hash = batch.SourceHash,
                collections = batch.Collections.Count,
                items = batch.Collections.Sum(x => x.Items.Count)
            }, ct);
            await db.SaveChangesAsync(ct);
            if (!await sourceUnchanged(ct)) throw new DbUpdateConcurrencyException("The source changed during migration. Retry from a new preview.");
            return batch.Workspace;
        }, cancellationToken);

    private static ItemRevisionsRow ImportedRevision(ResourcesRow item, ImportedContentRevision source, string schemaId) => new()
    {
        Id = source.Id,
        ItemId = item.Id,
        ContainerId = item.ContainerId!,
        SchemaRevisionId = schemaId,
        ContentTypeId = source.ContentTypeId,
        RevisionNumber = source.Number,
        CreatedAt = source.CreatedAt,
        CreatedBy = source.CreatedBy,
        Name = source.Name,
        Payload = source.Payload,
        Keywords = source.Keywords,
        BlobId = source.BlobId,
        Filename = source.Filename,
    };
}
