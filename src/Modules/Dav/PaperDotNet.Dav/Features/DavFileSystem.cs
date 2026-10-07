using System.Globalization;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json.Nodes;
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
using PaperDotNet.Abstractions;
using PaperDotNet.Dav.Data;
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
        ActivatorUtilities.CreateInstance<DavFileSystem>(services, principal.Identity?.IsAuthenticated == true, CanWrite(principal));

    /// <summary>
    /// Whether the token may write (API tokens and limited OAuth tokens carry their scopes), so that files show as
    /// read-only in Explorer for read-only tokens. Item permissions are checked per file.
    /// </summary>
    internal static bool CanWrite(IPrincipal principal) =>
        principal is ClaimsPrincipal user
        && (!user.HasClaim(c => c.Type is PaperDotNetClaims.TokenId or PaperDotNetClaims.ScopeLimited)
            || DavModule.WriteScopes.All(scope => user.HasClaim(PaperDotNetClaims.TokenScope, scope)));
}

/// <summary>
/// The WebDAV tree over the SDK (ADR-0047): <c>/</c> lists the caller's Home and shared workspaces, a workspace its
/// document libraries, and libraries and folders their folders and files. Everything is read and written with the
/// caller's permissions through <see cref="IListItemStore"/>, <see cref="IWorkspaceAccess"/>,
/// <see cref="IDocumentFileStore"/> and <see cref="IDocumentUploads"/>, so writes are uploads, versions, folders, moves
/// and deletes with the same rules, events and workflows as the API. Listings are cached for the request.
/// </summary>
internal sealed partial class DavFileSystem : IFileSystem
{
    private readonly IPathTraversalEngine _engine;
    private readonly Dictionary<(Guid, Guid?), IReadOnlyList<IEntry>> _listings = [];

    public DavFileSystem(
        bool authenticated,
        bool canWrite,
        IPathTraversalEngine engine,
        IPropertyStoreFactory propertyStores,
        IWorkspaceAccess workspaces,
        IListItemStore items,
        IDocumentFileStore documents,
        IDocumentUploads uploads,
        DavTransientStore transients,
        IOptions<DavOptions> options,
        ILogger<DavFileSystem> logger,
        ILockManager? lockManager = null)
    {
        _engine = engine;
        Authenticated = authenticated;
        CanWrite = canWrite;
        Workspaces = workspaces;
        Items = items;
        Documents = documents;
        Uploads = uploads;
        Transients = transients;
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

    /// <summary>The token has the write scopes (<see cref="DavFileSystemFactory.CanWrite"/>).</summary>
    internal bool CanWrite { get; }

    internal IWorkspaceAccess Workspaces { get; }

    internal IListItemStore Items { get; }

    internal IDocumentFileStore Documents { get; }

    internal IDocumentUploads Uploads { get; }

    internal DavTransientStore Transients { get; }

    internal DavOptions Options { get; }

    internal ILogger Logger { get; }

    public Task<SelectionResult> SelectAsync(string path, CancellationToken ct) => _engine.TraverseAsync(this, path, ct);

    /// <summary>The children of a collection, listed once per request (until a write changes the tree).</summary>
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

    /// <summary>
    /// Moves and renames an item (a document or folder) of <paramref name="source"/> into <paramref name="target"/>
    /// with <paramref name="title"/>: in the same library with <c>MoveAsync</c>, into another library with
    /// <c>MoveToAsync</c> (keeps identity, versions and links), then the title.
    /// </summary>
    internal async Task MoveItemAsync(ListItemData item, DavFolder source, DavFolder target, string title, CancellationToken ct)
    {
        var moved = item;
        if (target.List.Id != source.List.Id)
        {
            moved = Check(await Items.MoveToAsync(item.Id, target.List.WorkspaceId, target.List.Id, target.Folder?.Id, null, ct)).Item ?? item;
        }
        else if (target.ParentId != source.ParentId)
        {
            moved = Check(await Items.MoveAsync(source.List.WorkspaceId, source.List.Id, item.Id, target.Folder?.Id, ct)).Item ?? item;
        }

        if (!string.Equals(DavFolder.Title(moved), title, StringComparison.Ordinal))
        {
            Check(await Items.UpdateAsync(target.List.WorkspaceId, target.List.Id, item.Id, new JsonObject { ["title"] = title }, null, ct));
        }

        Invalidate();
    }

    internal static ListItemResult Check(ListItemResult result) => result.Succeeded ? result : throw Error(result);

    internal static DocumentWriteResult Check(DocumentWriteResult result) => result.Succeeded ? result : throw Error(result);

    internal static WebDavException Error(ListItemResult result) => new(result.Status switch
    {
        ListItemStatus.NotFound => WebDavStatusCode.NotFound,
        ListItemStatus.VersionMismatch => WebDavStatusCode.PreconditionFailed,
        ListItemStatus.Rejected => WebDavStatusCode.Conflict,
        _ => WebDavStatusCode.Forbidden,
    }, result.Describe());

    internal static WebDavException Error(DocumentWriteResult result) => new(result.Status switch
    {
        DocumentWriteStatus.NotFound => WebDavStatusCode.NotFound,
        DocumentWriteStatus.TooLarge => (WebDavStatusCode)413,
        DocumentWriteStatus.DuplicateBlocked or DocumentWriteStatus.Conflict => WebDavStatusCode.Conflict,
        DocumentWriteStatus.VersionMismatch => WebDavStatusCode.PreconditionFailed,
        _ => WebDavStatusCode.Forbidden,
    }, result.Message ?? result.Status.ToString());

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebDAV listing of {Path} stopped at {Max} entries; the rest is not shown")]
    internal static partial void LogTruncated(ILogger logger, Uri path, int max);
}

/// <summary>Common behaviour: ETags from our data and live properties, including the Windows ones.</summary>
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

