using System.CommandLine;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PaperDotNet.Client;

namespace PaperDotNet.Cli;

/// <summary>Where and as whom the CLI connects: <c>--url</c>, <c>--token</c> (API or OAuth token) and <c>--tenant</c>.</summary>
public sealed record CliConnection(Uri Url, string Token, string? Tenant);

/// <summary>
/// The client CLI (API-13): <c>pdn workspaces | libraries | upload | download | search</c>. Connection settings come
/// from options or the environment (<c>PAPERDOTNET_URL</c>, <c>PAPERDOTNET_TOKEN</c>, <c>PAPERDOTNET_TENANT</c>).
/// </summary>
public static class ClientCli
{
    /// <summary>Runs the CLI; <paramref name="connect"/> creates the HTTP client (tests pass an in-memory one).</summary>
    public static Task<int> RunAsync(string[] args, Func<CliConnection, HttpClient>? connect = null, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        connect ??= connection => new HttpClient { BaseAddress = connection.Url, Timeout = TimeSpan.FromMinutes(30) };

        var url = new Option<string?>("--url") { Description = "Address of the PaperDotNet installation (or PAPERDOTNET_URL).", Recursive = true };
        var token = new Option<string?>("--token") { Description = "API token or OAuth access token (or PAPERDOTNET_TOKEN).", Recursive = true };
        var tenant = new Option<string?>("--tenant") { Description = "Tenant, when the host name does not select it (or PAPERDOTNET_TENANT).", Recursive = true };
        var root = new RootCommand("PaperDotNet client: upload folders, download files and search.") { url, token, tenant };

        async Task<int> WithSessionAsync(ParseResult parse, Func<CliSession, Task<int>> action, CancellationToken ct)
        {
            var address = parse.GetValue(url) ?? Environment.GetEnvironmentVariable("PAPERDOTNET_URL");
            var secret = parse.GetValue(token) ?? Environment.GetEnvironmentVariable("PAPERDOTNET_TOKEN");
            if (!Uri.TryCreate(address, UriKind.Absolute, out var baseUri) || string.IsNullOrWhiteSpace(secret))
            {
                await error.WriteLineAsync("Set --url and --token (or PAPERDOTNET_URL and PAPERDOTNET_TOKEN).");
                return 2;
            }

            var connection = new CliConnection(baseUri, secret, parse.GetValue(tenant) ?? Environment.GetEnvironmentVariable("PAPERDOTNET_TENANT"));
            using var http = connect(connection);
            var session = new CliSession(http, connection, output, error);
            try
            {
                return await action(session);
            }
            catch (CliException ex)
            {
                await error.WriteLineAsync(ex.Message);
                return 1;
            }
            catch (HttpRequestException ex)
            {
                await error.WriteLineAsync($"The server could not be reached: {ex.Message}");
                return 1;
            }
        }

        var workspaces = new Command("workspaces", "List the workspaces you can see.");
        workspaces.SetAction((parse, ct) => WithSessionAsync(parse, s => s.WorkspacesAsync(ct), ct));
        root.Subcommands.Add(workspaces);

        var workspace = new Option<string>("--workspace", "-w") { Description = "Workspace id or name.", Required = true };
        var libraries = new Command("libraries", "List the document libraries of a workspace.") { workspace };
        libraries.SetAction((parse, ct) => WithSessionAsync(parse, s => s.LibrariesAsync(parse.GetValue(workspace)!, ct), ct));
        root.Subcommands.Add(libraries);

        var paths = new Argument<string[]>("paths") { Description = "Files or folders (folders are uploaded with their sub-folders).", Arity = ArgumentArity.OneOrMore };
        var uploadWorkspace = new Option<string?>("--workspace", "-w") { Description = "Workspace id or name." };
        var library = new Option<string?>("--library", "-l") { Description = "Library id or name." };
        var folder = new Option<string?>("--folder", "-f") { Description = "Folder path in the library, e.g. 2026/Invoices (created where missing)." };
        var inbox = new Option<bool>("--inbox") { Description = "Upload into your Inbox instead of a library." };
        var languages = new Option<string?>("--languages") { Description = "OCR languages of the files, e.g. deu+eng." };
        var upload = new Command("upload", "Upload files, or folders with their structure, into a library or your Inbox.")
        {
            paths, uploadWorkspace, library, folder, inbox, languages,
        };
        upload.SetAction((parse, ct) => WithSessionAsync(parse, s => s.UploadAsync(
            parse.GetValue(paths)!, parse.GetValue(inbox), parse.GetValue(uploadWorkspace), parse.GetValue(library), parse.GetValue(folder),
            parse.GetValue(languages), ct), ct));
        root.Subcommands.Add(upload);

        var downloadWorkspace = new Option<string>("--workspace", "-w") { Description = "Workspace id or name.", Required = true };
        var downloadLibrary = new Option<string>("--library", "-l") { Description = "Library id or name.", Required = true };
        var item = new Option<Guid>("--item", "-i") { Description = "Item id.", Required = true };
        var outputPath = new Option<string?>("--output", "-o") { Description = "File or folder to write to (default: the file's name here)." };
        var download = new Command("download", "Download a document's current file.") { downloadWorkspace, downloadLibrary, item, outputPath };
        download.SetAction((parse, ct) => WithSessionAsync(parse, s => s.DownloadAsync(
            parse.GetValue(downloadWorkspace)!, parse.GetValue(downloadLibrary)!, parse.GetValue(item), parse.GetValue(outputPath), ct), ct));
        root.Subcommands.Add(download);

        var query = new Argument<string>("query") { Description = "Search text (phrases in quotes, OR, -word)." };
        var top = new Option<int>("--top") { Description = "How many hits to show.", DefaultValueFactory = _ => 20 };
        var mode = new Option<string?>("--mode") { Description = "keyword, semantic or hybrid (default: the server's)." };
        var search = new Command("search", "Search everything you can see.") { query, top, mode };
        search.SetAction((parse, ct) => WithSessionAsync(parse, s => s.SearchAsync(parse.GetValue(query)!, parse.GetValue(top), parse.GetValue(mode), ct), ct));
        root.Subcommands.Add(search);

        var configuration = new InvocationConfiguration { Output = output, Error = error };
        return root.Parse(args).InvokeAsync(configuration);
    }
}

