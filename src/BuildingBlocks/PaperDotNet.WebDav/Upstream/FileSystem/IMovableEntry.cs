#nullable enable // PaperDotNet: vendored files are generated code for the analyzers (ADR-0047).
// PaperDotNet addition (ADR-0047): MOVE keeps the entry (identity, versions, permissions) and lets the file system
// decide what replacing an existing target means.

using System.Threading;
using System.Threading.Tasks;

namespace FubarDev.WebDavServer.FileSystem
{
    /// <summary>
    /// An entry that moves itself (a collection with its children) inside its file system, keeping its identity.
    /// Without it, MOVE re-creates the entry at the target and deletes the source.
    /// </summary>
    public interface IMovableEntry : IEntry
    {
        /// <summary>
        /// Moves this entry into <paramref name="collection"/> as <paramref name="name"/>. <paramref name="replaced"/> is
        /// the entry already at the target when the request allows overwriting it (<c>Overwrite: T</c>), otherwise null;
        /// the file system replaces it (e.g. as a new version of it) or removes it first.
        /// </summary>
        Task MoveToAsync(ICollection collection, string name, IEntry? replaced, CancellationToken cancellationToken);
    }
}
