using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;
using PaperDotNet.Storage;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Host.Backup;

public sealed record BackupResult(string Path, int Files, long Bytes);

/// <summary>What a backup archive holds; the first entry of the archive.</summary>
public sealed record BackupManifest(string Format, DateTimeOffset CreatedAt, string Provider, string Database, int Files);

/// <summary>
/// Backup and restore of a whole installation (PLT-12): one <c>.tar.gz</c> with <c>manifest.json</c>, a database
/// snapshot (<see cref="IDatabaseBackup"/>), the stored files under <c>blobs/</c>, without temporary files and
/// cached page images, and the <see cref="IBackupFolder"/>s under <c>folders/{name}/</c> (copied while frozen). The
/// snapshot is taken first: stored files are immutable and content-addressed, so every file it refers to exists when
/// the files are copied, and a backup is safe while the server runs.
/// </summary>
public sealed class BackupService(
    IDatabaseBackup database,
    IDatabaseProvider provider,
    DatabaseMigrator migrator,
    IBlobStore blobs,
    ITenantDirectory tenants,
    IEnumerable<IBackupFolder> folders,
    TimeProvider time)
{
    public const string Format = "paperdotnet-backup/1";

    private const string ManifestName = "manifest.json";
    private const string BlobFolder = "blobs/";
    private const string FoldersPrefix = "folders/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<BackupResult> BackupAsync(string path, CancellationToken ct)
    {
        var root = LocalRoot();
        path = Path.GetFullPath(path);
        var snapshot = Path.Combine(Path.GetTempPath(), $"pdn_snapshot_{Guid.NewGuid():N}");
        var partial = path + ".partial";
        try
        {
            await database.BackupAsync(snapshot, ct);
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(IsStoredFile)
                .Order(StringComparer.Ordinal)
                .ToList();
            var manifest = new BackupManifest(Format, time.GetUtcNow(), provider.Name, database.FileName, files.Count);

            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                using var manifestStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
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

                foreach (var folder in folders)
                {
                    await using var frozen = await folder.FreezeAsync(ct);
                    if (!Directory.Exists(folder.Path))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    {
                        var name = Path.GetRelativePath(folder.Path, file).Replace(Path.DirectorySeparatorChar, '/');
                        await tar.WriteEntryAsync(file, $"{FoldersPrefix}{folder.Name}/{name}", ct);
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
        if (first is not { Name: ManifestName, DataStream: { } manifestData }
            || await JsonSerializer.DeserializeAsync<BackupManifest>(manifestData, Json, ct) is not { } manifest
            || manifest.Format != Format)
        {
            throw new InvalidOperationException($"'{path}' is not a PaperDotNet backup.");
        }

        if (!string.Equals(manifest.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The backup is of a {manifest.Provider} database; this installation uses {provider.Name}.");
        }

        if (!force && (await tenants.ListAsync(ct)).Count > 0)
        {
            throw new InvalidOperationException("This installation already has data. Restore with --force to replace it.");
        }

        var restoredDatabase = false;
        var clearedFiles = false;
        var byName = folders.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var frozen = new Dictionary<string, IAsyncDisposable>(StringComparer.Ordinal);
        try
        {
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                // Folders keep their empty files (zvec's LOCK files); the database and blobs never have any.
                if (FolderEntry(entry.Name, byName) is { } target)
                {
                    if (!frozen.ContainsKey(target.Folder.Name))
                    {
                        frozen[target.Folder.Name] = await ClearAsync(target.Folder, ct);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target.File)!);
                    await using var file = File.Create(target.File);
                    if (entry.DataStream is { } content)
                    {
                        await content.CopyToAsync(file, ct);
                    }

                    continue;
                }

                if (entry.DataStream is null)
                {
                    continue;
                }

                if (entry.Name == manifest.Database)
                {
                    var snapshot = Path.Combine(Path.GetTempPath(), $"pdn_restore_{Guid.NewGuid():N}");
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

            // A folder the backup does not have would not match the restored database: it is emptied (a reindex fills it).
            foreach (var folder in byName.Values.Where(f => !frozen.ContainsKey(f.Name)))
            {
                frozen[folder.Name] = await ClearAsync(folder, ct);
            }
        }
        finally
        {
            foreach (var handle in frozen.Values)
            {
                await handle.DisposeAsync();
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

        await migrator.MigrateAsync(ct);
        return manifest;
    }

    private static async Task<IAsyncDisposable> ClearAsync(IBackupFolder folder, CancellationToken ct)
    {
        var handle = await folder.FreezeAsync(ct);
        if (Directory.Exists(folder.Path))
        {
            Directory.Delete(folder.Path, recursive: true);
        }

        return handle;
    }

    /// <summary>The folder and file an entry restores to; null for other entries and for names that leave the folder.</summary>
    private static (IBackupFolder Folder, string File)? FolderEntry(string name, Dictionary<string, IBackupFolder> folders)
    {
        if (!name.StartsWith(FoldersPrefix, StringComparison.Ordinal) || name[FoldersPrefix.Length..].Split('/', 2) is not [var folderName, var relative]
            || !folders.TryGetValue(folderName, out var folder) || relative.Length == 0)
        {
            return null;
        }

        var root = Path.GetFullPath(folder.Path) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(root, relative));
        return file.StartsWith(root, StringComparison.Ordinal) ? (folder, file) : null;
    }

    /// <summary>Stored files only: no temporary files, no cached page images (they are rendered again).</summary>
    private static bool IsStoredFile(string key) =>
        !key.StartsWith(".tmp/", StringComparison.Ordinal) && !key.Contains("/renders/", StringComparison.Ordinal);

    private static void ClearStoredFiles(string root)
    {
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
}