internal sealed class CliException(string message) : Exception(message);

/// <summary>One connection: typed calls through the SDK, multipart uploads and downloads through the same HTTP client.</summary>
internal sealed class CliSession
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".tif", ".tiff", ".jpg", ".jpeg", ".png" };

    private readonly HttpClient _http;
    private readonly PaperDotNetApiClient _api;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public CliSession(HttpClient http, CliConnection connection, TextWriter output, TextWriter error)
    {
        _http = http;
        _api = PaperDotNetClient.Create(http, connection.Token, connection.Tenant);
        _output = output;
        _error = error;
    }

    public async Task<int> WorkspacesAsync(CancellationToken ct)
    {
        foreach (var (id, name) in await WorkspaceListAsync(ct))
        {
            await _output.WriteLineAsync($"{id}  {name}");
        }

        return 0;
    }

    public async Task<int> LibrariesAsync(string workspace, CancellationToken ct)
    {
        var workspaceId = await ResolveWorkspaceAsync(workspace, ct);
        foreach (var list in await LibraryListAsync(workspaceId, ct))
        {
            await _output.WriteLineAsync($"{list.Id}  {list.Name}");
        }

        return 0;
    }

    public async Task<int> UploadAsync(
        string[] paths, bool inbox, string? workspace, string? library, string? folder, string? languages, CancellationToken ct)
    {
        if (inbox == (workspace is not null || library is not null) || (!inbox && (workspace is null || library is null)))
        {
            throw new CliException("Upload into a library (--workspace and --library) or into your Inbox (--inbox).");
        }

        string url;
        FolderIndex? folders = null;
        Guid? baseFolder = null;
        if (inbox)
        {
            url = "v1.0/me/inbox/documents";
        }
        else
        {
            var workspaceId = await ResolveWorkspaceAsync(workspace!, ct);
            var listId = await ResolveLibraryAsync(workspaceId, library!, ct);
            url = $"v1.0/workspaces/{workspaceId}/lists/{listId}/documents";
            folders = await FolderIndex.LoadAsync(_http, $"v1.0/workspaces/{workspaceId}/lists/{listId}/items", ct);
            baseFolder = await folders.EnsureAsync(Segments(folder), ct);
        }

        var failures = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var rootDirectory = Path.GetFullPath(path);
                foreach (var file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(rootDirectory, Path.GetDirectoryName(file)!);
                    var target = folders is null ? null : await folders.EnsureAsync([.. Segments(folder), .. Segments(relative)], ct);
                    failures += await UploadFileAsync(url, file, target ?? baseFolder, languages, ct) ? 0 : 1;
                }
            }
            else if (File.Exists(path))
            {
                failures += await UploadFileAsync(url, path, baseFolder, languages, ct) ? 0 : 1;
            }
            else
            {
                await _error.WriteLineAsync($"{path}: not found");
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    public async Task<int> DownloadAsync(string workspace, string library, Guid item, string? output, CancellationToken ct)
    {
        var workspaceId = await ResolveWorkspaceAsync(workspace, ct);
        var listId = await ResolveLibraryAsync(workspaceId, library, ct);
        using var response = await _http.GetAsync($"v1.0/workspaces/{workspaceId}/lists/{listId}/items/{item}/file", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "download", ct);
        var name = Path.GetFileName(response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? $"{item}");
        var path = output is null ? name : Directory.Exists(output) ? Path.Combine(output, name) : output;
        await using (var file = File.Create(path))
        {
            await response.Content.CopyToAsync(file, ct);
        }

        await _output.WriteLineAsync(path);
        return 0;
    }

    public async Task<int> SearchAsync(string query, int top, string? mode, CancellationToken ct)
    {
        var result = await _api.V10.Search.GetAsync(r =>
        {
            r.QueryParameters.Q = query;
            r.QueryParameters.Mode = mode;
        }, ct);
        foreach (var hit in (result?.Value ?? []).Take(Math.Max(1, top)))
        {
            var page = hit.Page is { } number ? $" (page {number})" : string.Empty;
            await _output.WriteLineAsync($"{hit.Title}{page}  {hit.WorkspaceId}/{hit.ContainerId}/{hit.Id}");
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
            {
                await _output.WriteLineAsync($"    {hit.Snippet.ReplaceLineEndings(" ")}");
            }
        }

        return 0;
    }

    private async Task<bool> UploadFileAsync(string url, string path, Guid? folderId, string? languages, CancellationToken ct)
    {
        if (!Supported.Contains(Path.GetExtension(path)))
        {
            await _output.WriteLineAsync($"{path}: skipped (only PDF, TIFF, JPEG and PNG files)");
            return true;
        }

        await using var content = File.OpenRead(path);
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(content), "file", Path.GetFileName(path) },
            { new StringContent(Path.GetFileNameWithoutExtension(path)), "title" },
        };
        if (folderId is { } folder)
        {
            form.Add(new StringContent(folder.ToString()), "folderId");
        }

        if (!string.IsNullOrWhiteSpace(languages))
        {
            form.Add(new StringContent(languages), "languages");
        }

        using var response = await _http.PostAsync(url, form, ct);
        if (!response.IsSuccessStatusCode)
        {
            await _error.WriteLineAsync($"{path}: {await ProblemAsync(response, ct)}");
            return false;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var duplicates = body.TryGetProperty("duplicates", out var list) ? list.GetArrayLength() : 0;
        await _output.WriteLineAsync($"{path}: {body.GetProperty("itemId").GetGuid()}{(duplicates > 0 ? $" (duplicate of {duplicates} existing)" : string.Empty)}");
        return true;
    }

    private async Task<List<(Guid Id, string Name)>> WorkspaceListAsync(CancellationToken ct)
    {
        var result = new List<(Guid, string)>();
        var page = await _api.V10.Workspaces.GetAsync(cancellationToken: ct);
        while (page is not null)
        {
            result.AddRange((page.Value ?? []).Where(w => w.Id is not null).Select(w => (w.Id!.Value, w.Name ?? string.Empty)));
            page = page.OdataNextLink is { Length: > 0 } next ? await _api.V10.Workspaces.WithUrl(next).GetAsync(cancellationToken: ct) : null;
        }

        return result;
    }

    private async Task<List<(Guid Id, string Name)>> LibraryListAsync(Guid workspaceId, CancellationToken ct) =>
        [.. ((await _api.V10.Workspaces[workspaceId].Lists.GetAsync(cancellationToken: ct)) ?? [])
            .Where(l => l.Id is not null && l.Kind == Client.Models.ListKind.Library)
            .Select(l => (l.Id!.Value, l.Name ?? string.Empty))];

    private async Task<Guid> ResolveWorkspaceAsync(string reference, CancellationToken ct) =>
        Resolve(reference, await WorkspaceListAsync(ct), "workspace");

    private async Task<Guid> ResolveLibraryAsync(Guid workspaceId, string reference, CancellationToken ct) =>
        Resolve(reference, await LibraryListAsync(workspaceId, ct), "library");

    private static Guid Resolve(string reference, List<(Guid Id, string Name)> candidates, string kind)
    {
        if (Guid.TryParse(reference, out var id) && candidates.Any(c => c.Id == id))
        {
            return id;
        }

        var matches = candidates.Where(c => string.Equals(c.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new CliException($"No {kind} '{reference}' (or you cannot see it)."),
            _ => throw new CliException($"Several {kind}s are named '{reference}'; use the id."),
        };
    }

    private static List<string> Segments(string? path) =>
        path is null or "." ? [] : [.. path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s != ".")];

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException($"Could not {action}: {await ProblemAsync(response, ct)}");
        }
    }

    internal static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var problem = JsonSerializer.Deserialize<JsonElement>(text, Json);
            var detail = problem.TryGetProperty("detail", out var d) ? d.GetString() : problem.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                detail = string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => $"{e.Name}: {v.GetString()}")));
            }

            return $"{(int)response.StatusCode} {detail ?? response.ReasonPhrase}";
        }
        catch (JsonException)
        {
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
    }
}

