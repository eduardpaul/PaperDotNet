using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Dav.Data;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.Dav.Features;

/// <summary>
/// Temporary files of desktop apps (ADR-0047): Office owner files (<c>~$…</c>), save files (<c>~WRD0001.tmp</c>,
/// Excel's <c>9A3B2C00</c>), LibreOffice locks (<c>.~lock.…#</c>) and system files. They are kept per user for a day and
/// never become documents, so they create no items, versions or workflow runs.
/// </summary>
internal static partial class DavTransient
{
    private static readonly HashSet<string> SystemFiles = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "Thumbs.db", ".DS_Store" };

    public static bool IsTransient(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal)
        || name.StartsWith("._", StringComparison.Ordinal)
        || (name.StartsWith(".~lock.", StringComparison.Ordinal) && name.EndsWith('#'))
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || SystemFiles.Contains(name)
        || ExcelTemporary().IsMatch(name);

    [GeneratedRegex("^[0-9A-F]{8}$")]
    private static partial Regex ExcelTemporary();
}

/// <summary>The caller's temporary files (<see cref="DavTransientFile"/>), with their content in the blob store.</summary>
internal sealed class DavTransientStore(DavDbContext db, IBlobStore blobs, ITenantContext tenant, ICurrentUser user, TimeProvider time)
{
    /// <summary>How long a temporary file is kept after its last change.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    private Guid UserId => user.UserId ?? throw new InvalidOperationException("Temporary files need a user.");

    public async Task<List<DavTransientFile>> ListAsync(Guid parentId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var userId = UserId;
        return await db.TransientFiles.Where(f => f.UserId == userId && f.ParentId == parentId && f.ExpiresAt > now).ToListAsync(ct);
    }

    public Task<DavTransientFile?> FindAsync(Guid parentId, string name, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var userId = UserId;
        var key = name.ToUpperInvariant();
        return db.TransientFiles.FirstOrDefaultAsync(f => f.UserId == userId && f.ParentId == parentId && f.NameKey == key && f.ExpiresAt > now, ct);
    }

    /// <summary>Stores <paramref name="content"/> as <paramref name="name"/>, replacing an earlier file of that name.</summary>
    public async Task<DavTransientFile> WriteAsync(Guid workspaceId, Guid listId, Guid parentId, string name, Stream content, long maxSize, CancellationToken ct)
    {
        var existing = await FindAsync(parentId, name, ct);
        var file = existing ?? await AddAsync(workspaceId, listId, parentId, name, ct);
        if (file.ItemId is not null)
        {
            // Writing to a renamed document's temporary name gives it content of its own.
            file.ItemId = null;
            file.VersionId = null;
            file.OriginalName = null;
            file.Released = false;
        }

        var key = file.BlobKey ?? $"{tenant.TenantId:N}/dav/{file.Id:N}";
        try
        {
            await using var limited = new LimitedStream(content, maxSize);
            await blobs.WriteAsync(key, limited, ct);
            file.Size = limited.Count;
        }
        catch when (existing is null)
        {
            // A new file that could not be written (too large, connection lost) is not kept.
            file.BlobKey = key;
            await DeleteAsync(file, CancellationToken.None);
            throw;
        }

        file.BlobKey = key;
        Touch(file);
        await db.SaveChangesAsync(ct);
        return file;
    }

    /// <summary>Shows a document as <paramref name="name"/> until the app saves it back or deletes the name.</summary>
    public async Task<DavTransientFile> AliasAsync(
        Guid workspaceId, Guid listId, Guid parentId, string name, Guid itemId, Guid versionId, long size, string originalName, CancellationToken ct)
    {
        var existing = await FindAsync(parentId, name, ct);
        if (existing is not null)
        {
            await DeleteAsync(existing, ct);
        }

        var file = await AddAsync(workspaceId, listId, parentId, name, ct);
        file.ItemId = itemId;
        file.VersionId = versionId;
        file.OriginalName = originalName;
        file.Size = size;
        await db.SaveChangesAsync(ct);
        return file;
    }

    public async Task MoveAsync(DavTransientFile file, Guid workspaceId, Guid listId, Guid parentId, string name, CancellationToken ct)
    {
        file.WorkspaceId = workspaceId;
        file.ListId = listId;
        file.ParentId = parentId;
        file.Name = name;
        file.NameKey = name.ToUpperInvariant();
        Touch(file);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(DavTransientFile alias, CancellationToken ct)
    {
        alias.Released = true;
        Touch(alias);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(DavTransientFile file, CancellationToken ct)
    {
        if (file.BlobKey is { } key)
        {
            await blobs.DeleteAsync(key, ct);
        }

        db.TransientFiles.Remove(file);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Stream> OpenReadAsync(DavTransientFile file, CancellationToken ct) =>
        file.BlobKey is { } key ? await blobs.OpenReadAsync(key, ct) ?? Stream.Null : Stream.Null;

    private async Task<DavTransientFile> AddAsync(Guid workspaceId, Guid listId, Guid parentId, string name, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var file = new DavTransientFile
        {
            Id = Ids.New(),
            UserId = UserId,
            WorkspaceId = workspaceId,
            ListId = listId,
            ParentId = parentId,
            Name = name,
            NameKey = name.ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        db.TransientFiles.Add(file);
        await db.SaveChangesAsync(ct);
        return file;
    }

    private void Touch(DavTransientFile file)
    {
        file.UpdatedAt = time.GetUtcNow();
        file.ExpiresAt = file.UpdatedAt + Lifetime;
    }
}

/// <summary>Reads at most <c>max</c> bytes; more fails with 413.</summary>
internal sealed class LimitedStream(Stream inner, long max) : Stream
{
    public long Count { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Counted(int read)
    {
        Count += read;
        return Count > max
            ? throw new FubarDev.WebDavServer.WebDavException((FubarDev.WebDavServer.WebDavStatusCode)413, "The file is too large.")
            : read;
    }
}
