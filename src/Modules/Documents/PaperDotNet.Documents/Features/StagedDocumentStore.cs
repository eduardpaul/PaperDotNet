using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Data;

namespace PaperDotNet.Documents.Features;

internal sealed class StagedDocumentStore(DocumentsDbContext db, IDocumentFileStore files, IBlobStore blobs,
    FileIntake intake, IOptions<DocumentsOptions> options, ICurrentUser user, TimeProvider time) : IStagedDocumentStore
{
    private async Task<StagedFile?> OwnedAsync(Guid id, CancellationToken ct) =>
        await db.StagedFiles.SingleOrDefaultAsync(s => s.Id == id && s.CreatedBy == user.UserId, ct);

    private async Task<StagedDocument> DescribeAsync(StagedFile record, CancellationToken ct)
    {
        var stored = record.StoredFileId is { } id ? await db.StoredFiles.SingleOrDefaultAsync(s => s.Id == id, ct) : null;
        return new(record.Id, record.Owner, record.CreatedBy, record.FileName, record.Languages, record.State,
            stored?.MediaType, stored?.Size, record.PageCount)
        { Attributes = ParseAttributes(record.Attributes) };
    }

    public async Task<StagedDocument?> GetAsync(Guid id, CancellationToken ct) =>
        await OwnedAsync(id, ct) is { } record ? await DescribeAsync(record, ct) : null;

    private static JsonObject ParseAttributes(string json) => JsonNode.Parse(json)!.AsObject();

    public async Task<StagedDocument> CreateAsync(Guid id, string owner, string fileName, string? languages, CancellationToken ct, JsonObject? attributes = null)
    {
        var attributesJson = attributes?.ToJsonString() ?? "{}";
        if (user.UserId is not { } actor || string.IsNullOrWhiteSpace(owner) || owner.Length > 100
            || string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
            throw new InvalidOperationException("Temporary documents require an authenticated owner and a valid name.");
        if (await db.StagedFiles.SingleOrDefaultAsync(s => s.Id == id, ct) is { } existing)
        {
            if (existing.CreatedBy != actor || existing.Owner != owner || existing.FileName != fileName || existing.Languages != languages
                || !JsonNode.DeepEquals(ParseAttributes(existing.Attributes), ParseAttributes(attributesJson)))
                throw new InvalidOperationException("The execution identifier belongs to different temporary content.");
            return await DescribeAsync(existing, ct);
        }
        var record = new StagedFile
        {
            Id = id,
            Owner = owner,
            CreatedBy = actor,
            FileName = fileName,
            Languages = languages,
            Attributes = attributesJson,
            CreatedAt = time.GetUtcNow()
        };
        db.StagedFiles.Add(record);
        await db.SaveChangesAsync(ct);
        return await DescribeAsync(record, ct);
    }

    public async Task RetainVersionsAsync(Guid id, IReadOnlyList<Guid> versionIds, CancellationToken ct)
    {
        var record = await OwnedAsync(id, ct) ?? throw new InvalidOperationException("Temporary document not found.");
        if (record.State == "released") throw new InvalidOperationException("Temporary document was released.");
        foreach (var versionId in versionIds.Distinct())
        {
            await using var content = await files.OpenVersionAsync(versionId, ct) ?? throw new InvalidOperationException("A source version is unavailable.");
            var stored = await db.FileVersions.Where(v => v.Id == versionId).Select(v => v.StoredFileId).SingleAsync(ct);
            if (!await db.StagedReferences.AnyAsync(r => r.StagedFileId == id && r.StoredFileId == stored, ct)
                && !db.StagedReferences.Local.Any(r => r.StagedFileId == id && r.StoredFileId == stored))
                db.StagedReferences.Add(new StagedFileReference { StagedFileId = id, StoredFileId = stored });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<StagedDocument> StageAsync(Guid id, Stream content, IReadOnlyList<string>? pageTexts, CancellationToken ct)
    {
        var record = await OwnedAsync(id, ct) ?? throw new InvalidOperationException("Temporary document not found.");
        if (record.State == "ready") return await DescribeAsync(record, ct);
        if (record.State != "preparing") throw new InvalidOperationException("Temporary document was released.");
        await using var spooled = await FileIntake.SpoolAsync(content, options.Value.MaxFileSize, ct);
        if (spooled.TooLarge || spooled.MediaType is null) throw new InvalidOperationException("Unsupported content or document size limit exceeded.");
        int? pageCount = null;
        if (spooled.MediaType == FileTypes.Pdf)
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(spooled.Path);
            pageCount = pdf.NumberOfPages;
            if (pageTexts is not null && pageCount != pageTexts.Count) throw new InvalidOperationException("Provide one text entry per PDF page.");
        }
        else if (pageTexts is not null) throw new InvalidOperationException("Page text can only be supplied with a PDF.");
        var stored = await intake.StoreAsync(spooled, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.StagedFiles.Where(s => s.Id == id && s.CreatedBy == user.UserId && s.State == "preparing")
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.StoredFileId, stored.Id).SetProperty(s => s.State, "ready").SetProperty(s => s.HasPreparedText, pageTexts != null).SetProperty(s => s.PageCount, pageCount), ct);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var completed = await GetAsync(id, ct);
            return completed is { State: "ready" } ? completed : throw new InvalidOperationException("Temporary document was released.");
        }
        if (pageTexts is not null && !await db.Pages.AnyAsync(p => p.StoredFileId == stored.Id, ct))
            for (var page = 0; page < pageTexts.Count; page++)
                db.Pages.Add(new StoredFilePage { StoredFileId = stored.Id, PageNumber = page + 1, Text = pageTexts[page] });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await db.Entry(record).ReloadAsync(ct);
        return await DescribeAsync(record, ct);
    }

    public async Task<Stream?> OpenAsync(Guid id, CancellationToken ct)
    {
        if (await OwnedAsync(id, ct) is not { State: "ready", StoredFileId: { } storedId }) return null;
        var stored = await db.StoredFiles.SingleOrDefaultAsync(s => s.Id == storedId, ct);
        return stored is null ? null : await blobs.OpenReadAsync(stored.BlobKey, ct);
    }

    public async Task ReleaseAsync(Guid id, CancellationToken ct)
    {
        if (await OwnedAsync(id, ct) is { } record)
        {
            record.State = "released";
            await db.SaveChangesAsync(ct);
        }
    }
}
