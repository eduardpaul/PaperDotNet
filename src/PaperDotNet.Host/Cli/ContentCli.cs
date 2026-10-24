using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Content;
using PaperDotNet.Persistence.PostgreSql.Content;
using PaperDotNet.Persistence.Sqlite.Content;

namespace PaperDotNet.Host.Cli;

/// <summary>Local database maintenance, deliberately independent of the modular application's bootstrap.</summary>
internal static class ContentCli
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<int> RunAsync(string[] args) => Build().Parse(args[1..]).InvokeAsync();

    private static RootCommand Build()
    {
        var root = new RootCommand("Maintain a PaperGo-compatible organization database through Entity Framework. Local database administrator access; does not start the web host.");
        var provider = new Option<string>("--provider") { Recursive = true, DefaultValueFactory = _ => "sqlite", Description = "sqlite or postgresql" };
        var connection = new Option<string>("--connection") { Recursive = true, Description = "Connection string; alternatively set PAPERDOTNET_CONTENT_CONNECTION_STRING." };
        var actor = new Option<string>("--actor") { Recursive = true, DefaultValueFactory = _ => "local-admin", Description = "Actor recorded in revisions and audit; local maintenance bypasses user grants." };
        root.Options.Add(provider); root.Options.Add(connection); root.Options.Add(actor);

        void Add(string name, string description, Option[] options, Func<ContentStore, ContentDbContext, ParseResult, CancellationToken, Task<object>> run)
        {
            var command = new Command(name, description);
            foreach (var option in options) command.Options.Add(option);
            command.SetAction(async (parse, ct) =>
            {
                var cs = parse.GetValue(connection) ?? Environment.GetEnvironmentVariable("PAPERDOTNET_CONTENT_CONNECTION_STRING");
                if (string.IsNullOrWhiteSpace(cs)) { Console.Error.WriteLine("Supply --connection or PAPERDOTNET_CONTENT_CONNECTION_STRING."); return 2; }
                try
                {
                    await using ContentDbContext db = parse.GetValue(provider) switch
                    {
                        "sqlite" => SqliteContentDbContext.Open(cs),
                        "postgresql" => PostgreSqlContentDbContext.Open(cs),
                        _ => throw new ArgumentException("Provider must be sqlite or postgresql."),
                    };
                    if (db is SqliteContentDbContext sqlite) await sqlite.InitializeAsync(ct);
                    else await ((PostgreSqlContentDbContext)db).InitializeAsync(ct);
                    var store = new ContentStore(db, new LocalDatabaseAdministrator());
                    var result = await run(store, db, parse, ct);
                    Console.WriteLine(JsonSerializer.Serialize(result, OutputJson));
                    return 0;
                }
                catch (DbUpdateConcurrencyException) { Console.Error.WriteLine("Version conflict. Reload the resource before retrying."); return 3; }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or JsonException)
                {
                    Console.Error.WriteLine(exception.Message); return 2;
                }
            });
            root.Subcommands.Add(command);
        }
        string Actor(ParseResult parse) => parse.GetValue(actor)!;
        Add("init", "Initialize an empty database or verify the pinned fingerprint of an existing one.", [],
            (_, _, _, _) => Task.FromResult<object>(new { reference = ContentDbContext.ReferenceCommit, status = "compatible" }));
        var workspaceName = Required<string>("--name");
        Add("workspace", "Create a workspace.", [workspaceName], async (store, _, parse, ct) =>
            await store.CreateWorkspaceAsync(Actor(parse), parse.GetValue(workspaceName)!, ct));
        var workspace = Required<string>("--workspace"); var collectionName = Required<string>("--name");
        var library = new Option<bool>("--library"); var publishing = new Option<bool>("--publishing");
        Add("collection", "Create a list or library; history defaults off.", [workspace, collectionName, library, publishing], async (store, _, parse, ct) =>
            await store.CreateCollectionAsync(Actor(parse), parse.GetValue(workspace)!, parse.GetValue(collectionName)!, parse.GetValue(library), parse.GetValue(publishing), cancellationToken: ct));
        var collection = Required<string>("--collection"); var version = Required<long>("--version"); var schemaFile = Required<string>("--input");
        Add("schema", "Record an effective schema snapshot and update its catalog entries from a JSON file.", [collection, version, schemaFile], async (store, _, parse, ct) =>
            await store.RecordSchemaAsync(Actor(parse), parse.GetValue(collection)!, parse.GetValue(version), await ReadInputAsync<EffectiveSchema>(parse.GetValue(schemaFile)!, ct), ct));
        var parent = Required<string>("--parent"); var contentFile = Required<string>("--input"); var folder = new Option<bool>("--folder");
        Add("create", "Create an item or folder from ContentInput JSON.", [parent, contentFile, folder], async (store, _, parse, ct) =>
            await store.CreateItemAsync(Actor(parse), parse.GetValue(parent)!, await ReadInputAsync<ContentInput>(parse.GetValue(contentFile)!, ct), parse.GetValue(folder), ct));
        var editId = Required<string>("--id"); var editVersion = Required<long>("--version"); var editFile = Required<string>("--input");
        Add("edit", "Record a new content snapshot from ContentInput JSON (full replacement of values).", [editId, editVersion, editFile], async (store, _, parse, ct) =>
            await store.EditAsync(Actor(parse), parse.GetValue(editId)!, parse.GetValue(editVersion), await ReadInputAsync<ContentInput>(parse.GetValue(editFile)!, ct), ct));
        foreach (var name in new[] { "publish", "unpublish", "archive", "unarchive" })
        {
            var id = Required<string>("--id"); var expected = Required<long>("--version");
            Add(name, $"{name} an item, checking its current version.", [id, expected], async (store, _, parse, ct) => name switch
            {
                "publish" => await store.PublishAsync(Actor(parse), parse.GetValue(id)!, parse.GetValue(expected), ct),
                "unpublish" => await store.UnpublishAsync(Actor(parse), parse.GetValue(id)!, parse.GetValue(expected), ct),
                _ => await store.SetArchivedAsync(Actor(parse), parse.GetValue(id)!, parse.GetValue(expected), name == "archive", ct),
            });
        }
        var historyId = Required<string>("--id");
        Add("history", "Read retained versions with their frozen schemas.", [historyId], async (store, _, parse, ct) =>
            await store.HistoryAsync(Actor(parse), parse.GetValue(historyId)!, cancellationToken: ct));
        var restoreId = Required<string>("--id"); var restoreVersion = Required<long>("--version"); var number = Required<long>("--number");
        Add("restore-revision", "Restore retained metadata through the current schema, creating a new revision.", [restoreId, restoreVersion, number], async (store, _, parse, ct) =>
            await store.RestoreHistoryAsync(Actor(parse), parse.GetValue(restoreId)!, parse.GetValue(restoreVersion), parse.GetValue(number), ct));
        var settingsId = Required<string>("--id"); var settingsVersion = Required<long>("--version"); var enabled = new Option<bool>("--publishing");
        var history = new Option<string>("--history") { DefaultValueFactory = _ => "off" };
        var maximum = new Option<long>("--max-versions") { DefaultValueFactory = _ => 50 };
        Add("configure", "Set publishing and history policy, updating existing item projections atomically.", [settingsId, settingsVersion, enabled, history, maximum], async (store, _, parse, ct) =>
            await store.ConfigureCollectionAsync(Actor(parse), parse.GetValue(settingsId)!, parse.GetValue(settingsVersion), parse.GetValue(enabled), parse.GetValue(history)!, parse.GetValue(maximum), ct));
        var readId = Required<string>("--id"); var readSurface = new Option<string>("--surface") { DefaultValueFactory = _ => "published" };
        Add("read", "Read a content snapshot with the schema that interprets it.", [readId, readSurface], async (store, _, parse, ct) =>
            await store.ReadAsync(Actor(parse), parse.GetValue(readId)!, parse.GetValue(readSurface)!, ct));
        var browseId = Required<string>("--collection"); var browseSurface = new Option<string>("--surface") { DefaultValueFactory = _ => "published" };
        var archive = new Option<ArchiveFilter>("--archive") { DefaultValueFactory = _ => ArchiveFilter.Active };
        Add("browse", "Browse active, archived or all content on a chosen surface.", [browseId, browseSurface, archive], async (store, _, parse, ct) =>
            await store.BrowseAsync(Actor(parse), parse.GetValue(browseId)!, parse.GetValue(browseSurface)!, parse.GetValue(archive), cancellationToken: ct));
        return root;
    }

    private static Option<T> Required<T>(string name) => new(name) { Required = true };

    private static async Task<T> ReadInputAsync<T>(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), OutputJson) ?? throw new JsonException("Input is null.");

    private sealed class LocalDatabaseAdministrator : IContentAuthorization
    {
        public Task AuthorizeAsync(string actor, ResourcesRow resource, string action, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is required.", nameof(actor));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
