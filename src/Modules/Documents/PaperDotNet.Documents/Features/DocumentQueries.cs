using Microsoft.EntityFrameworkCore;
using PaperDotNet.Documents.Data;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// The module's queries, each one precompiled expression with its values copied into locals and an explicit tenant
/// (ADR-0039).
/// </summary>
internal static class DocumentQueries
{
    public static Task<StoredFile?> StoredFileByHashAsync(DocumentsDbContext database, Guid tenantId, string sha256, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var hash = sha256;
        var ct = cancellationToken;
        return db.StoredFiles.Where(f => f.TenantId == tenant && f.Sha256 == hash).FirstOrDefaultAsync(ct);
    }

    public static Task<StoredFile?> StoredFileAsync(DocumentsDbContext database, Guid tenantId, Guid storedFileId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = storedFileId;
        var ct = cancellationToken;
        return db.StoredFiles.Where(f => f.TenantId == tenant && f.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The item's current file version (tracked).</summary>
    public static Task<FileVersion?> CurrentAsync(DocumentsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var item = itemId;
        var ct = cancellationToken;
        return db.FileVersions.Where(v => v.TenantId == tenant && v.ItemId == item && v.IsCurrent).FirstOrDefaultAsync(ct);
    }

    public static Task<FileVersion?> VersionAsync(DocumentsDbContext database, Guid tenantId, Guid itemId, int number, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var item = itemId;
        var wanted = number;
        var ct = cancellationToken;
        return db.FileVersions.Where(v => v.TenantId == tenant && v.ItemId == item && v.Number == wanted).FirstOrDefaultAsync(ct);
    }

    public static Task<FileVersion?> VersionByIdAsync(DocumentsDbContext database, Guid tenantId, Guid versionId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = versionId;
        var ct = cancellationToken;
        return db.FileVersions.Where(v => v.TenantId == tenant && v.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Every version of the item, newest first.</summary>
    public static Task<List<FileVersion>> VersionsAsync(DocumentsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var item = itemId;
        var ct = cancellationToken;
        return db.FileVersions.AsNoTracking().Where(v => v.TenantId == tenant && v.ItemId == item).OrderByDescending(v => v.Number).ToListAsync(ct);
    }

    /// <summary>Every version of the item, tracked (to remove them).</summary>
    public static Task<List<FileVersion>> VersionsOfItemAsync(DocumentsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var item = itemId;
        var ct = cancellationToken;
        return db.FileVersions.Where(v => v.TenantId == tenant && v.ItemId == item).ToListAsync(ct);
    }

    public static Task<int?> LastNumberAsync(DocumentsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var item = itemId;
        var ct = cancellationToken;
        return db.FileVersions.Where(v => v.TenantId == tenant && v.ItemId == item).MaxAsync(v => (int?)v.Number, ct);
    }

    public static Task<bool> HasVersionsAsync(DocumentsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var item = itemId;
        var ct = cancellationToken;
        return db.FileVersions.AnyAsync(v => v.TenantId == tenant && v.ItemId == item, ct);
    }

    /// <summary>Current files with the content, oldest first (ids are time-ordered).</summary>
    public static Task<List<FileVersion>> CurrentWithHashAsync(DocumentsDbContext database, Guid tenantId, string sha256, int take, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var hash = sha256;
        var count = take;
        var ct = cancellationToken;
        return db.FileVersions.AsNoTracking().Where(v => v.TenantId == tenant && v.Sha256 == hash && v.IsCurrent).OrderBy(v => v.Id).Take(count).ToListAsync(ct);
    }

    public static Task<LibrarySettings?> SettingsAsync(DocumentsDbContext database, Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var list = listId;
        var ct = cancellationToken;
        return db.LibrarySettings.Where(s => s.TenantId == tenant && s.ListId == list).FirstOrDefaultAsync(ct);
    }

    /// <summary>The saved page texts of a stored file, in order.</summary>
    public static Task<List<StoredFilePage>> PagesAsync(DocumentsDbContext database, Guid tenantId, Guid storedFileId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var stored = storedFileId;
        var ct = cancellationToken;
        return db.Pages.AsNoTracking().Where(p => p.TenantId == tenant && p.StoredFileId == stored).OrderBy(p => p.PageNumber).ToListAsync(ct);
    }

    public static Task<bool> HasPagesAsync(DocumentsDbContext database, Guid tenantId, Guid storedFileId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var stored = storedFileId;
        var ct = cancellationToken;
        return db.Pages.AnyAsync(p => p.TenantId == tenant && p.StoredFileId == stored, ct);
    }

    /// <summary>Stored content no version refers to, unused since <paramref name="cutoffUnixMs"/>.</summary>
    public static Task<List<StoredFile>> OrphansAsync(DocumentsDbContext database, Guid tenantId, long cutoffUnixMs, int take, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var cutoff = cutoffUnixMs;
        var count = take;
        var ct = cancellationToken;
        return db.StoredFiles
            .Where(f => f.TenantId == tenant && f.LastUsedAtUnixMs < cutoff && !db.FileVersions.Any(v => v.TenantId == tenant && v.StoredFileId == f.Id))
            .Take(count)
            .ToListAsync(ct);
    }

    /// <summary>The page texts of stored content (tracked, to remove them with it).</summary>
    public static Task<List<StoredFilePage>> PagesOfAsync(DocumentsDbContext database, Guid tenantId, Guid storedFileId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var stored = storedFileId;
        var ct = cancellationToken;
        return db.Pages.Where(p => p.TenantId == tenant && p.StoredFileId == stored).ToListAsync(ct);
    }

    /// <summary>Every version of every document of a library (exports).</summary>
    public static Task<List<FileVersion>> VersionsOfListAsync(DocumentsDbContext database, Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var list = listId;
        var ct = cancellationToken;
        return db.FileVersions.AsNoTracking().Where(v => v.TenantId == tenant && v.ListId == list).ToListAsync(ct);
    }

    public static Task<GroupInbox?> InboxOfGroupAsync(DocumentsDbContext database, Guid tenantId, Guid groupId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var group = groupId;
        var ct = cancellationToken;
        return db.GroupInboxes.Where(g => g.TenantId == tenant && g.GroupId == group).FirstOrDefaultAsync(ct);
    }

    public static Task<GroupInbox?> InboxOfListAsync(DocumentsDbContext database, Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var list = listId;
        var ct = cancellationToken;
        return db.GroupInboxes.Where(g => g.TenantId == tenant && g.ListId == list).FirstOrDefaultAsync(ct);
    }
}
