using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Dav.AspNetCore.Server;
using Dav.AspNetCore.Server.Store;
using Dav.AspNetCore.Server.Store.Properties;

namespace WebDavPoc;

public sealed class PocStoreOptions : StoreOptions;

/// <summary>
/// The adapter the Dav module would implement: resolves URIs by computed names, one listing per collection per
/// request (cached), and maps writes to item operations (create, new version, folder, rename/move, recycle bin).
/// </summary>
public sealed class PocStore(PocModel model, IHttpContextAccessor http, PocSettings settings) : IStore
{
    private readonly Dictionary<Node, IReadOnlyList<(Node Node, string Name)>> listings = [];

    public PocModel Model => model;
    public PocSettings Settings => settings;
    public HttpContext? Http => http.HttpContext;
    public int ListingsComputed { get; private set; }

    public Task<IStoreItem?> GetItemAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        http.HttpContext?.Response.Headers.Append("X-Poc-Resolve", Uri.EscapeDataString($"{uri.OriginalString}|{uri.LocalPath}"));
        lock (model.Gate)
        {
            // The library hands over file:// URIs whose LocalPath keeps '#' and '%' escaped: decode once here.
            var path = Uri.UnescapeDataString(uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString);
            var node = model.Root;
            var current = "/";
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Children(node).FirstOrDefault(c => c.Name == segment);
                if (match.Node is null)
                {
                    match = Children(node).FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
                }

                if (match.Node is null)
                {
                    return Task.FromResult<IStoreItem?>(null);
                }

                node = match.Node;
                // Keep the client's spelling: the library compares URI segments case-sensitively afterwards.
                current = current.TrimEnd('/') + "/" + segment;
            }

            return Task.FromResult<IStoreItem?>(Wrap(node, current));
        }
    }

    public IStoreItem Wrap(Node node, string path) =>
        node.IsCollection ? new PocCollection(this, node, path) : new PocFile(this, node, path);

    public IReadOnlyList<(Node Node, string Name)> Children(Node node)
    {
        if (!listings.TryGetValue(node, out var list))
        {
            ListingsComputed++;
            var visible = node.Children.Where(c => c.Kind switch
            {
                NodeKind.List => false,                      // only document libraries
                NodeKind.File => c.Content is not null,      // items without a file are hidden
                _ => true,
            });
            list = DavNames.Assign(visible).Take(settings.MaxFolderEntries).ToList();
            listings[node] = list;
        }

        return list;
    }

    public void Invalidate() => listings.Clear();

    public static Uri MakeUri(string path) => new(path.Length == 0 ? "/" : path);
}

