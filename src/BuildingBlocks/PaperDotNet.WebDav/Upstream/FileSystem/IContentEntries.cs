#nullable enable // PaperDotNet: vendored files are generated code for the analyzers (ADR-0047).
// PaperDotNet addition (ADR-0047): stores that create and replace a file in one step (an upload), instead of
// creating an empty document and writing a stream into it.

using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FubarDev.WebDavServer.FileSystem
{
    /// <summary>A collection that creates documents with their content (PUT of a new name).</summary>
    public interface IContentCollection : ICollection
    {
        /// <summary>Creates the document <paramref name="name"/> with <paramref name="content"/>.</summary>
        Task<IDocument> CreateDocumentAsync(string name, Stream content, CancellationToken cancellationToken);
    }

    /// <summary>A document whose content is replaced in one step (PUT of an existing name).</summary>
    public interface IContentDocument : IDocument
    {
        /// <summary>Replaces the content with <paramref name="content"/>.</summary>
        Task ReplaceAsync(Stream content, CancellationToken cancellationToken);
    }
}
