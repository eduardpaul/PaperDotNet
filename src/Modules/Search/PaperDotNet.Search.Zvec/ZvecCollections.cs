using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Zvec.Native;

namespace PaperDotNet.Search.Zvec;

/// <summary>Options of the zvec store (<c>Search:Zvec</c>).</summary>
public sealed class ZvecOptions
{
    public const string Section = "Search:Zvec";

    /// <summary>Where collections live; default <c>{Storage:DataPath}/search/zvec</c>.</summary>
    public string? Path { get; set; }

    /// <summary>The native library file or its folder, when it is not next to the app or on the library path.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>zvec's memory limit in MB (0: zvec's default).</summary>
    public int MemoryLimitMb { get; set; } = 1024;

    /// <summary>zvec's query threads (0: zvec's default).</summary>
    public int QueryThreads { get; set; }

    /// <summary>
    /// Languages with stemming (Snowball names, e.g. <c>english</c>, <c>german</c>). Each is a full-text column, which
    /// costs about 40 MB per open collection; set when a tenant's collection is created.
    /// </summary>
    public List<string> Languages { get; set; } = ["english"];

    /// <summary>Whether <c>word*</c> terms are supported (a trigram column, about 40 MB per open collection); set at creation.</summary>
    public bool PrefixSearch { get; set; } = true;

    /// <summary>Collections kept open; the least recently used ones are closed beyond this (each holds memory).</summary>
    public int MaxOpenCollections { get; set; } = 4;

    /// <summary>Head rows read per keyword search, for ranking, counts and facets.</summary>
    public int KeywordCandidates { get; set; } = 10_000;

    /// <summary>Passages looked at per semantic search; several may belong to one document.</summary>
    public int PassageCandidates { get; set; } = 1000;
}

/// <summary>
/// A tenant's open collection, its catalog and the lock its writes take. It closes once it is retired (evicted or
/// shut down) and its last user has released it.
/// </summary>
internal sealed class ZvecIndex(ZvecCollection collection, ZvecCatalog catalog, SearchVectorModel? vectors)
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _users;
    private bool _retired;

    public ZvecCollection Collection => collection;

    public ZvecCatalog Catalog => catalog;

    /// <summary>The model the collection's vector column is sized for, or null (text only).</summary>
    public SearchVectorModel? Vectors => vectors;

    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public long LastUsed { get; private set; }

    public Task Closed => _closed.Task;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_retired)
            {
                return false;
            }

            _users++;
            LastUsed = Environment.TickCount64;
            return true;
        }
    }

    public void Release()
    {
        bool close;
        lock (_gate)
        {
            _users--;
            close = _retired && _users == 0;
        }

        if (close)
        {
            Close();
        }
    }

    public void Retire()
    {
        bool close;
        lock (_gate)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
            close = _users == 0;
        }

        if (close)
        {
            Close();
        }
    }

    private void Close()
    {
        collection.Dispose();
        WriteLock.Dispose();
        _closed.TrySetResult();
    }
}

/// <summary>A use of an open collection; disposing it lets the collection close if it was evicted meanwhile.</summary>
internal sealed class ZvecLease(ZvecIndex index) : IDisposable
{
    private ZvecIndex? _index = index;

    public ZvecIndex Index => _index ?? throw new ObjectDisposedException(nameof(ZvecLease));

    public void Dispose() => Interlocked.Exchange(ref _index, null)?.Release();
}