/// <summary>The library's folders by parent and title; missing ones are created.</summary>
internal sealed class FolderIndex(HttpClient http, string itemsUrl, Dictionary<(Guid? Parent, string Title), Guid> folders)
{
    public static async Task<FolderIndex> LoadAsync(HttpClient http, string itemsUrl, CancellationToken ct)
    {
        var folders = new Dictionary<(Guid?, string), Guid>();
        var next = $"{itemsUrl}?$filter={Uri.EscapeDataString("isFolder eq true")}&$top=1000";
        while (next is not null)
        {
            using var response = await http.GetAsync(next, ct);
            await CliSession.EnsureSuccessAsync(response, "read the library's folders", ct);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            foreach (var item in page.GetProperty("value").EnumerateArray())
            {
                Guid? parent = item.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null;
                folders[(parent, item.GetProperty("fields").GetProperty("title").GetString()!)] = item.GetProperty("id").GetGuid();
            }

            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        return new FolderIndex(http, itemsUrl, folders);
    }

    /// <summary>The folder at <paramref name="path"/> (null for the library root), created where missing.</summary>
    public async Task<Guid?> EnsureAsync(IReadOnlyList<string> path, CancellationToken ct)
    {
        Guid? current = null;
        foreach (var title in path)
        {
            if (!folders.TryGetValue((current, title), out var id))
            {
                using var response = await http.PostAsJsonAsync(itemsUrl, new { isFolder = true, parentId = current, fields = new { title } }, ct);
                await CliSession.EnsureSuccessAsync(response, $"create the folder '{title}'", ct);
                id = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
                folders[(current, title)] = id;
            }

            current = id;
        }

        return current;
    }
}
