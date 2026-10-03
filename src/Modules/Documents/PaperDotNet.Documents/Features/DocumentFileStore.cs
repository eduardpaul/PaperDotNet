using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

internal sealed class DocumentFileStore(
    DocumentsDbContext db, IListItemStore items, IBlobStore blobs, FileIntake intake, DocumentEvents events,
    IItemActivity activity, TimeProvider time, ILiveEvents live, ITenantContext tenant, ICurrentUser user, IUserPreferences preferences) : IDocumentFileStore
{
    private bool _system;

    public IDocumentFileStore AsSystem() => new DocumentFileStore(db, items, blobs, intake, events, activity, time, live, tenant, user, preferences) { _system = true };

    private async Task<bool> AllowedAsync(DocumentFile file, bool write, CancellationToken ct)
    {
        var item = await (_system ? items.AsSystem() : items).GetAsync(file.WorkspaceId, file.ListId, file.ItemId, ct);
        return item is not null && (_system || !write || item.Access >= WorkspaceAccessLevel.Contribute);
    }

    internal static DocumentFile Describe(FileVersion v) => new(v.Id, v.WorkspaceId, v.ListId, v.ItemId, v.Number,
        v.FileName, v.MediaType, v.Size, v.Sha256, v.Source, v.Languages);

    public async Task<DocumentFile?> GetCurrentAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var version = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, cancellationToken);
        return version is not null && await AllowedAsync(Describe(version), false, cancellationToken)
            ? Describe(version) with { AnalysisLanguages = await DocumentText.LanguagesAsync(db, preferences, version, null, cancellationToken) } : null;
    }

    public async Task<Stream?> OpenVersionAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        if (version is null || !await AllowedAsync(Describe(version), false, cancellationToken))
        {
            return null;
        }

        var stored = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == version.StoredFileId, cancellationToken);
        return stored is null ? null : await blobs.OpenReadAsync(stored.BlobKey, cancellationToken);
    }

    private async Task<DocumentCandidate> DescribeAsync(FileCandidate c, CancellationToken ct)
    {
        var stored = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == c.StoredFileId, ct);
        return new(c.Id, c.RunId, JsonSerializer.Deserialize<DocumentFile>(c.SourceJson)!, c.State,
            c.FileName, stored?.MediaType ?? "image/webp", stored?.Size ?? 0, JsonNode.Parse(c.MetricsJson)!.AsObject());
    }

    public async Task<DocumentCandidate?> GetCandidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var candidate = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var result = await DescribeAsync(candidate, cancellationToken);
        return await AllowedAsync(result.Source, false, cancellationToken) ? result : null;
    }

    public async Task<DocumentCandidate?> FindSelectionAsync(Guid sourceVersionId, CancellationToken cancellationToken)
    {
        var candidate = await db.Candidates.AsNoTracking().Where(c => c.SourceVersionId == sourceVersionId && (c.State == "rejected" || c.State == "skipped"))
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return candidate is null ? null : await GetCandidateAsync(candidate.Id, cancellationToken);
    }

    public async Task<DocumentCandidate> RetainOriginalAsync(Guid id, Guid runId, Guid sourceVersionId, string reason,
        JsonObject metrics, CancellationToken cancellationToken)
    {
        var existing = await GetCandidateAsync(id, cancellationToken);
        if (existing is not null && existing.State != "abandoned")
        {
            return existing;
        }

        var source = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == sourceVersionId && v.IsCurrent, cancellationToken)
            ?? throw new InvalidOperationException("The source file changed before optimization finished.");
        if (!await AllowedAsync(Describe(source), true, cancellationToken))
        {
            throw new InvalidOperationException("You may not change this document.");
        }

        var analysis = metrics.DeepClone().AsObject();
        analysis["reason"] = reason;
        var selection = new FileCandidate
        {
            Id = id,
            RunId = runId,
            ItemId = source.ItemId,
            SourceVersionId = source.Id,
            SourceStoredFileId = source.StoredFileId,
            StoredFileId = source.StoredFileId,
            State = "skipped",
            CreatedAt = time.GetUtcNow(),
            SourceJson = JsonSerializer.Serialize(Describe(source)),
            MetricsJson = analysis.ToJsonString(),
            FileName = source.FileName,
        };
        if (existing is not null)
        {
            return await RenewAsync(existing, selection, cancellationToken);
        }

        db.Candidates.Add(selection);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(selection).State = EntityState.Detached;
            if (await GetCandidateAsync(id, cancellationToken) is { } repeated)
            {
                return repeated;
            }

            throw;
        }

        return await DescribeAsync(selection, cancellationToken);
    }

    public async Task<DocumentCandidate> StageAsync(Guid id, Guid runId, Guid sourceVersionId, Stream content,
        string fileName, JsonObject metrics, CancellationToken cancellationToken)
    {
        var existing = await GetCandidateAsync(id, cancellationToken);
        if (existing is not null && existing.State != "abandoned")
        {
            return existing;
        }

        var source = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == sourceVersionId && v.IsCurrent, cancellationToken)
            ?? throw new InvalidOperationException("The source file changed before optimization finished.");
        if (!await AllowedAsync(Describe(source), true, cancellationToken))
        {
            throw new InvalidOperationException("You may not change this document.");
        }

        await using var spooled = await FileIntake.SpoolAsync(content, source.Size, cancellationToken);
        if (spooled.TooLarge || spooled.Size >= source.Size || spooled.MediaType is null)
        {
            throw new InvalidOperationException("The candidate must be a smaller supported file.");
        }

        var stored = await intake.StoreAsync(spooled, cancellationToken);
        var candidate = new FileCandidate
        {
            Id = id,
            RunId = runId,
            ItemId = source.ItemId,
            SourceVersionId = source.Id,
            SourceStoredFileId = source.StoredFileId,
            StoredFileId = stored.Id,
            CreatedAt = time.GetUtcNow(),
            SourceJson = JsonSerializer.Serialize(Describe(source) with { AnalysisLanguages = metrics["languages"]?.GetValue<string>() }),
            MetricsJson = metrics.ToJsonString(),
            FileName = Path.GetFileName(fileName),
        };
        if (existing is not null)
        {
            return await RenewAsync(existing, candidate, cancellationToken);
        }

        db.Candidates.Add(candidate);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(candidate).State = EntityState.Detached;
            if (await GetCandidateAsync(id, cancellationToken) is { } repeated)
            {
                return repeated;
            }

            throw;
        }

        return await DescribeAsync(candidate, cancellationToken);
    }

    private async Task<DocumentCandidate> RenewAsync(DocumentCandidate existing, FileCandidate replacement, CancellationToken ct)
    {
        if (existing.RunId != replacement.RunId || existing.Source.Id != replacement.SourceVersionId)
        {
            throw new InvalidOperationException("The execution key belongs to another document selection.");
        }

        // Cleanup releases failed runs' candidates. A retry can stage fresh bytes under the same execution key.
        await db.Candidates.Where(c => c.Id == replacement.Id && c.State == "abandoned")
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, replacement.State)
                .SetProperty(c => c.StoredFileId, replacement.StoredFileId)
                .SetProperty(c => c.FileName, replacement.FileName)
                .SetProperty(c => c.SourceJson, replacement.SourceJson)
                .SetProperty(c => c.MetricsJson, replacement.MetricsJson)
                .SetProperty(c => c.CreatedAt, replacement.CreatedAt), ct);
        return await GetCandidateAsync(replacement.Id, ct)
            ?? throw new InvalidOperationException("The document selection is no longer available.");
    }

    public async Task<Stream?> OpenCandidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var candidate = await GetCandidateAsync(id, cancellationToken);
        if (candidate is null || candidate.State is not ("pending" or "accepted"))
        {
            return null;
        }

        var storedId = await db.Candidates.Where(c => c.Id == id).Select(c => c.StoredFileId).FirstAsync(cancellationToken);
        var stored = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == storedId, cancellationToken);
        return stored is null ? null : await blobs.OpenReadAsync(stored.BlobKey, cancellationToken);
    }

    public async Task<bool> PromoteAsync(Guid id, CancellationToken cancellationToken)
    {
        var candidate = await GetCandidateAsync(id, cancellationToken);
        if (candidate is null || !await AllowedAsync(candidate.Source, true, cancellationToken))
        {
            return false;
        }

        var record = await db.Candidates.FirstAsync(c => c.Id == id, cancellationToken);
        if (record.State == "accepted")
        {
            var promoted = await db.FileVersions.FirstOrDefaultAsync(v => v.Id == record.PromotedVersionId, cancellationToken);
            if (promoted is not null)
            {
                await AnnounceAsync(promoted, candidate, cancellationToken);
            }

            return true;
        }

        if (record.State != "pending")
        {
            return false;
        }

        var stored = await db.StoredFiles.FirstAsync(f => f.Id == record.StoredFileId, cancellationToken);
        if (!await blobs.ExistsAsync(stored.BlobKey, cancellationToken))
        {
            throw new InvalidOperationException("The optimized content is missing.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Candidates.Where(c => c.Id == id && c.State == "pending")
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "promoting"), cancellationToken) != 1)
        {
            return false;
        }

        var claimed = await db.FileVersions.Where(v => v.Id == record.SourceVersionId && v.IsCurrent)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.IsCurrent, false), cancellationToken);
        if (claimed != 1)
        {
            return false;
        }

        var source = await db.FileVersions.AsNoTracking().FirstAsync(v => v.Id == record.SourceVersionId, cancellationToken);
        var number = await db.FileVersions.Where(v => v.ItemId == source.ItemId).MaxAsync(v => v.Number, cancellationToken) + 1;
        var version = new FileVersion
        {
            Id = Ids.New(),
            WorkspaceId = source.WorkspaceId,
            ListId = source.ListId,
            ItemId = source.ItemId,
            Number = number,
            IsCurrent = true,
            StoredFileId = stored.Id,
            Sha256 = stored.Sha256,
            Size = stored.Size,
            MediaType = stored.MediaType,
            FileName = record.FileName,
            Source = "optimization",
            PageCount = 1,
            Languages = source.Languages ?? candidate.Source.AnalysisLanguages,
            TextLanguage = source.TextLanguage,
        };
        db.FileVersions.Add(version);
        record.State = "accepted";
        record.PromotedVersionId = version.Id;
        await db.FileVersions.Where(v => v.Id == source.Id).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await AnnounceAsync(version, candidate, cancellationToken);
        return true;
    }

    private async Task AnnounceAsync(FileVersion version, DocumentCandidate candidate, CancellationToken ct)
    {
        await events.AddedAsync(version, false, ct);
        await items.AsSystem().ReindexAsync(version.ItemId, ct);
        live.Publish(DocumentLiveEvents.Changed(tenant, user, version, "optimization"));
        await activity.RecordAsync(new ItemActivityEntry(version.WorkspaceId, version.ListId, version.ItemId,
            "document.optimized", $"Stored optimized file instead of source {candidate.Source.Id} ({candidate.Source.Sha256}), version {candidate.Source.Number}: {candidate.Source.Size} → {version.Size} bytes. {candidate.Metrics.ToJsonString()}",
            $"optimization:{candidate.Id:N}"), ct);
    }

    public async Task<bool> DiscardAsync(Guid id, CancellationToken cancellationToken)
    {
        var candidate = await GetCandidateAsync(id, cancellationToken);
        if (candidate is null || !await AllowedAsync(candidate.Source, true, cancellationToken))
        {
            return false;
        }

        if (candidate.State == "rejected")
        {
            await RecordRejectionAsync(candidate, cancellationToken);
            return true;
        }

        var discarded = await db.Candidates.Where(c => c.Id == id && c.State == "pending")
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "rejected"), cancellationToken) == 1;
        if (discarded)
        {
            await RecordRejectionAsync(candidate, cancellationToken);
        }

        return discarded;
    }

    private Task RecordRejectionAsync(DocumentCandidate candidate, CancellationToken ct) =>
        activity.RecordAsync(new ItemActivityEntry(candidate.Source.WorkspaceId, candidate.Source.ListId, candidate.Source.ItemId,
            "document.optimizationRejected", $"Kept source version {candidate.Source.Id}: {candidate.Metrics.ToJsonString()}",
            $"optimization:{candidate.Id:N}"), ct);
}
