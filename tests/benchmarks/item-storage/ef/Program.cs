using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Data;
using PaperDotNet.Persistence;
using PaperDotNet.Persistence.PostgreSql;
using PaperDotNet.Persistence.Sqlite;

var mode = args.Length > 0 ? args[0] : "sql";
var provider = args.Length > 1 ? args[1] : "sqlite";
var connection = args.Length > 2 ? args[2] : "Data Source=/tmp/efprobe.db";

var counter = new EventCounter();
var services = new ServiceCollection();
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:PaperDotNet"] = connection,
    ["Database:RowLevelSecurity"] = "true",
}).Build();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(counter));
services.AddSingleton<ITenantContext>(new Tenant(Md5Guid("tenant")));
services.AddSingleton<ICurrentUser>(new User(Md5Guid("u5")));
if (provider == "sqlite") services.AddPaperDotNetSqlite(config); else services.AddPaperDotNetPostgreSql(config);
services.AddModuleDbContext<ListsDbContext>(ListsDbContext.Schema);
await using var sp = services.BuildServiceProvider();
await using var scope = sp.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();

var list = Md5Guid("list-dms");
var userId = Md5Guid("u5");
var groups = new List<Guid> { Md5Guid("g5"), Md5Guid("g35") };
List<Guid> principals = [userId, Md5Guid("ws-members"), .. groups];
var folder = Md5Guid("sf5-1");

// --- today's ListSchemaLoader.GetAccessAsync, verbatim LINQ
IQueryable<bool> AnyUnique() => db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(i => i.ListId == list && i.HasUniquePermissions).Select(i => true).Take(1);
IQueryable<PermissionGrant> Grants() => db.Grants.Where(g => g.ListId == list).AsNoTracking()
    .Where(g => (g.PrincipalType == PrincipalType.User && g.PrincipalId == userId) || (g.PrincipalType == PrincipalType.Group && groups.Contains(g.PrincipalId)));
IQueryable<Guid> UniqueScopes() => db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(i => i.ListId == list && i.HasUniquePermissions).Select(i => i.Id);
// --- page queries
IQueryable<ListItem> Folder(IQueryable<ListItem> q) => q.Where(i => i.ParentId == folder).OrderByDescending(i => i.IsFolder).ThenBy(i => i.Title).Take(101);
IQueryable<ListItem> AllById(IQueryable<ListItem> q) => q.Where(i => !i.IsFolder).OrderBy(i => i.Id).Take(101);
IQueryable<ListItem> Items() => db.Items.AsNoTracking().Where(i => i.ListId == list);
IQueryable<ListItem> Today(List<Guid> allowed) => Items().Where(i => i.ScopeId == null || allowed.Contains(i.ScopeId.Value));
IQueryable<ListItem> AArray(List<Guid> allowed) => Items().Where(i => allowed.Contains(i.ScopeId!.Value));
IQueryable<ListItem> ASubquery() => Items().Where(i => db.Grants.Where(g => principals.Contains(g.PrincipalId) && g.ListId == list).Select(g => (Guid?)g.ObjectId).Contains(i.ScopeId));

if (mode == "ddl")
{
    Console.WriteLine(db.Database.GenerateCreateScript());
    return;
}

if (mode == "sql")
{
    var three = new List<Guid> { Md5Guid("hf5"), Md5Guid("pub"), Md5Guid("x") };
    void Show(string label, IQueryable q) { Console.WriteLine($"----- {label}\n{q.ToQueryString()}\n"); }
    Show("today preload 1 (any unique)", AnyUnique());
    Show("today preload 3 (grants of user)", Grants());
    Show("today preload 4 (all unique scope ids)", UniqueScopes());
    Show("today page: ScopeId IS NULL OR allowed.Contains", Folder(Today(three)));
    Show("option A: allowed.Contains(ScopeId)", AllById(AArray(three)));
    Show("option A: subquery on ACL", AllById(ASubquery()));
    return;
}

async Task<(double Ms, int Rows, int Opens)> Time(string label, Func<Task<int>> run, int n = 5)
{
    await run(); // warm
    var times = new List<double>(); int rows = 0; int opens = counter.Opened;
    for (var k = 0; k < n; k++) { var sw = Stopwatch.StartNew(); rows = await run(); times.Add(sw.Elapsed.TotalMilliseconds); }
    times.Sort();
    var median = times[n / 2];
    Console.WriteLine($"@@ ef {provider} {label}: {median:0.00} ms (rows={rows}, connection opens per run={(counter.Opened - opens) / n})");
    return (median, rows, (counter.Opened - opens) / n);
}

