namespace PaperDotNet.Abstractions;

/// <summary>
/// Binary content storage (DOC-15): local disk by default, S3-compatible later. Keys are
/// relative paths of lower-case letters, digits, <c>-</c>, <c>_</c> and <c>/</c>; callers choose
/// the layout (e.g. content-addressed under a tenant prefix). Writes are atomic: a key either
/// holds the complete content or does not exist.
/// </summary>
public interface IBlobStore
{
    /// <summary>Stores <paramref name="content"/> under <paramref name="key"/>, replacing existing content.</summary>
    Task WriteAsync(string key, Stream content, CancellationToken cancellationToken);

    /// <summary>A readable, seekable stream, or null when the key does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes the content; missing keys are ignored.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// A folder a backup (PLT-12) copies as it is, under <c>folders/{Name}/</c>: data kept outside the database and the
/// blob store, such as the zvec search collections (ADR-0044). While frozen, its owner has closed its files and holds
/// new users back, so the copy is consistent and a restore can replace the files.
/// </summary>
public interface IBackupFolder
{
    /// <summary>A short name, e.g. <c>search-zvec</c>.</summary>
    string Name { get; }

    /// <summary>The folder (it may not exist).</summary>
    string Path { get; }

    /// <summary>Closes the folder's files and holds new users until the returned handle is disposed.</summary>
    Task<IAsyncDisposable> FreezeAsync(CancellationToken cancellationToken);
}
