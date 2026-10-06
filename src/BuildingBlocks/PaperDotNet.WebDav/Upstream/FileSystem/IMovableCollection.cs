#nullable enable // PaperDotNet: vendored files are generated code for the analyzers (ADR-0047).
// PaperDotNet addition (ADR-0047): folder MOVE keeps the folder.

using System.Threading;
using System.Threading.Tasks;

namespace FubarDev.WebDavServer.FileSystem
{
    /// <summary>
    /// A collection that can move itself (with its children) inside its file system, keeping its identity.
    /// Without it, MOVE re-creates the collection at the target and moves the children one by one.
    /// </summary>
    public interface IMovableCollection : ICollection
    {
        /// <summary>Moves this collection into <paramref name="collection"/> as <paramref name="name"/>.</summary>
        Task<ICollection> MoveToAsync(ICollection collection, string name, CancellationToken cancellationToken);
    }
}
