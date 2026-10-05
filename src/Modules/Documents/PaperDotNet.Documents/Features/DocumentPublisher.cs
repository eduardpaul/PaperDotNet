using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

internal sealed class DocumentPublisher(DocumentsDbContext db, IListItemStore items, IBlobStore blobs,
    DocumentEvents events, ITenantContext tenant, ICurrentUser user, ILiveEvents live) : IDocumentPublisher
{
    public Task LockVersionsAsync(IReadOnlyList<DocumentFile> versions, DbTransaction transaction, CancellationToken ct) =>
        SharedTransaction.RunAsync(db, transaction, async token =>
        {
            foreach (var file in versions.OrderBy(f => f.ItemId))
            {
                var item = await items.GetAsync(file.WorkspaceId, file.ListId, file.ItemId, token);
                if (item is null || item.IsFolder || item.Access < WorkspaceAccessLevel.Contribute
                    || await db.FileVersions.Where(v => v.Id == file.Id && v.ItemId == file.ItemId && v.ListId == file.ListId && v.WorkspaceId == file.WorkspaceId && v.IsCurrent)
                        .ExecuteUpdateAsync(u => u.SetProperty(v => v.IsCurrent, v => v.IsCurrent), token) != 1)
                    throw new InvalidOperationException("A document changed or you may no longer change it.");
            }
        }, ct);

    public Task PublishAsync(Guid stagedId, DocumentFile expected, string source, DbTransaction transaction, CancellationToken ct) =>
        SharedTransaction.RunAsync(db, transaction, async token =>
        {
            if (await db.StagedFiles.Where(s => s.Id == stagedId && s.CreatedBy == user.UserId && s.State == "ready")
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, s => s.State), token) != 1)
                throw new InvalidOperationException("Temporary document is unavailable.");
            // Caller-owned transactions can roll back after SaveChanges. Always read persisted state on retry.
            db.ChangeTracker.Clear();
            var record = await db.StagedFiles.SingleOrDefaultAsync(s => s.Id == stagedId && s.CreatedBy == user.UserId, token)
                ?? throw new InvalidOperationException("Temporary document not found.");
            if (record.PublishedVersionId is { } published)
            {
                if (!await db.FileVersions.AnyAsync(v => v.Id == published && v.ItemId == expected.ItemId, token))
                    throw new InvalidOperationException("Temporary document was published to another item.");
                return;
            }
            var item = await items.GetAsync(expected.WorkspaceId, expected.ListId, expected.ItemId, token);
            if (item is null || item.IsFolder || item.Access < WorkspaceAccessLevel.Contribute || record.State != "ready"
                || string.IsNullOrWhiteSpace(source) || source.Length > 20)
                throw new InvalidOperationException("You may not publish this temporary document.");
            var stored = await db.StoredFiles.SingleAsync(s => s.Id == record.StoredFileId, token);
            if (!await blobs.ExistsAsync(stored.BlobKey, token)) throw new InvalidOperationException("Temporary content is missing.");
            if (await db.FileVersions.Where(v => v.Id == expected.Id && v.ItemId == expected.ItemId && v.ListId == expected.ListId && v.WorkspaceId == expected.WorkspaceId && v.IsCurrent)
                .ExecuteUpdateAsync(u => u.SetProperty(v => v.IsCurrent, false), token) != 1)
                throw new InvalidOperationException("The target document changed.");
            var number = await db.FileVersions.Where(v => v.ItemId == expected.ItemId).MaxAsync(v => v.Number, token) + 1;
            db.FileVersions.Add(new FileVersion { Id = stagedId, WorkspaceId = expected.WorkspaceId, ListId = expected.ListId, ItemId = expected.ItemId,
                Number = number, IsCurrent = true, StoredFileId = stored.Id, Sha256 = stored.Sha256, Size = stored.Size, MediaType = stored.MediaType,
                FileName = record.FileName, Source = source, PageCount = record.PageCount, Languages = record.Languages,
                TextLanguage = record.Languages?.Split('+')[0] });
            record.PublishedVersionId = stagedId;
            await db.SaveChangesAsync(token);
        }, ct);

    public async Task AnnounceAsync(Guid stagedId, CancellationToken ct)
    {
        var id = await db.StagedFiles.Where(s => s.Id == stagedId && s.CreatedBy == user.UserId).Select(s => s.PublishedVersionId).SingleOrDefaultAsync(ct);
        if (id is null) return;
        var version = await db.FileVersions.AsNoTracking().SingleAsync(v => v.Id == id, ct);
        await events.AddedAsync(version, false, ct);
        await items.ReindexAsync(version.ItemId, ct);
        live.Publish(DocumentLiveEvents.Changed(tenant, user, version, version.Source));
    }
}
