using System.Globalization;
using System.Security.Principal;
using System.Xml.Linq;
using FubarDev.WebDavServer;
using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Models;
using FubarDev.WebDavServer.Props;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Generic;
using FubarDev.WebDavServer.Props.Live;
using FubarDev.WebDavServer.Props.Store;

namespace WebDavPoc;

/// <summary>One file system per request (scoped): the listing cache lives as long as the request.</summary>
public sealed class PocFileSystemFactory(
    PocModel model,
    PocSettings settings,
    IPathTraversalEngine engine,
    IHttpContextAccessor http,
    ILockManager? lockManager = null,
    IPropertyStoreFactory? propertyStoreFactory = null) : IFileSystemFactory
{
    public IFileSystem CreateFileSystem(ICollection? mountPoint, IPrincipal principal) =>
        new PocFileSystem(model, settings, engine, http, lockManager, propertyStoreFactory);
}

public sealed class PocFileSystem : IFileSystem
{
    private readonly IPathTraversalEngine engine;
    private readonly Dictionary<Node, IReadOnlyList<(Node Node, string Name)>> listings = [];

    public PocFileSystem(PocModel model, PocSettings settings, IPathTraversalEngine engine, IHttpContextAccessor http,
        ILockManager? lockManager, IPropertyStoreFactory? propertyStoreFactory)
    {
        Model = model;
        Settings = settings;
        Http = http;
        this.engine = engine;
        LockManager = lockManager;
        Root = new AsyncLazy<ICollection>(() => Task.FromResult<ICollection>(new FCollection(this, null, model.Root, new Uri(string.Empty, UriKind.Relative), string.Empty)));
        PropertyStore = propertyStoreFactory?.Create(this);
    }

    public PocModel Model { get; }
    public PocSettings Settings { get; }
    public IHttpContextAccessor Http { get; }
    public AsyncLazy<ICollection> Root { get; }
    public bool SupportsRangedRead => true;
    public IPropertyStore? PropertyStore { get; }
    public ILockManager? LockManager { get; }

    public Task<SelectionResult> SelectAsync(string path, CancellationToken ct) => engine.TraverseAsync(this, path, ct);

    public IReadOnlyList<(Node Node, string Name)> Children(Node node)
    {
        if (!listings.TryGetValue(node, out var list))
        {
            var visible = node.Children.Where(c => c.Kind switch
            {
                NodeKind.List => false,
                NodeKind.File => c.Content is not null,
                _ => true,
            });
            list = DavNames.Assign(visible).Take(Settings.MaxFolderEntries).ToList();
            listings[node] = list;
        }

        return list;
    }

    public void Invalidate() => listings.Clear();

    public IEntry Wrap(FCollection parent, Node node, string name) => node.IsCollection
        ? new FCollection(this, parent, node, parent.Path.AppendDirectory(name), name)
        : new FDocument(this, parent, node, parent.Path.Append(name, false), name);

    public void Header(string name, string value) => Http.HttpContext?.Response.Headers.Append(name, value);

    public void EnsureWritable(Node node)
    {
        if (Settings.ReadOnly || !node.Writable)
        {
            throw new WebDavException(WebDavStatusCode.Forbidden);
        }
    }
}

public abstract class FEntry(PocFileSystem fileSystem, FCollection? parent, Node node, Uri path, string name) : IEntityTagEntry
{
    public PocFileSystem Fs { get; } = fileSystem;
    public Node Node { get; } = node;
    public string Name { get; } = name;
    public IFileSystem FileSystem => Fs;
    public ICollection? Parent => parent;
    public FCollection? ParentCollection => parent;
    public Uri Path { get; } = path;

    public EntityTag ETag => new(false, Node.IsCollection
        ? $"{Node.Id:N}-{Node.Updated.UtcTicks}"
        : $"{Node.Sha256[..16]}-{Node.Version}");

    public Task<EntityTag> UpdateETagAsync(CancellationToken cancellationToken) => Task.FromResult(ETag);

