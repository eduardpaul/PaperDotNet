namespace PaperDotNet.Persistence;

/// <summary>
/// A consistent snapshot of the whole database (PLT-12), one implementation per database build: SQLite uses its online
/// backup API.
/// </summary>
public interface IDatabaseBackup
{
    /// <summary>The database of the build (<c>sqlite</c>); a backup restores only into the same one.</summary>
    string Provider { get; }

    /// <summary>File name of the snapshot inside a backup archive.</summary>
    string FileName { get; }

    /// <summary>Writes a snapshot of the database to <paramref name="file"/> while the application may keep running.</summary>
    Task BackupAsync(string file, CancellationToken cancellationToken);

    /// <summary>Replaces the database content with the snapshot in <paramref name="file"/> (server stopped).</summary>
    Task RestoreAsync(string file, CancellationToken cancellationToken);
}
