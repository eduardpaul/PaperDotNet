using System.Globalization;
using System.Security.Principal;
using System.Xml.Linq;
using FubarDev.WebDavServer;
using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Models;
using FubarDev.WebDavServer.Props;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Live;
using FubarDev.WebDavServer.Props.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Dav.Features;

/// <summary>
/// One file system per request, for the caller (scoped). Anonymous requests (only OPTIONS is allowed without
/// credentials) see an empty root.
/// </summary>
internal sealed class DavFileSystemFactory(IServiceProvider services) : IFileSystemFactory
{
    public IFileSystem CreateFileSystem(ICollection? mountPoint, IPrincipal principal) =>
        ActivatorUtilities.CreateInstance<DavFileSystem>(services, principal.Identity?.IsAuthenticated == true);
}

/// <summary>
/// The WebDAV tree over the SDK (ADR-0047): <c>/</c> lists the caller's Home and shared workspaces, a workspace its
/// document libraries, and libraries and folders their folders and files. Everything is read with the caller's
/// permissions through <see cref="IListItemStore"/>, <see cref="IWorkspaceAccess"/> and <see cref="IDocumentFileStore"/>.
/// Listings are cached for the request, so a PROPFIND reads each collection once.
/// </summary>
internal sealed partial class DavFileSystem : IFileSystem
{
    private readonly IPathTraversalEngine _engine;
    private readonly Dictionary<(Guid, Guid?), IReadOnlyList<IEntry>> _listings = [];

    public DavFileSystem(
        bool authenticated,
        IPathTraversalEngine engine,
        IPropertyStoreFactory propertyStores,
        IWorkspaceAccess workspaces,
        IListItemStore items,
        IDocumentFileStore documents,
        IOptions<DavOptions> options,
        ILogger<DavFileSystem> logger,
        ILockManager? lockManager = null)
    {
        _engine = engine;
        Authenticated = authenticated;
        Workspaces = workspaces;
        Items = items;
        Documents = documents;
        Options = options.Value;
        Logger = logger;
        LockManager = lockManager;
        RootCollection = new DavRoot(this);
        Root = new AsyncLazy<ICollection>(() => Task.FromResult<ICollection>(RootCollection));
        PropertyStore = propertyStores.Create(this);
    }

    public AsyncLazy<ICollection> Root { get; }

    public bool SupportsRangedRead => true;

    public IPropertyStore? PropertyStore { get; }

    public ILockManager? LockManager { get; }

    internal DavRoot RootCollection { get; }

    internal bool Authenticated { get; }

    internal IWorkspaceAccess Workspaces { get; }

    internal IListItemStore Items { get; }

    internal IDocumentFileStore Documents { get; }

    internal DavOptions Options { get; }

    internal ILogger Logger { get; }

    public Task<SelectionResult> SelectAsync(string path, CancellationToken ct) => _engine.TraverseAsync(this, path, ct);

    /// <summary>The children of a collection, listed once per request.</summary>
    internal async Task<IReadOnlyList<IEntry>> ListAsync(DavCollection collection, CancellationToken ct)
    {
        var key = collection.ListingKey;
        if (!_listings.TryGetValue(key, out var entries))
        {
            entries = await collection.LoadChildrenAsync(ct);
            _listings[key] = entries;
        }

        return entries;
    }

    internal void Invalidate() => _listings.Clear();

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebDAV listing of {Path} stopped at {Max} entries; the rest is not shown")]
    internal static partial void LogTruncated(ILogger logger, Uri path, int max);
}

/// <summary>Common behaviour: ETags from our data, live properties, read-only until writes are supported.</summary>
internal abstract class DavEntry(DavFileSystem fileSystem, DavCollection? parent, Uri path, string name) : IEntityTagEntry
{
    public string Name { get; } = name;

    public IFileSystem FileSystem => Dav;

    public ICollection? Parent => ParentCollection;

    public Uri Path { get; } = path;

    public abstract EntityTag ETag { get; }

    internal DavFileSystem Dav { get; } = fileSystem;

    internal DavCollection? ParentCollection { get; } = parent;

