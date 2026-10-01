using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PaperDotNet.IntegrationTests;

/// <summary>MCP server (API-08) and MCP tools from extensions (API-09).</summary>
public sealed class McpTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>An MCP client that talks to the test server with the given API client's token.</summary>
    private static async Task<McpClient> ConnectAsync(HttpClient api)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(api.BaseAddress!, "/v1.0/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            api,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return JsonElement.Parse(text);
    }

    private static async Task<string> ListFromTemplateAsync(HttpClient client, string ws, string name, string templateKey)
    {
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<List<string>> ToolNamesAsync(McpClient client) =>
        [.. (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name)];

    [Fact]
    public async Task Assistants_search_read_and_change_items_with_the_callers_permissions()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Projects");
        var list = (await Api.CreateListAsync(_admin, ws, "Todos", new[] { new { name = "state", type = "text" } })).Id();
        await Api.CreateItemAsync(_admin, ws, list, new { title = "Order quokkafeed", state = "open" });

        await using var mcp = await ConnectAsync(_admin);
        var tools = await ToolNamesAsync(mcp);
        Assert.Contains("search", tools);
        Assert.Contains("query_items", tools);
        Assert.Contains("create_item", tools);
        Assert.DoesNotContain("tests_tickets_count", tools); // the extension is not enabled here

        var workspaces = await CallAsync(mcp, "list_workspaces", []);
        Assert.Contains(workspaces.GetProperty("workspaces").EnumerateArray(), w => w.GetProperty("name").GetString() == "Projects");
        var lists = await CallAsync(mcp, "list_lists", new() { ["workspaceId"] = ws });
        Assert.Contains(lists.GetProperty("lists").EnumerateArray(), l => l.GetProperty("id").GetString() == list);

        var created = await CallAsync(mcp, "create_item", new()
        {
            ["workspaceId"] = ws,
            ["listId"] = list,
            ["fields"] = new Dictionary<string, object> { ["title"] = "Call the vet", ["state"] = "open" },
        });
        var id = created.GetProperty("id").GetString();
        await CallAsync(mcp, "update_item", new()
        {
            ["workspaceId"] = ws,
            ["listId"] = list,
            ["itemId"] = id,
            ["fields"] = new Dictionary<string, object> { ["state"] = "done" },
        });
        var open = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws, ["listId"] = list, ["filter"] = "fields/state eq 'open'" });
        Assert.Equal(["Order quokkafeed"], open.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement hits;
        do
        {
            hits = (await CallAsync(mcp, "search", new() { ["query"] = "quokkafeed" })).GetProperty("hits");
            await Task.Delay(200, Ct);
        }
        while (hits.GetArrayLength() == 0 && DateTime.UtcNow < deadline);
        Assert.Equal("Order quokkafeed", hits[0].GetProperty("title").GetString());

        // Errors are reported to the assistant, not thrown.
        var missing = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["workspaceId"] = ws, ["listId"] = list }, cancellationToken: Ct);
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task Mcp_needs_a_token_and_stays_in_the_tenant()
    {
        var b = await _host.CreateTenantAsync("mcp-isolation-b");
        var ws = await Api.CreateWorkspaceAsync(_admin, "Secret");
        var list = (await Api.CreateListAsync(_admin, ws, "Notes", new[] { new { name = "x", type = "text" } })).Id();

        var anonymous = _host.CreateClient();
        using var response = await anonymous.PostAsJsonAsync("/v1.0/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var mcp = await ConnectAsync(b);
        var result = await mcp.CallToolAsync("query_items", new Dictionary<string, object?> { ["workspaceId"] = ws, ["listId"] = list }, cancellationToken: Ct);
        Assert.True(result.IsError);
        var workspaces = await CallAsync(mcp, "list_workspaces", []);
        Assert.DoesNotContain(workspaces.GetProperty("workspaces").EnumerateArray(), w => w.GetProperty("id").GetString() == ws);
    }

    [Fact]
    public async Task Extension_tools_appear_where_the_extension_is_enabled()
    {
        var other = await _host.CreateTenantAsync("mcp-extension-off");
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/v1.0/extensions/{Extension.TicketsExtension.Id}/enable", null, Ct)).StatusCode);

        await using var mcp = await ConnectAsync(_admin);
        Assert.Contains("tests_tickets_count", await ToolNamesAsync(mcp));
        var count = await CallAsync(mcp, "tests_tickets_count", []);
        Assert.Equal(JsonValueKind.Number, count.GetProperty("added").ValueKind);

        await using (var elsewhere = await ConnectAsync(other))
        {
            Assert.DoesNotContain("tests_tickets_count", await ToolNamesAsync(elsewhere));
            Assert.True((await elsewhere.CallToolAsync("tests_tickets_count", new Dictionary<string, object?>(), cancellationToken: Ct)).IsError);
        }

        await _admin.PostAsync($"/v1.0/extensions/{Extension.TicketsExtension.Id}/disable", null, Ct);
        Assert.DoesNotContain("tests_tickets_count", await ToolNamesAsync(mcp));
    }

    [Fact]
    public async Task Agents_can_page_describe_and_organize_folders()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Work");
        var tasks = await ListFromTemplateAsync(_admin, ws, "Tasks", "tasks");
        var list = (await Api.CreateListAsync(_admin, ws, "Notes", new[] { new { name = "state", type = "text" } })).Id();

        await using var mcp = await ConnectAsync(_admin);
        var home = await CallAsync(mcp, "get_home", []);
        Assert.NotEqual(Guid.Empty, home.GetProperty("documentsListId").GetGuid());

        var lists = await CallAsync(mcp, "list_lists", new() { ["workspaceId"] = ws });
        var taskList = lists.GetProperty("lists").EnumerateArray().Single(item => item.GetProperty("id").GetString() == tasks);
        Assert.Equal("task", taskList.GetProperty("contentTypes")[0].GetProperty("key").GetString());

        var described = await CallAsync(mcp, "describe_list", new() { ["workspaceId"] = ws, ["listId"] = tasks });
        var status = described.GetProperty("contentTypes")[0].GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("name").GetString() == "status");
        Assert.Contains("notStarted", status.GetProperty("choices").EnumerateArray().Select(choice => choice.GetString()));
        Assert.True(described.GetProperty("contentTypes")[0].GetProperty("fields").EnumerateArray().First().GetProperty("required").GetBoolean());

        await CallAsync(mcp, "create_item", new() { ["workspaceId"] = ws, ["listId"] = list, ["fields"] = new Dictionary<string, object> { ["title"] = "one" } });
        await CallAsync(mcp, "create_item", new() { ["workspaceId"] = ws, ["listId"] = list, ["fields"] = new Dictionary<string, object> { ["title"] = "two" } });
        await CallAsync(mcp, "create_item", new() { ["workspaceId"] = ws, ["listId"] = list, ["fields"] = new Dictionary<string, object> { ["title"] = "three" } });
        var first = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws, ["listId"] = list, ["top"] = 2 });
        Assert.Equal(["one", "two"], Titles(first));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws, ["listId"] = list, ["top"] = 2, ["cursor"] = cursor });
        Assert.Equal(["three"], Titles(second));
        Assert.False(second.TryGetProperty("nextCursor", out _));
        var invalid = await mcp.CallToolAsync("query_items", new Dictionary<string, object?> { ["workspaceId"] = ws, ["listId"] = list, ["cursor"] = "nope" }, cancellationToken: Ct);
        Assert.True(invalid.IsError);

        var folder = await CallAsync(mcp, "create_folder", new() { ["workspaceId"] = ws, ["listId"] = list, ["title"] = "Contracts" });
        var folderId = folder.GetProperty("id").GetString();
        var nested = await CallAsync(mcp, "create_item", new()
        {
            ["workspaceId"] = ws,
            ["listId"] = list,
            ["parentId"] = folderId,
            ["fields"] = new Dictionary<string, object> { ["title"] = "signed" },
        });
        var children = await CallAsync(mcp, "list_children", new() { ["workspaceId"] = ws, ["listId"] = list, ["folderId"] = folderId });
        Assert.Equal(["signed"], Titles(children));
        var ensured = await CallAsync(mcp, "ensure_folder", new() { ["workspaceId"] = ws, ["listId"] = list, ["path"] = "Contracts/2026" });
        Assert.NotEqual(folderId, ensured.GetProperty("folderId").GetString());

        var blocked = await mcp.CallToolAsync("delete_item", new Dictionary<string, object?> { ["workspaceId"] = ws, ["listId"] = list, ["itemId"] = folderId }, cancellationToken: Ct);
        Assert.True(blocked.IsError);
        await CallAsync(mcp, "delete_item", new() { ["workspaceId"] = ws, ["listId"] = list, ["itemId"] = ensured.GetProperty("folderId").ToString() });
        await CallAsync(mcp, "move_item", new() { ["workspaceId"] = ws, ["listId"] = list, ["itemId"] = nested.GetProperty("id").ToString() });
        var root = await CallAsync(mcp, "list_children", new() { ["workspaceId"] = ws, ["listId"] = list });
        Assert.Contains(Titles(root), title => title == "signed");
        await CallAsync(mcp, "delete_item", new() { ["workspaceId"] = ws, ["listId"] = list, ["itemId"] = nested.GetProperty("id").ToString() });
        var after = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws, ["listId"] = list, ["filter"] = "fields/title eq 'signed'" });
        Assert.Empty(Titles(after));
    }

    [Fact]
    public async Task Agents_can_upload_and_read_library_files()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Records");
        var library = await ListFromTemplateAsync(_admin, ws, "Documents", "documents");
        var plain = (await Api.CreateListAsync(_admin, ws, "Plain")).Id();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% marker\n%%EOF\n");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

        await using var mcp = await ConnectAsync(_admin);
        var rejected = await mcp.CallToolAsync("upload_document", new Dictionary<string, object?>
        {
            ["workspaceId"] = ws,
            ["listId"] = plain,
            ["fileName"] = "a.pdf",
            ["contentBase64"] = Convert.ToBase64String(pdf),
        }, cancellationToken: Ct);
        Assert.True(rejected.IsError);

        var uploaded = await CallAsync(mcp, "upload_document", new()
        {
            ["workspaceId"] = ws,
            ["listId"] = library,
            ["fileName"] = "Invoice 42.pdf",
            ["title"] = "Invoice 42",
            ["contentBase64"] = Convert.ToBase64String(pdf),
        });
        var itemId = uploaded.GetProperty("itemId").GetString()!;
        Assert.Equal("Invoice 42.pdf", uploaded.GetProperty("file").GetProperty("fileName").GetString());
        var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
        var file = await CallAsync(mcp, "get_file", new() { ["workspaceId"] = ws, ["listId"] = library, ["itemId"] = itemId });
        Assert.Equal(sha, file.GetProperty("file").GetProperty("sha256").GetString());

        // The file is only a marker, not a readable PDF: the library's workflows find no text, and the tool says so.
        await WorkflowRunsAsync(_admin, ws, itemId, 3);
        var text = await CallAsync(mcp, "read_document", new() { ["workspaceId"] = ws, ["listId"] = library, ["itemId"] = itemId });
        Assert.Equal(0, text.GetProperty("pages").GetArrayLength());
        Assert.StartsWith("No text is stored", text.GetProperty("message").GetString(), StringComparison.Ordinal);

        var stale = await mcp.CallToolAsync("replace_document", new Dictionary<string, object?>
        {
            ["workspaceId"] = ws,
            ["listId"] = library,
            ["itemId"] = itemId,
            ["fileName"] = "scan.png",
            ["contentBase64"] = Convert.ToBase64String(png),
            ["sha256"] = "0000",
        }, cancellationToken: Ct);
        Assert.True(stale.IsError);

        var replaced = await CallAsync(mcp, "replace_document", new()
        {
            ["workspaceId"] = ws,
            ["listId"] = library,
            ["itemId"] = itemId,
            ["fileName"] = "scan.png",
            ["contentBase64"] = Convert.ToBase64String(png),
            ["sha256"] = sha,
        });
        Assert.Equal(2, replaced.GetProperty("file").GetProperty("number").GetInt32());
        Assert.Equal("image/png", replaced.GetProperty("file").GetProperty("mediaType").GetString());
    }

    [Fact]
    public async Task A_read_only_token_hides_write_tools()
    {
        using var created = await _admin.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "assistant", scopes = new[] { "mcp.use", "list.read", "workspace.read", "document.read", "search.read" } }, Ct);
        var secret = (await created.JsonAsync(HttpStatusCode.Created)).GetProperty("secret").GetString();
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        await using var mcp = await ConnectAsync(client);
        var tools = (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).ToList();
        Assert.Contains("describe_list", tools);
        Assert.Contains("read_document", tools);
        Assert.DoesNotContain("create_item", tools);
        Assert.DoesNotContain("upload_document", tools);
        Assert.DoesNotContain("delete_item", tools);
    }

    /// <summary>Waits until the item's workflow runs (the library's built-in document workflows) are finished.</summary>
    private static async Task WorkflowRunsAsync(HttpClient client, string ws, string item, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var runs = (await (await client.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?itemId={item}", Ct)).JsonAsync(HttpStatusCode.OK))
                .GetProperty("value").EnumerateArray().ToList();
            if (runs.Count >= count && runs.All(r => r.GetProperty("status").GetString() is "completed" or "failed"))
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, string.Join(", ", runs.Select(r => $"{r.GetProperty("status")} {r.GetProperty("error")}")));
            await Task.Delay(200, Ct);
        }
    }

    private static IEnumerable<string?> Titles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("fields").GetProperty("title").GetString());
}