public abstract class PocItem(PocStore store, Node node, string path) : IStoreItem
{
    public PocStore Store { get; } = store;
    public Node Node { get; } = node;
    public string Path { get; } = path;
    // Collections get a trailing slash, so PROPFIND hrefs of folders end with '/' (what clients expect).
    public Uri Uri { get; } = PocStore.MakeUri(node.IsCollection && !path.EndsWith('/') ? path + "/" : path);
    public string Name => Path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    public abstract Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken = default);

    public abstract Task<DavStatusCode> WriteDataAsync(Stream stream, CancellationToken cancellationToken = default);

    public abstract Task<ItemResult> CopyAsync(IStoreCollection destination, string name, bool overwrite, CancellationToken cancellationToken = default);

    public static void RegisterCommon<T>(bool collection) where T : PocItem
    {
        Property.RegisterProperty<T>(Xml.Dav("displayname"), read: (c, _) => Set(c, ((PocItem)c.Item).Name), metadata: new PropertyMetadata(Computed: true));
        Property.RegisterProperty<T>(Xml.Dav("creationdate"),
            read: (c, _) => Set(c, XmlConvert.ToString(((PocItem)c.Item).Node.Created.UtcDateTime, XmlDateTimeSerializationMode.Utc)),
            metadata: new PropertyMetadata(Computed: true));
        Property.RegisterProperty<T>(Xml.Dav("getlastmodified"),
            read: (c, _) => Set(c, ((PocItem)c.Item).Node.Updated.ToString("R", CultureInfo.InvariantCulture)),
            metadata: new PropertyMetadata(Computed: true));
        Property.RegisterProperty<T>(Xml.Dav("resourcetype"),
            read: (c, _) => Set(c, collection ? new XElement(Xml.Dav("collection")) : null),
            metadata: new PropertyMetadata(Computed: true));

        // Windows Mini-Redirector properties: readable, and PROPPATCH accepted but not stored (plan: WD-3).
        Property.RegisterProperty<T>(Xml.Ms("Win32FileAttributes"),
            read: (c, _) =>
            {
                var item = (PocItem)c.Item;
                var attributes = collection ? 0x10 : item.Store.Settings.ReadOnly || !item.Node.Writable ? 0x01 : 0x20;
                return Set(c, attributes.ToString("X8", CultureInfo.InvariantCulture));
            },
            change: (_, _) => ValueTask.CompletedTask);
        foreach (var name in new[] { "Win32CreationTime", "Win32LastAccessTime", "Win32LastModifiedTime" })
        {
            Property.RegisterProperty<T>(Xml.Ms(name),
                read: (c, _) => Set(c, ((PocItem)c.Item).Node.Updated.ToString("R", CultureInfo.InvariantCulture)),
                change: (_, _) => ValueTask.CompletedTask);
        }

        Property.RegisterSupportedLockProperty<T>();
        Property.RegisterLockDiscoveryProperty<T>();
    }

    protected static ValueTask Set(PropertyReadContext context, object? value)
    {
        context.SetResult(value);
        return ValueTask.CompletedTask;
    }
}

public sealed class PocFile(PocStore store, Node node, string path) : PocItem(store, node, path)
{
    public static void Register()
    {
        RegisterCommon<PocFile>(collection: false);
        Property.RegisterProperty<PocFile>(Xml.Dav("getcontentlength"),
            read: (c, _) => Set(c, ((PocFile)c.Item).Node.Content!.LongLength.ToString(CultureInfo.InvariantCulture)),
            metadata: new PropertyMetadata(Computed: true));
        Property.RegisterProperty<PocFile>(Xml.Dav("getcontenttype"),
            read: (c, _) => Set(c, ((PocFile)c.Item).Node.MediaType),
            metadata: new PropertyMetadata(Computed: true));

        // Not expensive: derived from the stored hash and the item version, never from reading the content.
        Property.RegisterProperty<PocFile>(Xml.Dav("getetag"),
            read: (c, _) => Set(c, $"{((PocFile)c.Item).Node.Sha256[..16]}-{((PocFile)c.Item).Node.Version}"),
            metadata: new PropertyMetadata(Computed: true));
    }