    internal abstract DateTimeOffset Created { get; }

    internal abstract DateTimeOffset Modified { get; }

    internal abstract bool IsCollection { get; }

    public Task<EntityTag> UpdateETagAsync(CancellationToken cancellationToken) => Task.FromResult(ETag);

    public virtual Task<DeleteResult> DeleteAsync(CancellationToken cancellationToken) => throw ReadOnly();

    public virtual IEnumerable<IUntypedReadableProperty> GetLiveProperties()
    {
        yield return new LastModifiedProperty(Modified.UtcDateTime, (_, _) => throw ReadOnly());
        yield return new CreationDateProperty(Created, (_, _) => throw ReadOnly());
        yield return new GetETagProperty(FileSystem.PropertyStore, this);

        // Windows Mini-Redirector properties: FILE_ATTRIBUTE_DIRECTORY, or FILE_ATTRIBUTE_READONLY for files.
        yield return new LiveStringProperty(DavXml.Win32FileAttributes, IsCollection ? "00000010" : "00000001");
        yield return new LiveStringProperty(DavXml.Win32CreationTime, Created.ToString("R", CultureInfo.InvariantCulture));
        yield return new LiveStringProperty(DavXml.Win32LastModifiedTime, Modified.ToString("R", CultureInfo.InvariantCulture));
        yield return new LiveStringProperty(DavXml.Win32LastAccessTime, Modified.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Writes are refused until the WebDAV mount supports them (ADR-0047, WD-3).</summary>
    internal static WebDavException ReadOnly() => new(WebDavStatusCode.Forbidden);
}

internal abstract class DavCollection(DavFileSystem fileSystem, DavCollection? parent, Uri path, string name)
    : DavEntry(fileSystem, parent, path, name), ICollection
{
    internal abstract (Guid, Guid?) ListingKey { get; }

    internal override bool IsCollection => true;

    public async Task<IEntry?> GetChildAsync(string name, CancellationToken ct)
    {
        var children = await Dav.ListAsync(this, ct);
        return children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            ?? children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyCollection<IEntry>> GetChildrenAsync(CancellationToken ct) => await Dav.ListAsync(this, ct);

    public virtual Task<IDocument> CreateDocumentAsync(string name, CancellationToken ct) => throw ReadOnly();

    public virtual Task<ICollection> CreateCollectionAsync(string name, CancellationToken ct) => throw ReadOnly();

    internal abstract Task<IReadOnlyList<IEntry>> LoadChildrenAsync(CancellationToken ct);

    protected Uri ChildPath(string name, bool collection) =>
        collection ? Path.AppendDirectory(name) : Path.Append(name, false);
}

/// <summary><c>/</c>: the caller's Home and the shared workspaces they can read.</summary>
internal sealed class DavRoot(DavFileSystem fileSystem) : DavCollection(fileSystem, null, new Uri(string.Empty, UriKind.Relative), string.Empty)
{
    public override EntityTag ETag => new(false, "root");

    internal override (Guid, Guid?) ListingKey => (Guid.Empty, null);

    internal override DateTimeOffset Created => DateTimeOffset.UnixEpoch;

    internal override DateTimeOffset Modified => DateTimeOffset.UnixEpoch;

    internal override async Task<IReadOnlyList<IEntry>> LoadChildrenAsync(CancellationToken ct)
    {
        if (!Dav.Authenticated)
        {
            return [];
        }

        var home = await Dav.Items.EnsureHomeAsync(ct);
        var memberships = await Dav.Workspaces.GetMyWorkspacesAsync(ct);
        var shared = await Dav.Workspaces.GetSharedNamesAsync(memberships.Select(m => m.WorkspaceId).ToList(), ct);
        var workspaces = shared.Where(s => s.Key != home.WorkspaceId).Select(s => (s.Key, Name: DavNames.Name(s.Value))).ToList();
        workspaces.Add((home.WorkspaceId, "Home"));
        return DavNames.Assign(workspaces, w => w.Key, w => w.Name)
            .Select(w => (IEntry)new DavWorkspace(Dav, this, w.Entry.Key, ChildPath(w.Name, true), w.Name))
            .ToList();
    }
}

/// <summary>A workspace: its document libraries.</summary>
internal sealed class DavWorkspace(DavFileSystem fileSystem, DavCollection parent, Guid workspaceId, Uri path, string name)
    : DavCollection(fileSystem, parent, path, name)
{
    public Guid WorkspaceId { get; } = workspaceId;

    public override EntityTag ETag => new(false, WorkspaceId.ToString("N"));

    internal override (Guid, Guid?) ListingKey => (WorkspaceId, null);

    internal override DateTimeOffset Created => DateTimeOffset.UnixEpoch;

    internal override DateTimeOffset Modified => DateTimeOffset.UnixEpoch;

    internal override async Task<IReadOnlyList<IEntry>> LoadChildrenAsync(CancellationToken ct)
    {
        var lists = await Dav.Items.GetListsAsync(WorkspaceId, null, ct);
        return DavNames.Assign(lists.Where(l => l.IsLibrary), l => l.Id, l => DavNames.Name(l.Name))
            .Select(l => (IEntry)new DavFolder(Dav, this, l.Entry, null, ChildPath(l.Name, true), l.Name))
            .ToList();
    }
}

/// <summary>A library (no folder item) or one of its folders: folders and files, as the caller may read them.</summary>
internal sealed class DavFolder(DavFileSystem fileSystem, DavCollection parent, ListData list, ListItemData? folder, Uri path, string name)
    : DavCollection(fileSystem, parent, path, name)
{
    public ListData List { get; } = list;

    /// <summary>The folder item; null for the library itself.</summary>
    public ListItemData? Folder { get; } = folder;

    public override EntityTag ETag => new(false, Folder is null ? List.Id.ToString("N") : $"{Folder.Id:N}-{Folder.Version}");

    internal override (Guid, Guid?) ListingKey => (List.Id, Folder?.Id);

    internal override DateTimeOffset Created => Folder?.CreatedAt ?? DateTimeOffset.UnixEpoch;

    internal override DateTimeOffset Modified => Folder?.UpdatedAt ?? DateTimeOffset.UnixEpoch;

    internal override async Task<IReadOnlyList<IEntry>> LoadChildrenAsync(CancellationToken ct)
    {
        var max = Dav.Options.MaxFolderEntries;
        var children = new List<ListItemData>();
        string? cursor = null;
        do
        {
            var (page, error) = await Dav.Items.ListChildrenAsync(List.WorkspaceId, List.Id, Folder?.Id,
                new ListItemQuery(Top: ListItemQuery.MaxTop, SkipToken: cursor), ct);
            if (page is null)
            {
                throw new InvalidOperationException(error ?? "The folder could not be listed.");
            }

            children.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null && children.Count < max);

        if (children.Count > max || cursor is not null)
        {
            DavFileSystem.LogTruncated(Dav.Logger, Path, max);
            children = children.Take(max).ToList();
        }

        var files = await Dav.Documents.GetCurrentAsync(children.Where(c => !c.IsFolder).Select(c => c.Id).ToList(), ct);
        var named = children
            .Where(c => c.IsFolder || files.ContainsKey(c.Id))
            .Select(c => (Item: c, File: c.IsFolder ? null : files[c.Id]));
        return DavNames.Assign(named, c => c.Item.Id, c => DavNames.Name(Title(c.Item), c.File is null ? string.Empty : DavNames.Extension(c.File.FileName, c.File.MediaType)))
            .Select(c => c.Entry.File is null
                ? (IEntry)new DavFolder(Dav, this, List, c.Entry.Item, ChildPath(c.Name, true), c.Name)
                : new DavDocument(Dav, this, c.Entry.Item, c.Entry.File, ChildPath(c.Name, false), c.Name))
            .ToList();
    }

    internal static string? Title(ListItemData item) =>
        item.Fields["title"] is { } title && title.GetValueKind() == System.Text.Json.JsonValueKind.String ? title.GetValue<string>() : null;
}

/// <summary>A document: its current file.</summary>
internal sealed class DavDocument(DavFileSystem fileSystem, DavFolder parent, ListItemData item, DocumentFile file, Uri path, string name)
    : DavEntry(fileSystem, parent, path, name), IDocument
{
    public ListItemData Item { get; } = item;

    public DocumentFile File { get; } = file;

    public long Length => File.Size;

    /// <summary>Changes with the content (hash) and with the item (version), never read from the content.</summary>
    public override EntityTag ETag => new(false, $"{File.Sha256[..16]}-{Item.Version}");

    internal override DateTimeOffset Created => Item.CreatedAt;

    internal override DateTimeOffset Modified => Item.UpdatedAt;

    internal override bool IsCollection => false;

    public override IEnumerable<IUntypedReadableProperty> GetLiveProperties()
    {
        foreach (var property in base.GetLiveProperties())
        {
            yield return property;
        }

        yield return new ContentLengthProperty(Length);
    }

    public async Task<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        await Dav.Documents.OpenVersionAsync(File.Id, cancellationToken) ?? throw new WebDavException(WebDavStatusCode.NotFound);

    public Task<Stream> CreateAsync(CancellationToken cancellationToken) => throw ReadOnly();

    public Task<Stream> OpenWriteAsync(long position, CancellationToken cancellationToken) => throw ReadOnly();

    public Task<IDocument> CopyToAsync(ICollection collection, string name, CancellationToken cancellationToken) => throw ReadOnly();

    public Task<IDocument> MoveToAsync(ICollection collection, string name, CancellationToken cancellationToken) => throw ReadOnly();
}

/// <summary>A computed live property; PROPPATCH of it is refused while the mount is read-only.</summary>
internal sealed class LiveStringProperty(XName name, string value) : ILiveProperty, IUntypedWriteableProperty
{
    public XName Name { get; } = name;

    public string? Language => null;

    public IReadOnlyCollection<XName> AlternativeNames { get; } = [];

    public int Cost => 0;

    public Task<bool> IsValidAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<XElement> GetXmlValueAsync(CancellationToken ct) => Task.FromResult(new XElement(Name, value));

    public Task SetXmlValueAsync(XElement element, CancellationToken ct) => throw DavEntry.ReadOnly();
}

/// <summary>The stored media type, not a guess from the name.</summary>
internal sealed class DavMimeTypeDetector : IMimeTypeDetector
{
    public bool TryDetect(IEntry entry, out string mimeType)
    {
        mimeType = entry is DavDocument document ? document.File.MediaType : "application/octet-stream";
        return entry is DavDocument;
    }
}

/// <summary>Dead (client-defined) properties are not stored (ADR-0047); ETags come from the entries.</summary>
internal sealed class EmptyPropertyStore(IDeadPropertyFactory deadProperties) : PropertyStoreBase(deadProperties)
{
    public override int Cost => 0;

    public override Task<IReadOnlyCollection<XElement>> GetAsync(IEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<XElement>>([]);

    public override Task SetAsync(IEntry entry, IEnumerable<XElement> properties, CancellationToken cancellationToken) =>
        throw DavEntry.ReadOnly();

    public override Task<IReadOnlyCollection<bool>> RemoveAsync(IEntry entry, IEnumerable<XName> names, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<bool>>(names.Select(_ => false).ToList());

    protected override Task<EntityTag> GetDeadETagAsync(IEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(new EntityTag(false));

    protected override Task<EntityTag> UpdateDeadETagAsync(IEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(new EntityTag(false));
}

internal sealed class EmptyPropertyStoreFactory(IDeadPropertyFactory deadProperties) : IPropertyStoreFactory
{
    public IPropertyStore Create(IFileSystem fileSystem) => new EmptyPropertyStore(deadProperties);
}

internal static class DavXml
{
    private static readonly XNamespace Microsoft = "urn:schemas-microsoft-com:";

    public static readonly XName Win32FileAttributes = Microsoft + "Win32FileAttributes";
    public static readonly XName Win32CreationTime = Microsoft + "Win32CreationTime";
    public static readonly XName Win32LastModifiedTime = Microsoft + "Win32LastModifiedTime";
    public static readonly XName Win32LastAccessTime = Microsoft + "Win32LastAccessTime";
}