/// <summary>
/// Opens one collection per tenant and vector space (ADR-0044), under <c>{Path}/{tenant}/{space}</c>: <c>text</c>
/// without an embedding model, else a key of the model and its dimensions. zvec cannot add a vector column later, so a
/// different model gets a new, empty collection that a reindex fills. At most <see cref="ZvecOptions.MaxOpenCollections"/>
/// stay open. zvec locks a collection's folder, so only one server can use it, and a collection is reopened only
/// after its previous handle closed. As an <see cref="IBackupFolder"/> it closes every collection and holds new uses
/// while a backup copies or a restore replaces the folder.
/// </summary>
internal sealed partial class ZvecCollections(IOptions<ZvecOptions> options, ILogger<ZvecCollections> logger) : IBackupFolder, IDisposable
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _frozen;

    /// <summary>Tenants known to have no passage waiting for an embedding (unknown after a start: assumed waiting).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _embedded = new();

    /// <summary>Whether the tenant may have passages without embeddings; the embedding job skips the others.</summary>
    public bool MayNeedEmbeddings(Guid tenantId) => !_embedded.GetValueOrDefault(tenantId);

    public void SetEmbedded(Guid tenantId, bool done) => _embedded[tenantId] = done;
    private readonly Dictionary<string, ZvecIndex> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZvecIndex> _closing = new(StringComparer.Ordinal);

    /// <summary>
    /// A use of the tenant's collection; null when it does not exist and <paramref name="create"/> is false (reads of a
    /// tenant that never indexed anything create nothing).
    /// </summary>
    public async Task<ZvecLease?> LeaseAsync(Guid tenantId, SearchVectorModel? vectors, bool create, CancellationToken ct)
    {
        var settings = options.Value;
        ZvecLibrary.Initialize(settings.LibraryPath, (long)settings.MemoryLimitMb << 20, settings.QueryThreads);
        var space = vectors is null ? "text" : $"v-{ZvecLayout.Hash(vectors.Key, 12)}-{vectors.Dimensions}";
        var path = System.IO.Path.Combine(Path, ZvecLayout.Id(tenantId), space);
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_frozen is { } frozen)
                {
                    wait = frozen.Task;
                }
                else if (_open.TryGetValue(path, out var index) && index.TryAcquire())
                {
                    return new ZvecLease(index);
                }
                else if (!_closing.TryGetValue(path, out var closing) || closing.Closed.IsCompleted)
                {
                    if (!create && !Directory.Exists(path))
                    {
                        return null;
                    }

                    _closing.Remove(path);
                    index = Open(path, vectors, settings);
                    index.TryAcquire();
                    _open[path] = index;
                    Evict(path, settings.MaxOpenCollections);
                    return new ZvecLease(index);
                }
                else
                {
                    wait = closing.Closed;
                }
            }

            await wait.WaitAsync(ct);
        }
    }

    /// <summary>Retires the least recently used collections beyond the limit (they close when their last use ends).</summary>
    private void Evict(string keep, int max)
    {
        foreach (var (path, index) in _open.Where(e => e.Key != keep).OrderBy(e => e.Value.LastUsed).Take(Math.Max(0, _open.Count - Math.Max(1, max))).ToList())
        {
            _open.Remove(path);
            _closing[path] = index;
            index.Retire();
        }
    }

    public string Name => "search-zvec";

    public string Path => options.Value.Path ?? System.IO.Path.Combine("data", "search", "zvec");

    public async Task<IAsyncDisposable> FreezeAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource frozen;
        List<Task> closing;
        lock (_gate)
        {
            if (_frozen is not null)
            {
                throw new InvalidOperationException("The zvec collections are already frozen.");
            }

            _frozen = frozen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var (path, index) in _open)
            {
                _closing[path] = index;
                index.Retire();
            }

            _open.Clear();
            closing = [.. _closing.Values.Select(i => i.Closed)];
        }

        await Task.WhenAll(closing).WaitAsync(cancellationToken);
        return new Thaw(this, frozen);
    }

    private sealed class Thaw(ZvecCollections owner, TaskCompletionSource frozen) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                owner._frozen = null;
                owner._closing.Clear();
            }

            frozen.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private ZvecIndex Open(string path, SearchVectorModel? vectors, ZvecOptions settings)
    {
        var catalog = ZvecCatalog.Load(path);
        ZvecCollection collection;
        if (Directory.Exists(path))
        {
            collection = OpenClosed(path);
        }
        else
        {
            LogCreating(path);
            var languages = settings.Languages.Where(ZvecLayout.Languages.Contains).Distinct().ToList();
            collection = ZvecCollection.Create(path, ZvecLayout.Columns(vectors?.Dimensions, languages, settings.PrefixSearch));
            catalog.Initialize(languages, settings.PrefixSearch, vectors is null ? null : ZvecLayout.Hash(vectors.Key, 16));
        }

        return new ZvecIndex(collection, catalog, vectors);
    }

    /// <summary>
    /// Opens a collection whose previous handle was just closed: zvec finishes closing in the background, so its
    /// <c>LOCK</c> can still be held for a moment.
    /// </summary>
    private static ZvecCollection OpenClosed(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ZvecCollection.Open(path);
            }
            catch (ZvecException ex) when (attempt < 100 && ex.Message.Contains("lock", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(20);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var index in _open.Values)
            {
                index.Retire();
            }

            _open.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Creating the zvec search collection {Path}.")]
    private partial void LogCreating(string path);
}
