using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features.PhotoToDocument;

internal sealed record PhotoSource(DocumentFile File, uint ItemVersion);
internal sealed record PhotoConversion(Guid Id, Guid RunId, Guid PrimaryItemId, Guid StartedBy,
    string State, string FileName, string Languages, IReadOnlyList<PhotoSource> Sources, int? PageCount);

internal sealed class PhotoConversionStore(PhotoConversionDbContext db, IStagedDocumentStore staging, IDocumentPublisher publisher,
    IListItemStore items, IDocumentFileStore files, IItemBatchRecycle recycle, IUserDirectory users, ICurrentUser user,
    IItemActivity activity)
{
    private async Task<PhotoConversion> DescribeAsync(PhotoConversionRecord record, CancellationToken ct) =>
        new(record.Id, record.RunId, record.PrimaryItemId, record.StartedBy, record.State, record.FileName, record.Languages,
            JsonSerializer.Deserialize<PhotoSource[]>(record.SourcesJson)!, (await staging.GetAsync(record.Id, ct))?.PageCount);

    public async Task<PhotoConversion?> GetAsync(Guid id, CancellationToken ct) =>
        await db.Conversions.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } record ? await DescribeAsync(record, ct) : null;

    public async Task<PhotoConversion> BeginAsync(Guid id, Guid runId, Guid primaryItemId, Guid startedBy,
        IReadOnlyList<PhotoSource> sources, string languages, CancellationToken ct)
    {
        var record = await db.Conversions.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (record is not null)
        {
            if (record.RunId != runId || record.PrimaryItemId != primaryItemId || record.StartedBy != startedBy)
                throw new InvalidOperationException("The execution key belongs to another photo conversion.");
        }
        else
        {
            if (sources.Count is 0 or > 100 || sources.Select(s => s.File.ItemId).Distinct().Count() != sources.Count
                || sources.Select(s => (s.File.WorkspaceId, s.File.ListId)).Distinct().Count() != 1 || user.UserId != startedBy)
                throw new InvalidOperationException("A photo conversion requires an authorized selection from one library.");
            var primary = sources.Single(s => s.File.ItemId == primaryItemId).File;
            record = new PhotoConversionRecord { Id = id, RunId = runId, PrimaryItemId = primaryItemId, StartedBy = startedBy,
                FileName = Path.ChangeExtension(primary.FileName, ".pdf"), Languages = languages, SourcesJson = JsonSerializer.Serialize(sources) };
            db.Conversions.Add(record);
            await db.SaveChangesAsync(ct);
        }
        await staging.CreateAsync(id, PhotoToDocument.Key, record.FileName, record.Languages, ct);
        // Re-establish pins after a crash between recording snapshots and protecting their bytes.
        await staging.RetainVersionsAsync(id, JsonSerializer.Deserialize<PhotoSource[]>(record.SourcesJson)!.Select(s => s.File.Id).ToArray(), ct);
        if (!await IsFreshAsync(id, true, ct)) throw new InvalidOperationException("A source changed or you may not change it.");
        return await DescribeAsync(record, ct);
    }

    public async Task<PhotoConversion> StageAsync(Guid id, Stream pdf, IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!await IsFreshAsync(id, true, ct)) throw new InvalidOperationException("A source changed or access was removed.");
        await staging.StageAsync(id, pdf, texts, ct);
        if (await db.Conversions.Where(c => c.Id == id && (c.State == "preparing" || c.State == "pending"))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "pending"), ct) != 1)
            throw new InvalidOperationException("The photo conversion was settled or abandoned.");
        return (await GetAsync(id, ct))!;
    }

    public async Task<bool> IsFreshAsync(Guid id, bool write, CancellationToken ct)
    {
        var selection = await GetAsync(id, ct);
        if (selection is null || selection.State is "accepted" or "rejected" or "abandoned"
            || write && (user.UserId != selection.StartedBy || !await users.IsActiveAsync(selection.StartedBy, ct))) return false;
        foreach (var source in selection.Sources)
        {
            var file = source.File;
            var item = await items.GetAsync(file.WorkspaceId, file.ListId, file.ItemId, ct);
            if (item is null || item.IsFolder || item.Version != source.ItemVersion || write && item.Access < WorkspaceAccessLevel.Contribute
                || (await files.GetCurrentAsync(file.ItemId, ct))?.Id != file.Id) return false;
        }
        return true;
    }

    public Task<Stream?> OpenAsync(Guid id, CancellationToken ct) => staging.OpenAsync(id, ct);

    private static ItemRecycleTarget[] Reviewed(PhotoConversion selection) => selection.Sources.Select(s =>
        new ItemRecycleTarget(s.File.WorkspaceId, s.File.ListId, s.File.ItemId, s.ItemVersion, s.File.ItemId != selection.PrimaryItemId, selection.RunId)).ToArray();

    public async Task<bool> PromoteAsync(Guid id, CancellationToken ct)
    {
        var selection = await GetAsync(id, ct);
        if (selection is null || user.UserId != selection.StartedBy) return false;
        if (selection.State == "accepted") { await AnnounceAsync(selection, ct); return true; }
        if (selection.State != "pending" || !await IsFreshAsync(id, true, ct)) return false;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (await db.Conversions.Where(c => c.Id == id && c.State == "pending")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "promoting"), ct) != 1) return false;
            await publisher.LockVersionsAsync(selection.Sources.Select(s => s.File).ToArray(), transaction.GetDbTransaction(), ct);
            await recycle.RecycleAsync(Reviewed(selection), transaction.GetDbTransaction(), ct);
            await publisher.PublishAsync(id, selection.Sources.Single(s => s.File.ItemId == selection.PrimaryItemId).File,
                "composition", transaction.GetDbTransaction(), ct);
            await db.Conversions.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "accepted"), ct);
            await transaction.CommitAsync(ct);
        }
        await AnnounceAsync(selection, ct);
        return true;
    }

    private async Task AnnounceAsync(PhotoConversion selection, CancellationToken ct)
    {
        await publisher.AnnounceAsync(selection.Id, ct);
        await recycle.AnnounceAsync(Reviewed(selection), ct);
        var primary = selection.Sources.Single(s => s.File.ItemId == selection.PrimaryItemId).File;
        await activity.RecordAsync(new ItemActivityEntry(primary.WorkspaceId, primary.ListId, primary.ItemId,
            "document.composed", $"Approved composed PDF with {selection.Sources.Count} pages; recycled the other source items.", $"composition:{selection.Id:N}"), ct);
    }

    public async Task<bool> DiscardAsync(Guid id, CancellationToken ct)
    {
        if (await GetAsync(id, ct) is not { } selection || user.UserId != selection.StartedBy) return false;
        if (selection.State == "rejected") { await staging.ReleaseAsync(id, ct); return true; }
        if (await db.Conversions.Where(c => c.Id == id && c.State == "pending")
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "rejected"), ct) != 1) return false;
        await staging.ReleaseAsync(id, ct);
        return true;
    }

}