    public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(Node.Content!, writable: false));

    public override async Task<DavStatusCode> WriteDataAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (Store.Settings.ReadOnly || !Node.Writable)
        {
            return Fail(DavStatusCode.Forbidden);
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > Store.Model.MaxFileSize)
        {
            return Fail((DavStatusCode)StatusCodes.Status413PayloadTooLarge);
        }

        lock (Store.Model.Gate)
        {
            var content = buffer.ToArray();
            // Explorer creates a 0-byte file, then PUTs the content: replace the empty version instead of adding one.
            var replaceEmpty = Node.Content is { Length: 0 } && Node.FileVersions <= 1;
            Node.SetContent(content, newVersion: !replaceEmpty);
            if (replaceEmpty && Node.FileVersions == 0)
            {
                Node.FileVersions = 1;
            }

            Store.Invalidate();
        }

        Store.Http?.Response.Headers.Append("X-Poc-Versions", Node.FileVersions.ToString(CultureInfo.InvariantCulture));
        return DavStatusCode.Ok;
    }

    /// <summary>
    /// PutHandler ignores the status WriteDataAsync returns. With X-Poc-Status-Workaround the store sets the response
    /// status itself, to see whether that survives.
    /// </summary>
    private DavStatusCode Fail(DavStatusCode status)
    {
        if (Store.Http is { } http && http.Request.Headers.ContainsKey("X-Poc-Status-Workaround"))
        {
            http.Response.StatusCode = (int)status;
        }

        return status;
    }

    public override async Task<ItemResult> CopyAsync(IStoreCollection destination, string name, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (Store.Settings.ReadOnly)
        {
            return ItemResult.Fail(DavStatusCode.Forbidden);
        }

        var existing = await destination.GetItemAsync(name, cancellationToken);
        if (existing is not null && !overwrite)
        {
            return ItemResult.Fail(DavStatusCode.PreconditionFailed);
        }

        // MoveHandler calls CopyAsync and then deletes the source: a move becomes a new item (identity is lost).
        Store.Http?.Response.Headers.Append("X-Poc-CopyAsync", Node.Id.ToString());
        var created = await destination.CreateItemAsync(name, cancellationToken);
        if (created.Item is not PocFile target)
        {
            return ItemResult.Fail(created.StatusCode);
        }

        lock (Store.Model.Gate)
        {
            target.Node.FileName = name;
            target.Node.MediaType = Node.MediaType;
            target.Node.SetContent(Node.Content!, newVersion: true);
            Store.Invalidate();
        }

        return existing is null ? ItemResult.Created(target) : ItemResult.NoContent(target);
    }
}

public sealed class PocCollection(PocStore store, Node node, string path) : PocItem(store, node, path), IStoreCollection
{
    public static void Register()
    {
        RegisterCommon<PocCollection>(collection: true);
        Property.RegisterProperty<PocCollection>(Xml.Dav("quota-available-bytes"), read: (c, _) => Set(c, "107374182400"), metadata: new PropertyMetadata(Computed: true));
        Property.RegisterProperty<PocCollection>(Xml.Dav("quota-used-bytes"), read: (c, _) => Set(c, "1048576"), metadata: new PropertyMetadata(Computed: true));
    }

    public bool CanHoldItems => Node.Kind is NodeKind.Library or NodeKind.Folder;