if (mode == "run")
{
    var allowedToday = await Grants().Select(g => g.ObjectId).ToListAsync();
    await Time("today preload 1 any unique", async () => (await AnyUnique().AnyAsync()) ? 1 : 0);
    await Time("today preload 3 grants", async () => (await Grants().ToListAsync()).Count);
    await Time("today preload 4 all unique scope ids", async () => (await UniqueScopes().ToListAsync()).Count, 3);
    await Time("today folder page", async () => (await Folder(Today(allowedToday)).ToListAsync()).Count);
    await Time("today all docs by id", async () => (await AllById(Today(allowedToday)).ToListAsync()).Count);
    return;
}

if (mode == "runA") // after scope_id = coalesce(scope_id, list_id) and pseudo-principal grants for list scopes
{
    var allowed = await db.Grants.Where(g => principals.Contains(g.PrincipalId) && g.ListId == list).Select(g => g.ObjectId).Distinct().ToListAsync();
    await Time("A allowed set lookup", async () => (await db.Grants.Where(g => principals.Contains(g.PrincipalId) && g.ListId == list).Select(g => g.ObjectId).Distinct().ToListAsync()).Count);
    await Time($"A array ({allowed.Count}) folder page", async () => (await Folder(AArray(allowed)).ToListAsync()).Count);
    await Time($"A array ({allowed.Count}) all docs by id", async () => (await AllById(AArray(allowed)).ToListAsync()).Count);
    await Time("A subquery folder page", async () => (await Folder(ASubquery()).ToListAsync()).Count);
    await Time("A subquery all docs by id", async () => (await AllById(ASubquery()).ToListAsync()).Count);
    return;
}

if (mode == "runJson")
{
    var allowed = await db.Grants.Where(g => principals.Contains(g.PrincipalId) && g.ListId == list).Select(g => g.ObjectId).Distinct().ToListAsync();
    IQueryable<ListItem> AJson() => Items().Where(i => EF.Parameter(allowed).Contains(i.ScopeId!.Value));
    Console.WriteLine(AllById(AJson()).ToQueryString());
    await Time($"A EF.Parameter ({allowed.Count}) folder page", async () => (await Folder(AJson()).ToListAsync()).Count);
    await Time($"A EF.Parameter ({allowed.Count}) all docs by id", async () => (await AllById(AJson()).ToListAsync()).Count);
    return;
}

if (mode == "opens")
{
    int before = counter.Opened;
    await AnyUnique().AnyAsync(); await Grants().ToListAsync(); await Folder(Today([Md5Guid("hf5")])).ToListAsync();
    await db.Items.Where(i => i.Id == folder).ToListAsync(); await db.Grants.Where(g => g.ObjectId == list).ToListAsync();
    Console.WriteLine($"@@ ef {provider} 5 queries outside a transaction: {counter.Opened - before} connection opens");
    before = counter.Opened;
    await using (var tx = await db.Database.BeginTransactionAsync())
    {
        await AnyUnique().AnyAsync(); await Grants().ToListAsync(); await Folder(Today([Md5Guid("hf5")])).ToListAsync();
        await db.Items.Where(i => i.Id == folder).ToListAsync(); await db.Grants.Where(g => g.ObjectId == list).ToListAsync();
        await tx.RollbackAsync();
    }
    Console.WriteLine($"@@ ef {provider} 5 queries inside one transaction: {counter.Opened - before} connection opens");
    return;
}

static Guid Md5Guid(string s) => Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant().Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-"));

sealed record Tenant(Guid? TenantId) : ITenantContext { public string? TenantIdentifier => "default"; }
sealed record User(Guid? UserId) : ICurrentUser;

/// <summary>Counts EF's ConnectionOpened log events (the RLS interceptor runs on each of them).</summary>
sealed class EventCounter : ILoggerProvider, ILogger
{
    public int Opened;
    public ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (eventId.Name == "Microsoft.EntityFrameworkCore.Database.Connection.ConnectionOpened") Interlocked.Increment(ref Opened);
    }
}