    public async Task<DeleteResult> DeleteAsync(CancellationToken cancellationToken)
    {
        Fs.EnsureWritable(Node);
        lock (Fs.Model.Gate)
        {
            // The item store refuses non-empty folders: delete children first, then the folder (all to the recycle bin).
            Recycle(Node);
            Fs.Invalidate();
        }

        if (Fs.PropertyStore is { } store)
        {
            await store.RemoveAsync(this, cancellationToken);
        }

        return new DeleteResult(WebDavStatusCode.OK, null);
    }

    private void Recycle(Node node)
    {
        foreach (var child in node.Children.ToList())
        {
            Recycle(child);
        }

        node.Parent?.Children.Remove(node);
        Fs.Model.RecycleBin.Add(node);
    }

    public virtual IEnumerable<IUntypedReadableProperty> GetLiveProperties()
    {
        yield return new LastModifiedProperty(Node.Updated.UtcDateTime, (_, _) => Task.CompletedTask);
        yield return new CreationDateProperty(Node.Created, (_, _) => Task.CompletedTask);
        yield return new GetETagProperty(FileSystem.PropertyStore, this);

        // Windows Mini-Redirector properties: readable, writes accepted and ignored.
        var attributes = Node.IsCollection ? 0x10 : Fs.Settings.ReadOnly || !Node.Writable ? 0x01 : 0x20;
        yield return new LiveStringProperty(Ms("Win32FileAttributes"), attributes.ToString("X8", CultureInfo.InvariantCulture));
        foreach (var time in new[] { "Win32CreationTime", "Win32LastAccessTime", "Win32LastModifiedTime" })
        {
            yield return new LiveStringProperty(Ms(time), Node.Updated.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public static XName Ms(string name) => XName.Get(name, "urn:schemas-microsoft-com:");
}

public sealed class FCollection(PocFileSystem fileSystem, FCollection? parent, Node node, Uri path, string name)
    : FEntry(fileSystem, parent, node, path, name), ICollection
#if FOLDER_MOVE
    , IMovableCollection
#endif
{
#if FOLDER_MOVE
    /// <summary>With fubar-folder-move.patch: a folder moves as one item (id, permissions, values kept).</summary>
    public Task<ICollection> MoveToAsync(ICollection collection, string name, CancellationToken cancellationToken)
    {
        Fs.Header("X-Poc-MoveItemAsync", "called");
        Fs.EnsureWritable(Node);
        var target = (FCollection)collection;
        if (!target.AcceptsItems || !CanHoldItems)
        {
            throw new WebDavException(WebDavStatusCode.Forbidden);
        }

        lock (Fs.Model.Gate)
        {
            ParentCollection!.Node.Children.Remove(Node);
            Node.Title = name;
            Node.Updated = DateTimeOffset.UtcNow;
            target.Node.Add(Node);
            Fs.Invalidate();
            return Task.FromResult<ICollection>(new FCollection(Fs, target, Node, target.Path.AppendDirectory(name), name));
        }
    }
#endif

    private bool CanHoldItems => Node.Kind is NodeKind.Library or NodeKind.Folder;

    public override IEnumerable<IUntypedReadableProperty> GetLiveProperties()
    {
        foreach (var property in base.GetLiveProperties())
        {
            yield return property;
        }

        yield return new LiveStringProperty(XName.Get("quota-available-bytes", "DAV:"), "107374182400");
        yield return new LiveStringProperty(XName.Get("quota-used-bytes", "DAV:"), "1048576");
    }

    public Task<IEntry?> GetChildAsync(string name, CancellationToken ct)
    {
        lock (Fs.Model.Gate)
        {
            var children = Fs.Children(Node);
            var match = children.FirstOrDefault(c => c.Name == name);
            if (match.Node is null)
            {
                match = children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(match.Node is null ? null : Fs.Wrap(this, match.Node, match.Name));
        }
    }

    public Task<IReadOnlyCollection<IEntry>> GetChildrenAsync(CancellationToken ct)
    {
        lock (Fs.Model.Gate)
        {
            IReadOnlyCollection<IEntry> result = Fs.Children(Node).Select(c => Fs.Wrap(this, c.Node, c.Name)).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IDocument> CreateDocumentAsync(string name, CancellationToken ct)
    {
        if (Fs.Settings.ReadOnly || !CanHoldItems)
        {
            throw new WebDavException(WebDavStatusCode.Forbidden);
        }

        RefuseExisting(name);

        lock (Fs.Model.Gate)
        {
            var file = Node.Add(new Node
            {
                Kind = NodeKind.File,
                Title = DavNames.TitleFromName(name, isFile: true),
                FileName = name,
                MediaType = MediaTypes.FromName(name),
            });
            file.Content = [];
            Fs.Invalidate();
            Fs.Header("X-Poc-Transient", DavNames.IsTransient(name) ? "true" : "false");
            return Task.FromResult<IDocument>(new FDocument(Fs, this, file, Path.Append(name, false), name));
        }
    }

    public Task<ICollection> CreateCollectionAsync(string name, CancellationToken ct)
    {
        if (Fs.Settings.ReadOnly || !CanHoldItems)
        {
            throw new WebDavException(WebDavStatusCode.Forbidden);
        }

        RefuseExisting(name);

        lock (Fs.Model.Gate)
        {
            var folder = Node.Add(new Node { Kind = NodeKind.Folder, Title = name });
            Fs.Invalidate();
            return Task.FromResult<ICollection>(new FCollection(Fs, this, folder, Path.AppendDirectory(name), name));
        }
    }

    public bool AcceptsItems => CanHoldItems;

    /// <summary>The traversal lets MKCOL "name/" through when "name" is a document: refuse it here (405).</summary>
    private void RefuseExisting(string name)
    {
        lock (Fs.Model.Gate)
        {
            if (Fs.Children(Node).Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WebDavException(WebDavStatusCode.MethodNotAllowed);
            }
        }
    }
}

public sealed class FDocument(PocFileSystem fileSystem, FCollection parent, Node node, Uri path, string name)
    : FEntry(fileSystem, parent, node, path, name), IDocument
{
    public long Length => Node.Content?.LongLength ?? 0;

    public override IEnumerable<IUntypedReadableProperty> GetLiveProperties()
    {
        foreach (var property in base.GetLiveProperties())
        {
            yield return property;
        }

        yield return new ContentLengthProperty(Length);
    }

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream(Node.Content ?? [], writable: false));

    public Task<Stream> CreateAsync(CancellationToken cancellationToken)
    {
        Fs.EnsureWritable(Node);
        return Task.FromResult<Stream>(new CommitStream(this, []));
    }

    public Task<Stream> OpenWriteAsync(long position, CancellationToken cancellationToken)
    {
        Fs.EnsureWritable(Node);
        var stream = new CommitStream(this, Node.Content ?? []) { Position = position };
        return Task.FromResult<Stream>(stream);
    }

    public Task<IDocument> CopyToAsync(ICollection collection, string name, CancellationToken cancellationToken)
    {
        Fs.Header("X-Poc-CopyAsync", Node.Id.ToString());
        var target = (FCollection)collection;
        Fs.EnsureWritable(target.Node);
        lock (Fs.Model.Gate)
        {
            var existing = Fs.Children(target.Node).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing.Node is not null)
            {
                target.Node.Children.Remove(existing.Node);
                Fs.Model.RecycleBin.Add(existing.Node);
            }

            var copy = target.Node.Add(new Node { Kind = NodeKind.File, Title = DavNames.TitleFromName(name, true), FileName = name, MediaType = Node.MediaType });
            copy.SetContent(Node.Content ?? [], newVersion: true);
            Fs.Invalidate();
            return Task.FromResult<IDocument>(new FDocument(Fs, target, copy, target.Path.Append(name, false), name));
        }
    }

    public Task<IDocument> MoveToAsync(ICollection collection, string name, CancellationToken cancellationToken)
    {
        Fs.Header("X-Poc-MoveItemAsync", "called");
        Fs.EnsureWritable(Node);
        var target = (FCollection)collection;
        if (!target.AcceptsItems)
        {
            throw new WebDavException(WebDavStatusCode.Forbidden);
        }

        lock (Fs.Model.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var source = ParentCollection!.Node;

            // Same safe-save rules as the Dav.AspNetCore.Server spike (PocStore.MoveItemAsync).
            if (Fs.Model.RenamedAway.Remove((target.Node, name.ToLowerInvariant()), out var original)
                && now - original.At < TimeSpan.FromMinutes(2) && original.Node.Parent == target.Node)
            {
                original.Node.SetContent(Node.Content ?? [], newVersion: true);
                original.Node.Title = DavNames.TitleFromName(name, isFile: true);
                original.Node.FileName = name;
                source.Children.Remove(Node);
                Fs.Invalidate();
                Fs.Header("X-Poc-SafeSave", "merged into original");
                return Task.FromResult<IDocument>(new FDocument(Fs, target, original.Node, target.Path.Append(name, false), name));
            }

            if (DavNames.IsTransient(name) && !DavNames.IsTransient(Name))
            {
                Fs.Model.RenamedAway[(source, Name.ToLowerInvariant())] = (Node, now);
            }

            // The engine deletes an existing target before moving; drop it if it is still there.
            var existing = Fs.Children(target.Node).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing.Node is not null && existing.Node != Node)
            {
                target.Node.Children.Remove(existing.Node);
                Fs.Model.RecycleBin.Add(existing.Node);
            }

            source.Children.Remove(Node);
            Node.Title = DavNames.TitleFromName(name, isFile: true);
            Node.FileName = name;
            Node.Updated = now;
            target.Node.Add(Node);
            Fs.Invalidate();
            return Task.FromResult<IDocument>(new FDocument(Fs, target, Node, target.Path.Append(name, false), name));
        }
    }

    /// <summary>Buffers the upload and commits it as a version when the PUT handler disposes it.</summary>
    private sealed class CommitStream : MemoryStream
    {
        private readonly FDocument document;
        private readonly bool initialized;
        private bool committed;

        public CommitStream(FDocument document, byte[] initial)
        {
            this.document = document;
            base.Write(initial, 0, initial.Length);
            Position = 0;
            initialized = true;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            if (initialized && Length > document.Fs.Model.MaxFileSize)
            {
                throw new WebDavException((WebDavStatusCode)StatusCodes.Status413PayloadTooLarge);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !committed)
            {
                committed = true;
                var node = document.Node;
                lock (document.Fs.Model.Gate)
                {
                    var replaceEmpty = node.Content is { Length: 0 } && node.FileVersions <= 1;
                    node.SetContent(ToArray(), newVersion: !replaceEmpty);
                    if (replaceEmpty && node.FileVersions == 0)
                    {
                        node.FileVersions = 1;
                    }

                    document.Fs.Invalidate();
                }
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A computed live property; PROPPATCH is accepted and ignored (Explorer sets Win32 times after each PUT).</summary>
public sealed class LiveStringProperty(XName name, string value) : ILiveProperty, IUntypedWriteableProperty
{
    public XName Name { get; } = name;
    public string? Language => null;
    public IReadOnlyCollection<XName> AlternativeNames { get; } = [];
    public int Cost => 0;

    public Task<bool> IsValidAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<XElement> GetXmlValueAsync(CancellationToken ct) => Task.FromResult(new XElement(Name, value));

    public Task SetXmlValueAsync(XElement element, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>getcontenttype from the stored media type, not from the name.</summary>
public sealed class PocMimeTypeDetector : IMimeTypeDetector
{
    public bool TryDetect(IEntry entry, out string mimeType)
    {
        mimeType = entry is FDocument doc ? doc.Node.MediaType : "application/octet-stream";
        return entry is FDocument;
    }
}

public static class MediaTypes
{
    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider Provider = new();

    public static string FromName(string name) =>
        Provider.TryGetContentType(name, out var type) ? type : "application/octet-stream";
}
