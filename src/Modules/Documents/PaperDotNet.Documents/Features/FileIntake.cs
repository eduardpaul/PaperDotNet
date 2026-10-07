using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Detects file types by content (DOC-02): the first bytes decide, never the file name. Libraries take any file
/// (ADR-0047); only the types detected here are processed (text, images, OCR), others are stored as they are.
/// </summary>
public static class FileTypes
{
    private static readonly FileExtensionContentTypeProvider Names = new();

    public const string Pdf = "application/pdf";
    public const string Tiff = "image/tiff";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";

    /// <summary>Content of any other type.</summary>
    public const string Other = "application/octet-stream";

    /// <summary>Bytes needed by <see cref="Detect"/>.</summary>
    public const int HeaderLength = 12;

    /// <summary>The media type of a supported file (PDF, TIFF, JPEG, PNG, WebP), or null.</summary>
    public static string? Detect(ReadOnlySpan<byte> header) => header switch
    {
        [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => Pdf,                           // %PDF-
        [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => Tiff,  // II*. / MM.*
        [0xFF, 0xD8, 0xFF, ..] => Jpeg,
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Png,
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => Webp,
        _ => null,
    };

    /// <summary>Whether the server processes files of this type (the types <see cref="Detect"/> finds).</summary>
    public static bool IsProcessable(string? mediaType) => mediaType is Pdf or Tiff or Jpeg or Png or Webp;

    /// <summary>
    /// The media type of a file version: the detected type of its content, or for other content the type its name
    /// suggests, for downloads only. A name never makes content processable: <c>scan.pdf</c> without a PDF header is
    /// <see cref="Other"/>.
    /// </summary>
    public static string ForVersion(string contentType, string? fileName) =>
        IsProcessable(contentType) ? contentType
        : Names.TryGetContentType(fileName ?? string.Empty, out var byName) && !IsProcessable(byName) ? byName
        : Other;
}

/// <summary>An upload spooled to a temporary file, with its hash and detected type (<see cref="FileTypes.Other"/> if none).</summary>
internal sealed class SpooledFile(string path, string sha256, long size, string mediaType) : IAsyncDisposable
{
    public string Path { get; } = path;

    public string Sha256 { get; } = sha256;

    public long Size { get; } = size;

    public string MediaType { get; } = mediaType;

    public bool TooLarge { get; init; }

    public Stream OpenRead() => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public ValueTask DisposeAsync()
    {
        File.Delete(Path);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Spools uploads (streaming, hashed on the fly) and stores their content once per tenant (DOC-11).</summary>
internal sealed class FileIntake(DocumentsDbContext db, IBlobStore blobs, TimeProvider time)
{
    /// <summary>Copies <paramref name="content"/> to a temporary file, stopping after <paramref name="maxSize"/> bytes.</summary>
    public static async Task<SpooledFile> SpoolAsync(Stream content, long maxSize, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdn_upload_{Ids.New():N}");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[FileTypes.HeaderLength];
        var headerLength = 0;
        long size = 0;
        var buffer = new byte[81920];
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                size += read;
                if (size > maxSize)
                {
                    return new SpooledFile(path, string.Empty, size, FileTypes.Other) { TooLarge = true };
                }

                if (headerLength < header.Length)
                {
                    var take = Math.Min(read, header.Length - headerLength);
                    buffer.AsSpan(0, take).CopyTo(header.AsSpan(headerLength));
                    headerLength += take;
                }

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        return new SpooledFile(path, Convert.ToHexStringLower(hash.GetHashAndReset()), size, FileTypes.Detect(header.AsSpan(0, headerLength)) ?? FileTypes.Other);
    }

    /// <summary>The stored file for the content, writing the blob only when the tenant does not have it yet.</summary>
    public async Task<StoredFile> StoreAsync(SpooledFile file, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        for (var attempt = 0; ; attempt++)
        {
            var stored = await db.StoredFiles.FirstOrDefaultAsync(f => f.Sha256 == file.Sha256, ct);
            if (stored is not null)
            {
                stored.LastUsedAt = now;
                await db.SaveChangesAsync(ct);
                if (!await blobs.ExistsAsync(stored.BlobKey, ct))
                {
                    await WriteBlobAsync(stored, file, ct);
                }

                return stored;
            }

            stored = new StoredFile
            {
                Id = Ids.New(),
                Sha256 = file.Sha256,
                Size = file.Size,
                MediaType = file.MediaType,
                CreatedAt = now,
                LastUsedAt = now,
            };
            db.StoredFiles.Add(stored);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Stored concurrently (unique tenant + hash); use that row.
                db.ChangeTracker.Clear();
                continue;
            }

            await WriteBlobAsync(stored, file, ct);
            return stored;
        }
    }

    private async Task WriteBlobAsync(StoredFile stored, SpooledFile file, CancellationToken ct)
    {
        await using var content = file.OpenRead();
        await blobs.WriteAsync(stored.BlobKey, content, ct);
    }
}
