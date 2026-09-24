namespace PaperDotNet.Persistence;

/// <summary>
/// A consistent snapshot of the whole database (PLT-12), one implementation per provider:
/// SQLite uses its online backup API, PostgreSQL <c>pg_dump</c> / <c>pg_restore</c>.
/// </summary>
public interface IDatabaseBackup
{
    /// <summary>File name of the snapshot inside a backup archive.</summary>
    string FileName { get; }

    /// <summary>Writes a snapshot of the database to <paramref name="file"/> while the application may keep running.</summary>
    Task BackupAsync(string file, CancellationToken cancellationToken);

    /// <summary>Replaces the database content with the snapshot in <paramref name="file"/> (application stopped).</summary>
    Task RestoreAsync(string file, CancellationToken cancellationToken);
}