    /// <summary>Windows file attributes (hex): directory, read-only, hidden, archive.</summary>
    internal abstract string Win32Attributes { get; }

    public Task<EntityTag> UpdateETagAsync(CancellationToken cancellationToken) => Task.FromResult(ETag);

    public virtual Task<DeleteResult> DeleteAsync(CancellationToken cancellationToken) => throw Forbidden();

    public virtual IEnumerable<IUntypedReadableProperty> GetLiveProperties()
    {
        // Clients set times after writing or copying a file; they are accepted, not stored (ours come from the item).
        yield return new LastModifiedProperty(Modified.UtcDateTime, (_, _) => Task.CompletedTask);
        yield return new CreationDateProperty(Created, (_, _) => Task.CompletedTask);
        yield return new GetETagProperty(FileSystem.PropertyStore, this);

        // Windows Mini-Redirector properties, the same.
        yield return new LiveStringProperty(DavXml.Win32FileAttributes, Win32Attributes);
        yield return new LiveStringProperty(DavXml.Win32CreationTime, Created.ToString("R", CultureInfo.InvariantCulture));
        yield return new LiveStringProperty(DavXml.Win32LastModifiedTime, Modified.ToString("R", CultureInfo.InvariantCulture));
        yield return new LiveStringProperty(DavXml.Win32LastAccessTime, Modified.ToString("R", CultureInfo.InvariantCulture));
    }

    internal static WebDavException Forbidden() => new(WebDavStatusCode.Forbidden);

    /// <summary>The folder a move or copy goes into: only folders of libraries hold files.</summary>
    internal static DavFolder Target(ICollection collection) => collection as DavFolder ?? throw Forbidden();
}

