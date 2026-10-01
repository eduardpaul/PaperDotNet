using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;
using PaperDotNet.Persistence.Sqlite;
using PaperDotNet.Storage;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Host.Backup;

public sealed record BackupResult(string Path, int Files, long Bytes);

/// <summary>What a backup archive holds; the first entry of the archive.</summary>
public sealed record BackupManifest(string Format, DateTimeOffset CreatedAt, string Provider, string Database, int Files);

/// <summary>
/// Backup and restore of a whole installation (PLT-12): one <c>.tar.gz</c> with <c>manifest.json</c>, a database
/// snapshot (<see cref="IDatabaseBackup"/>) and the stored files under <c>blobs/</c>, without temporary files and
/// cached page images. The snapshot is taken first: stored files are immutable and content-addressed, so every file
/// it refers to exists when the files are copied, and a backup is safe while the server runs.
/// </summary>
public sealed class BackupService(IDatabaseBackup database, IBlobStore blobs, ITenantDirectory tenants, IServiceProvider services, TimeProvider time)
{
    public const string Format = "paperdotnet-backup/1";

    private const string ManifestName = "manifest.json";
    private const string BlobFolder = "blobs/";

    public async Task<BackupResult> BackupAsync(string path, CancellationToken ct)
    {
        var root = LocalRoot();
        path = Path.GetFullPath(path);
        var snapshot = Path.Combine(Path.GetTempPath(), $"pdn_snapshot_{Ids.New():N}");
        var partial = path + ".partial";
        try
        {
            await database.BackupAsync(snapshot, ct);
            var files = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
                    .Where(IsStoredFile)
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : [];
            var manifest = new BackupManifest(Format, time.GetUtcNow(), database.Provider, database.FileName, files.Count);

            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                using var manifestStream = new MemoryStream(Write(manifest));
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestName) { DataStream = manifestStream }, ct);
                await tar.WriteEntryAsync(snapshot, database.FileName, ct);
                foreach (var file in files)
                {
                    var source = Path.Combine(root, file);
                    if (File.Exists(source))
                    {
                        await tar.WriteEntryAsync(source, BlobFolder + file, ct);
                    }
                }
            }

            File.Move(partial, path, overwrite: true);
            return new BackupResult(path, files.Count, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(snapshot);
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Restores a backup into this installation (server stopped): refuses to replace existing tenants unless
    /// <paramref name="force"/>, restores the database, replaces the stored files and migrates.
    /// </summary>
    public async Task<BackupManifest> RestoreAsync(string path, bool force, CancellationToken ct)
    {
        var root = LocalRoot();
        await using var input = File.OpenRead(path);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);

        var first = await tar.GetNextEntryAsync(copyData: false, ct);
        if (first is not { Name: ManifestName, DataStream: { } manifestData } || await ReadAsync(manifestData, ct) is not { Format: Format } manifest)
        {
            throw new InvalidOperationException($"'{path}' is not a PaperDotNet backup.");
        }

        if (!string.Equals(manifest.Provider, database.Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The backup is of a {manifest.Provider} database; this installation uses {database.Provider}.");
        }

        if (!force && (await tenants.ListAsync(ct)).Count > 0)
        {
            throw new InvalidOperationException("This installation already has data. Restore with --force to replace it.");
        }

        var restoredDatabase = false;
        var clearedFiles = false;
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                continue;
            }

            if (entry.Name == manifest.Database)
            {
                var snapshot = Path.Combine(Path.GetTempPath(), $"pdn_restore_{Ids.New():N}");
                try
                {
                    await entry.ExtractToFileAsync(snapshot, overwrite: false, ct);
                    await database.RestoreAsync(snapshot, ct);
                    restoredDatabase = true;
                }
                finally
                {
                    File.Delete(snapshot);
                }
            }
            else if (entry.Name.StartsWith(BlobFolder, StringComparison.Ordinal) && IsStoredFile(entry.Name[BlobFolder.Length..]))
            {
                if (!clearedFiles)
                {
                    ClearStoredFiles(root);
                    clearedFiles = true;
                }

                // The blob store checks the key, so an entry cannot write outside the store.
                await blobs.WriteAsync(entry.Name[BlobFolder.Length..], entry.DataStream, ct);
            }
        }

        if (!restoredDatabase)
        {
            throw new InvalidOperationException($"'{path}' has no database snapshot.");
        }

        if (!clearedFiles)
        {
            ClearStoredFiles(root);
        }

        await SqliteDatabase.MigrateAsync(services, ct);
        return manifest;
    }

    /// <summary>Stored files only: no temporary files, no cached page images (they are rendered again).</summary>
    private static bool IsStoredFile(string key) =>
        !key.StartsWith(".tmp/", StringComparison.Ordinal) && !key.Contains("/renders/", StringComparison.Ordinal);

    private static void ClearStoredFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory) != ".tmp")
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        foreach (var file in Directory.EnumerateFiles(root))
        {
            File.Delete(file);
        }
    }

    private string LocalRoot() => blobs is LocalBlobStore local
        ? local.Root
        : throw new NotSupportedException("Backups include stored files on the local file system only; back up other blob stores with their own tools.");

    /// <summary>The manifest as JSON, written by hand (no reflection under Native AOT).</summary>
    private static byte[] Write(BackupManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", manifest.Format);
            json.WriteString("createdAt", manifest.CreatedAt);
            json.WriteString("provider", manifest.Provider);
            json.WriteString("database", manifest.Database);
            json.WriteNumber("files", manifest.Files);
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static async Task<BackupManifest?> ReadAsync(Stream data, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(data, cancellationToken: ct);
            var root = document.RootElement;
            return new BackupManifest(
                root.GetProperty("format").GetString()!, root.GetProperty("createdAt").GetDateTimeOffset(), root.GetProperty("provider").GetString()!,
                root.GetProperty("database").GetString()!, root.GetProperty("files").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