    public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stream.Null);

    public override Task<DavStatusCode> WriteDataAsync(Stream stream, CancellationToken cancellationToken = default) =>
        Task.FromResult(DavStatusCode.Forbidden);

    public override async Task<ItemResult> CopyAsync(IStoreCollection destination, string name, bool overwrite, CancellationToken cancellationToken = default)
    {
        // Copy (and move, which the library implements as copy + delete) of a folder: create it, the handler recurses.
        var existing = await destination.GetItemAsync(name, cancellationToken);
        if (existing is PocCollection existingCollection)
        {
            return ItemResult.NoContent(existingCollection);
        }

        var created = await destination.CreateCollectionAsync(name, cancellationToken);
        return created.Collection is null ? ItemResult.Fail(created.StatusCode) : ItemResult.Created(created.Collection);
    }

    public Task<IStoreItem?> GetItemAsync(string name, CancellationToken cancellationToken = default)
    {
        Store.Http?.Response.Headers.Append("X-Poc-Lookup", Uri.EscapeDataString($"{Path}|{name}"));
        lock (Store.Model.Gate)
        {
            var match = Store.Children(Node).FirstOrDefault(c => c.Name == name);
            if (match.Node is null)
            {
                match = Store.Children(Node).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(match.Node is null ? null : Store.Wrap(match.Node, Child(match.Name)));
        }
    }

    public Task<IReadOnlyCollection<IStoreItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        lock (Store.Model.Gate)
        {
            IReadOnlyCollection<IStoreItem> items = Store.Children(Node).Select(c => Store.Wrap(c.Node, Child(c.Name))).ToList();
            return Task.FromResult(items);
        }
    }

    public async Task<CollectionResult> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Store.Settings.ReadOnly || !CanHoldItems)
        {
            return CollectionResult.Fail(DavStatusCode.Forbidden);
        }

        if (await GetItemAsync(name, cancellationToken) is not null)
        {
            return CollectionResult.Fail(DavStatusCode.NotAllowed);
        }

        lock (Store.Model.Gate)
        {
            var folder = Node.Add(new Node { Kind = NodeKind.Folder, Title = name });
            Store.Invalidate();
            return CollectionResult.Created(new PocCollection(Store, folder, Child(DavNames.BaseName(folder))));
        }
    }

    public async Task<ItemResult> CreateItemAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Store.Settings.ReadOnly || !CanHoldItems)
        {
            return ItemResult.Fail(DavStatusCode.Forbidden);
        }

        // PUT on an existing name: return it, WriteDataAsync then adds a version.
        if (await GetItemAsync(name, cancellationToken) is PocFile existing)
        {
            return ItemResult.NoContent(existing);
        }

        lock (Store.Model.Gate)
        {
            var file = Node.Add(new Node
            {
                Kind = NodeKind.File,
                Title = DavNames.TitleFromName(name, isFile: true),
                FileName = name,
                MediaType = MediaTypes.FromName(name),
            });
            file.Content = [];
            Store.Invalidate();
            Store.Http?.Response.Headers.Append("X-Poc-Transient", DavNames.IsTransient(name) ? "true" : "false");
            return ItemResult.Created(new PocFile(Store, file, Child(DavNames.BaseName(file))));
        }
    }

    /// <summary>
    /// Moves keep the item (id, versions). NuGet 1.0.1 and git HEAD never call this (MOVE = COPY + DELETE); the
    /// patched library (library-fixes.patch) does.
    /// </summary>
    public async Task<ItemResult> MoveItemAsync(string name, IStoreCollection destination, string destinationName, bool overwrite, CancellationToken cancellationToken = default)
    {
        Store.Http?.Response.Headers.Append("X-Poc-MoveItemAsync", "called");
        if (Store.Settings.ReadOnly)
        {
            return ItemResult.Fail(DavStatusCode.Forbidden);
        }

        if (await GetItemAsync(name, cancellationToken) is not PocItem source || destination is not PocCollection target)
        {
            return ItemResult.Fail(DavStatusCode.NotFound);
        }

        if (!target.CanHoldItems)
        {
            return ItemResult.Fail(DavStatusCode.Forbidden);
        }

        if (!overwrite && await target.GetItemAsync(destinationName, cancellationToken) is not null)
        {
            return ItemResult.Fail(DavStatusCode.PreconditionFailed);
        }

        lock (Store.Model.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var isFile = source is PocFile;

            // Safe-save, step 3: the temporary file is moved onto the name the original had a moment ago.
            // The original gets the content as a new version and its name back; the temporary file never becomes an item.
            if (isFile && Store.Model.RenamedAway.Remove((target.Node, destinationName.ToLowerInvariant()), out var original)
                && now - original.At < TimeSpan.FromMinutes(2) && original.Node.Parent == target.Node)
            {
                original.Node.SetContent(source.Node.Content ?? [], newVersion: true);
                original.Node.Title = DavNames.TitleFromName(destinationName, isFile: true);
                original.Node.FileName = destinationName;
                Node.Children.Remove(source.Node);
                Store.Invalidate();
                Store.Http?.Response.Headers.Append("X-Poc-SafeSave", "merged into original");
                return ItemResult.Created(Store.Wrap(original.Node, target.Path.TrimEnd('/') + "/" + destinationName));
            }

            // Safe-save, step 2: the original is renamed to a temporary name; remember where it came from.
            if (isFile && DavNames.IsTransient(destinationName) && !DavNames.IsTransient(name))
            {
                Store.Model.RenamedAway[(Node, name.ToLowerInvariant())] = (source.Node, now);
            }

            Node.Children.Remove(source.Node);
            source.Node.Title = DavNames.TitleFromName(destinationName, isFile);
            if (isFile)
            {
                source.Node.FileName = destinationName;
            }

            target.Node.Add(source.Node);
            source.Node.Updated = now;
            Store.Invalidate();
            return ItemResult.Created(Store.Wrap(source.Node, target.Path.TrimEnd('/') + "/" + destinationName));
        }
    }

    public async Task<DavStatusCode> DeleteItemAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Store.Settings.ReadOnly)
        {
            return DavStatusCode.Forbidden;
        }

        if (await GetItemAsync(name, cancellationToken) is not PocItem item)
        {
            return DavStatusCode.NotFound;
        }

        lock (Store.Model.Gate)
        {
            if (item.Node.Children.Count > 0)
            {
                return DavStatusCode.Conflict; // the item store rejects non-empty folders
            }

            Node.Children.Remove(item.Node);
            Store.Model.RecycleBin.Add(item.Node);
            Store.Invalidate();
            return DavStatusCode.NoContent;
        }
    }

    private string Child(string name) => Path.TrimEnd('/') + "/" + name;
}

