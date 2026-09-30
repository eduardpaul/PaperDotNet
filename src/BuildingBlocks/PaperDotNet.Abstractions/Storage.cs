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