internal abstract class DavCollection(DavFileSystem fileSystem, DavCollection? parent, Uri path, string name)
    : DavEntry(fileSystem, parent, path, name), ICollection
{
    internal abstract (Guid, Guid?) ListingKey { get; }

    internal override string Win32Attributes => "00000010";

    public async Task<IEntry?> GetChildAsync(string name, CancellationToken ct)
    {
        var children = await Dav.ListAsync(this, ct);
        return children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            ?? children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyCollection<IEntry>> GetChildrenAsync(CancellationToken ct) => await Dav.ListAsync(this, ct);

    public virtual Task<IDocument> CreateDocumentAsync(string name, CancellationToken ct) => throw Forbidden();

    public virtual Task<ICollection> CreateCollectionAsync(string name, CancellationToken ct) => throw Forbidden();

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

/// <summary>A workspace: its document libraries (created and changed in the web UI, not through WebDAV).</summary>
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

/// <summary>
/// A library (no folder item) or one of its folders: folders and files as the caller may read them, plus the caller's
/// temporary files (<see cref="DavTransient"/>). PUT uploads, MKCOL creates folders.
/// </summary>
internal sealed class DavFolder(DavFileSystem fileSystem, DavCollection parent, ListData list, ListItemData? folder, Uri path, string name)
    : DavCollection(fileSystem, parent, path, name), IContentCollection, IMovableEntry
{
    public ListData List { get; } = list;

    /// <summary>The folder item; null for the library itself.</summary>
    public ListItemData? Folder { get; } = folder;

    /// <summary>The folder's id, or the library's at its root (where temporary files are kept).</summary>
    public Guid ParentId => Folder?.Id ?? List.Id;

    /// <summary>The caller may add files here (temporary files too); the library's access is read when needed.</summary>
    public async Task<bool> CanAddAsync(CancellationToken ct) =>
        Dav.CanWrite && (Folder?.Access ?? (await Dav.Items.GetListAsync(List.WorkspaceId, List.Id, ct))?.Access) >= WorkspaceAccessLevel.Contribute;

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

        // Documents an app renamed to a temporary name while saving are shown under that name only.
        var transients = Dav.Authenticated ? await Dav.Transients.ListAsync(ParentId, ct) : [];
        var renamed = transients.Where(t => t is { ItemId: not null, Released: false }).Select(t => t.ItemId!.Value).ToHashSet();
        var files = await Dav.Documents.GetCurrentAsync(children.Where(c => !c.IsFolder).Select(c => c.Id).ToList(), ct);
        var named = children
            .Where(c => c.IsFolder || (files.ContainsKey(c.Id) && !renamed.Contains(c.Id)))
            .Select(c => (Item: c, File: c.IsFolder ? null : files[c.Id]));
        var entries = DavNames.Assign(named, c => c.Item.Id, c => DavNames.Name(Title(c.Item), c.File is null ? string.Empty : DavNames.Extension(c.File.FileName, c.File.MediaType)))
            .Select(c => c.Entry.File is null
                ? (IEntry)new DavFolder(Dav, this, List, c.Entry.Item, ChildPath(c.Name, true), c.Name)
                : new DavDocument(Dav, this, c.Entry.Item, c.Entry.File, ChildPath(c.Name, false), c.Name))
            .ToList();
        var taken = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        entries.AddRange(transients
            .Where(t => taken.Add(t.Name) && (t.ItemId is null || files.ContainsKey(t.ItemId.Value)))
            .Select(t => new DavTransientDocument(Dav, this, t, ChildPath(t.Name, false))));
        return entries;
    }

    public Task<IDocument> CreateDocumentAsync(string name, Stream content, CancellationToken cancellationToken) =>
        WriteAsync(name, content, replace: false, cancellationToken);

    /// <summary>A LOCK on a new name creates an empty file (RFC 4918 7.3); the next PUT fills it in place.</summary>
    public override Task<IDocument> CreateDocumentAsync(string name, CancellationToken ct) => CreateDocumentAsync(name, Stream.Null, ct);

    public override async Task<ICollection> CreateCollectionAsync(string name, CancellationToken ct)
    {
        if (await GetChildAsync(name, ct) is not null)
        {
            // MKCOL of a name that exists (also a file, asked for with a trailing slash): RFC 4918 9.3.1.
            throw new WebDavException(WebDavStatusCode.MethodNotAllowed);
        }

        var created = DavFileSystem.Check(await Dav.Items.CreateFolderAsync(List.WorkspaceId, List.Id, name, Folder?.Id, ct)).Item!;
        Dav.Invalidate();
        return new DavFolder(Dav, this, List, created, ChildPath(name, true), name);
    }

    /// <summary>Deletes the folder with everything in it (each item to the recycle bin). A library cannot be deleted here.</summary>
    public override async Task<DeleteResult> DeleteAsync(CancellationToken cancellationToken)
    {
        if (Folder is null)
        {
            return new DeleteResult(WebDavStatusCode.Forbidden, this);
        }

        foreach (var child in await Dav.ListAsync(this, cancellationToken))
        {
            var deleted = await child.DeleteAsync(cancellationToken);
            if (deleted.StatusCode != WebDavStatusCode.OK)
            {
                return deleted;
            }
        }

        var result = await Dav.Items.DeleteAsync(List.WorkspaceId, List.Id, Folder.Id, null, cancellationToken);
        Dav.Invalidate();
        return result.Succeeded ? new DeleteResult(WebDavStatusCode.OK, null) : new DeleteResult(DavFileSystem.Error(result).StatusCode, this);
    }

    public async Task MoveToAsync(ICollection collection, string name, IEntry? replaced, CancellationToken cancellationToken)
    {
        var target = Target(collection);
        if (Folder is null)
        {
            throw Forbidden();
        }

        if (replaced is not null && !ReferenceEquals(replaced, this)
            && (await replaced.DeleteAsync(cancellationToken)).StatusCode is var status && status != WebDavStatusCode.OK)
        {
            throw new WebDavException(status);
        }

        await Dav.MoveItemAsync(Folder, (DavFolder)ParentCollection!, target, name, cancellationToken);
    }

    /// <summary>
    /// Writes <paramref name="name"/>: a temporary file for the caller, a new version of the document with that name
    /// (when <paramref name="replace"/>), or a new document titled by the name without its extension.
    /// </summary>
    internal async Task<IDocument> WriteAsync(string name, Stream content, bool replace, CancellationToken ct)
    {
        if (replace && await GetChildAsync(name, ct) is IContentDocument existing)
        {
            await existing.ReplaceAsync(content, ct);
            return existing;
        }

        if (DavTransient.IsTransient(name))
        {
            if (!await CanAddAsync(ct))
            {
                throw Forbidden();
            }

            var file = await Dav.Transients.WriteAsync(List.WorkspaceId, List.Id, ParentId, name, content, Dav.Options.MaxTransientFileSize, ct);
            Dav.Invalidate();
            return new DavTransientDocument(Dav, this, file, ChildPath(name, false));
        }

        var uploaded = DavFileSystem.Check(await Dav.Uploads.UploadAsync(List.WorkspaceId, List.Id, Folder?.Id, content, name, DavNames.Title(name), ct)).File!;
        var item = await Dav.Items.GetAsync(List.WorkspaceId, List.Id, uploaded.ItemId, ct) ?? throw new WebDavException(WebDavStatusCode.NotFound);
        Dav.Invalidate();
        return new DavDocument(Dav, this, item, uploaded, ChildPath(name, false), name);
    }

    internal static string? Title(ListItemData item) =>
        item.Fields["title"] is { } title && title.GetValueKind() == System.Text.Json.JsonValueKind.String ? title.GetValue<string>() : null;
}

/// <summary>A document: its current file. PUT stores a new version, MOVE renames or moves it, DELETE recycles it.</summary>
internal sealed class DavDocument(DavFileSystem fileSystem, DavFolder parent, ListItemData item, DocumentFile file, Uri path, string name)
    : DavEntry(fileSystem, parent, path, name), IContentDocument, IMovableEntry
{
    public ListItemData Item { get; } = item;

    public DocumentFile File { get; } = file;

    public long Length => File.Size;

    /// <summary>Changes with the content (hash) and with the item (version), never read from the content.</summary>
    public override EntityTag ETag => new(false, $"{File.Sha256[..16]}-{Item.Version}");

    internal DavFolder Folder => (DavFolder)ParentCollection!;

    internal override DateTimeOffset Created => Item.CreatedAt;

    internal override DateTimeOffset Modified => Item.UpdatedAt;

    /// <summary>Archive, or read-only when the caller may not change the document.</summary>
    internal override string Win32Attributes => Dav.CanWrite && Item.Access >= WorkspaceAccessLevel.Contribute ? "00000020" : "00000001";

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

    public async Task ReplaceAsync(Stream content, CancellationToken cancellationToken)
    {
        DavFileSystem.Check(await Dav.Uploads.ReplaceAsync(
            Item.WorkspaceId, Item.ListId, Item.Id, content, File.FileName, File.Sha256, Dav.Options.EmptyFileGrace, cancellationToken));
        Dav.Invalidate();
    }

    public override async Task<DeleteResult> DeleteAsync(CancellationToken cancellationToken)
    {
        var result = await Dav.Items.DeleteAsync(Item.WorkspaceId, Item.ListId, Item.Id, null, cancellationToken);
        Dav.Invalidate();
        return result.Succeeded ? new DeleteResult(WebDavStatusCode.OK, null) : new DeleteResult(DavFileSystem.Error(result).StatusCode, this);
    }

    /// <summary>
    /// Rename or move. A temporary name in the same folder (Office renaming the original while it saves) only hides the
    /// document under that name, so that saving the new content onto the original name becomes a new version of it.
    /// </summary>
    public async Task MoveToAsync(ICollection collection, string name, IEntry? replaced, CancellationToken cancellationToken)
    {
        var target = Target(collection);
        if (replaced is not null && !ReferenceEquals(replaced, this)
            && (await replaced.DeleteAsync(cancellationToken)).StatusCode is var status && status != WebDavStatusCode.OK)
        {
            throw new WebDavException(status);
        }

        if (DavTransient.IsTransient(name) && target.ParentId == Folder.ParentId)
        {
            if (Item.Access < WorkspaceAccessLevel.Contribute)
            {
                throw Forbidden();
            }

            await Dav.Transients.AliasAsync(target.List.WorkspaceId, target.List.Id, target.ParentId, name, Item.Id, File.Id, File.Size, Name, cancellationToken);
            Dav.Invalidate();
            return;
        }

        if (!string.Equals(System.IO.Path.GetExtension(name), System.IO.Path.GetExtension(Name), StringComparison.OrdinalIgnoreCase))
        {
            DavFileSystem.Check(await Dav.Uploads.RenameAsync(Item.WorkspaceId, Item.ListId, Item.Id, name, cancellationToken));
        }

        await Dav.MoveItemAsync(Item, Folder, target, DavNames.Title(name), cancellationToken);
    }

    public async Task<IDocument> CopyToAsync(ICollection collection, string name, CancellationToken cancellationToken)
    {
        await using var content = await OpenReadAsync(cancellationToken);
        return await Target(collection).WriteAsync(name, content, replace: true, cancellationToken);
    }

    public Task<Stream> CreateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Stream> OpenWriteAsync(long position, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IDocument> MoveToAsync(ICollection collection, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>
/// A temporary file of the caller (<see cref="DavTransientFile"/>), or a document they renamed to a temporary name
/// (then it shows that document's content as it was). Moving one onto a document's name stores it as a new version.
/// </summary>
internal sealed class DavTransientDocument(DavFileSystem fileSystem, DavFolder parent, DavTransientFile file, Uri path)
    : DavEntry(fileSystem, parent, path, file.Name), IContentDocument, IMovableEntry
{
    public DavTransientFile File { get; } = file;

    public long Length => File.Size;

    public override EntityTag ETag => new(false, $"t{File.Id:N}-{File.UpdatedAt.UtcTicks}");

    internal DavFolder Folder => (DavFolder)ParentCollection!;

    internal override DateTimeOffset Created => File.CreatedAt;

    internal override DateTimeOffset Modified => File.UpdatedAt;

    /// <summary>Hidden and archive, like the temporary files of the apps that create them.</summary>
    internal override string Win32Attributes => "00000022";

    public override IEnumerable<IUntypedReadableProperty> GetLiveProperties()
    {
        foreach (var property in base.GetLiveProperties())
        {
            yield return property;
        }

        yield return new ContentLengthProperty(Length);
    }

    public async Task<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        File.VersionId is { } version
            ? await Dav.Documents.OpenVersionAsync(version, cancellationToken) ?? Stream.Null
            : await Dav.Transients.OpenReadAsync(File, cancellationToken);

    public async Task ReplaceAsync(Stream content, CancellationToken cancellationToken)
    {
        await Dav.Transients.WriteAsync(Folder.List.WorkspaceId, Folder.List.Id, Folder.ParentId, File.Name, content, Dav.Options.MaxTransientFileSize, cancellationToken);
        Dav.Invalidate();
    }

    public override async Task<DeleteResult> DeleteAsync(CancellationToken cancellationToken)
    {
        await Dav.Transients.DeleteAsync(File, cancellationToken);
        Dav.Invalidate();
        return new DeleteResult(WebDavStatusCode.OK, null);
    }

    public async Task MoveToAsync(ICollection collection, string name, IEntry? replaced, CancellationToken cancellationToken)
    {
        var target = Target(collection);
        if (DavTransient.IsTransient(name))
        {
            await DeleteReplacedAsync(replaced, cancellationToken);
            await Dav.Transients.MoveAsync(File, target.List.WorkspaceId, target.List.Id, target.ParentId, name, cancellationToken);
        }
        else if (File.ItemId is { } itemId)
        {
            // A renamed document moves on (or back to its name): it is the document again.
            await DeleteReplacedAsync(replaced, cancellationToken);
            await Dav.Transients.DeleteAsync(File, cancellationToken);
            var item = await Dav.Items.GetAsync(Folder.List.WorkspaceId, Folder.List.Id, itemId, cancellationToken) ?? throw new WebDavException(WebDavStatusCode.NotFound);
            if (!File.Released && (target.ParentId != Folder.ParentId || !string.Equals(name, File.OriginalName, StringComparison.OrdinalIgnoreCase)))
            {
                await Dav.MoveItemAsync(item, Folder, target, DavNames.Title(name), cancellationToken);
            }
        }
        else
        {
            await SaveAsAsync(target, name, replaced, cancellationToken);
            await Dav.Transients.DeleteAsync(File, cancellationToken);
        }

        Dav.Invalidate();
    }

    public async Task<IDocument> CopyToAsync(ICollection collection, string name, CancellationToken cancellationToken)
    {
        await using var content = await OpenReadAsync(cancellationToken);
        return await Target(collection).WriteAsync(name, content, replace: true, cancellationToken);
    }

    public Task<Stream> CreateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Stream> OpenWriteAsync(long position, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IDocument> MoveToAsync(ICollection collection, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// The temporary content becomes <paramref name="name"/>: a new version of the document the app renamed away from
    /// that name (Office's safe save), of the document it overwrites, or a new document.
    /// </summary>
    private async Task SaveAsAsync(DavFolder target, string name, IEntry? replaced, CancellationToken ct)
    {
        await using var content = await OpenReadAsync(ct);
        var renamed = (await Dav.Transients.ListAsync(target.ParentId, ct))
            .FirstOrDefault(t => t is { ItemId: not null, Released: false } && string.Equals(t.OriginalName, name, StringComparison.OrdinalIgnoreCase));
        if (renamed is not null)
        {
            DavFileSystem.Check(await Dav.Uploads.ReplaceAsync(target.List.WorkspaceId, target.List.Id, renamed.ItemId!.Value, content, name, null, null, ct));
            await Dav.Transients.ReleaseAsync(renamed, ct);
        }
        else if (replaced is IContentDocument document and not DavTransientDocument)
        {
            await document.ReplaceAsync(content, ct);
        }
        else
        {
            await DeleteReplacedAsync(replaced, ct);
            await target.WriteAsync(name, content, replace: false, ct);
        }
    }

    private static async Task DeleteReplacedAsync(IEntry? replaced, CancellationToken ct)
    {
        if (replaced is not null && (await replaced.DeleteAsync(ct)).StatusCode is var status && status != WebDavStatusCode.OK)
        {
            throw new WebDavException(status);
        }
    }
}

/// <summary>A computed live property. Windows sets these after writing a file: accepted, not stored.</summary>
internal sealed class LiveStringProperty(XName name, string value) : ILiveProperty, IUntypedWriteableProperty
{
    public XName Name { get; } = name;

    public string? Language => null;

    public IReadOnlyCollection<XName> AlternativeNames { get; } = [];

    public int Cost => 0;

    public Task<bool> IsValidAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<XElement> GetXmlValueAsync(CancellationToken ct) => Task.FromResult(new XElement(Name, value));

    public Task SetXmlValueAsync(XElement element, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The stored media type, not a guess from the name.</summary>
internal sealed class DavMimeTypeDetector : IMimeTypeDetector
{
    public bool TryDetect(IEntry entry, out string mimeType)
    {
        mimeType = entry is DavDocument document ? document.File.MediaType : "application/octet-stream";
        return entry is DavDocument or DavTransientDocument;
    }
}

/// <summary>
/// Dead (client-defined) properties are not stored (ADR-0047): setting a custom one is refused; the standard
/// <c>DAV:</c> ones (the content type a PUT sends, the display name a COPY carries over) are accepted and ignored, since
/// names and types come from our data. ETags come from the entries.
/// </summary>
internal sealed class EmptyPropertyStore(IDeadPropertyFactory deadProperties) : PropertyStoreBase(deadProperties)
{
    public override int Cost => 0;

    public override Task<IReadOnlyCollection<XElement>> GetAsync(IEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<XElement>>([]);

    public override Task SetAsync(IEntry entry, IEnumerable<XElement> properties, CancellationToken cancellationToken) =>
        properties.All(p => p.Name.NamespaceName == "DAV:") ? Task.CompletedTask : throw DavEntry.Forbidden();

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