public static class Xml
{
    public static XName Dav(string name) => XName.Get(name, "DAV:");

    public static XName Ms(string name) => XName.Get(name, "urn:schemas-microsoft-com:");
}

public static class MediaTypes
{
    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider Provider = new();

    public static string FromName(string name) =>
        Provider.TryGetContentType(name, out var type) ? type : "application/octet-stream";
}

/// <summary>Dead properties are not stored (no table in the plan); unknown properties report 404/403 per property.</summary>
public sealed class NoopPropertyStore : IPropertyStore
{
    public ValueTask SaveChangesAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DeletePropertiesAsync(IStoreItem item, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask CopyPropertiesAsync(IStoreItem source, IStoreItem destination, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<bool> SetPropertyAsync(IStoreItem item, XName propertyName, PropertyMetadata propertyMetadata, bool isRegistered,
        object? propertyValue, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask<IReadOnlyCollection<PropertyData>> GetPropertiesAsync(IStoreItem item, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyCollection<PropertyData>>([]);
}

/// <summary>Optional (Poc:DeadProperties=true): keeps client properties in memory, to see what litmus "props" needs.</summary>
public sealed class InMemoryPropertyStore : IPropertyStore
{
    private static readonly Dictionary<string, Dictionary<XName, object?>> Properties = [];

    private static string Key(IStoreItem item) => item.Uri.AbsolutePath.TrimEnd('/');

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DeletePropertiesAsync(IStoreItem item, CancellationToken cancellationToken = default)
    {
        lock (Properties)
        {
            Properties.Remove(Key(item));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CopyPropertiesAsync(IStoreItem source, IStoreItem destination, CancellationToken cancellationToken = default)
    {
        lock (Properties)
        {
            if (Properties.TryGetValue(Key(source), out var values))
            {
                Properties[Key(destination)] = new Dictionary<XName, object?>(values);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SetPropertyAsync(IStoreItem item, XName propertyName, PropertyMetadata propertyMetadata, bool isRegistered,
        object? propertyValue, CancellationToken cancellationToken = default)
    {
        lock (Properties)
        {
            if (!Properties.TryGetValue(Key(item), out var values))
            {
                Properties[Key(item)] = values = [];
            }

            if (propertyValue is null)
            {
                values.Remove(propertyName);
            }
            else
            {
                values[propertyName] = propertyValue;
            }
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<IReadOnlyCollection<PropertyData>> GetPropertiesAsync(IStoreItem item, CancellationToken cancellationToken = default)
    {
        lock (Properties)
        {
            IReadOnlyCollection<PropertyData> result = Properties.TryGetValue(Key(item), out var values)
                ? values.Select(v => new PropertyData(v.Key, v.Value)).ToList()
                : [];
            return ValueTask.FromResult(result);
        }
    }
}
