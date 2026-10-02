using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PaperDotNet.IntegrationTests;

/// <summary>MCP server (API-08) and MCP tools from extensions (API-09).</summary>
public sealed class McpTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An MCP client that talks to the test server with the given API client's headers (token and tenant).</summary>
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
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Global_tools_link_items_and_move_them_without_changing_identity()
    {
        await factory.CreateTenantAsync("mcp-global");
        var admin = await ApiClient.CreateAsync(factory, "mcp-global");
        var ws = await admin.CreateWorkspaceAsync("Before");
        var destination = await admin.CreateWorkspaceAsync("After");
        var type = await admin.CreateContentTypeAsync("Record", []);
        var sourceList = await admin.CreateListAsync(ws, "Records", type);
        var targetList = await admin.CreateListAsync(destination, "Records", type);
        var first = (await admin.CreateItemAsync(ws, sourceList, new { fields = new { title = "First" } })).GetProperty("id").GetGuid();
        var second = (await admin.CreateItemAsync(destination, targetList, new { fields = new { title = "Second" } })).GetProperty("id").GetGuid();
        await using var mcp = await ConnectAsync(admin);
        await CallAsync(mcp, "relate_items", new() { ["itemId"] = first.ToString(), ["otherId"] = second.ToString() });
        var inverse = await CallAsync(mcp, "list_related_items", new() { ["itemId"] = second.ToString() });
        Assert.Equal(first, Assert.Single(inverse.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var item = await CallAsync(mcp, "get_global_item", new() { ["itemId"] = first.ToString() });
        var moved = await CallAsync(mcp, "move_item_to_list", new()
        {
            ["itemId"] = first.ToString(),
            ["workspaceId"] = destination.ToString(),
            ["listId"] = targetList.ToString(),
            ["version"] = item.GetProperty("version").GetInt32(),
        });
        Assert.Equal(first, moved.GetProperty("id").GetGuid());
        Assert.Equal(destination, moved.GetProperty("workspaceId").GetGuid());
        var located = await CallAsync(mcp, "get_global_item", new() { ["itemId"] = first.ToString() });
        Assert.Equal(targetList, located.GetProperty("listId").GetGuid());
        await CallAsync(mcp, "relate_items", new() { ["itemId"] = second.ToString(), ["otherId"] = first.ToString(), ["related"] = false });
        var removed = await CallAsync(mcp, "list_related_items", new() { ["itemId"] = first.ToString() });
        Assert.Empty(removed.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Assistants_search_read_and_change_items_with_the_callers_permissions()
    {
        await factory.CreateTenantAsync("mcp-basic");
        var admin = await ApiClient.CreateAsync(factory, "mcp-basic");
        var ws = await admin.CreateWorkspaceAsync("Projects");
        var contentType = await admin.CreateContentTypeAsync("Todo", [new { name = "state", type = "text" }]);
        var list = await admin.CreateListAsync(ws, "Todos", contentType);
        await admin.CreateItemAsync(ws, list, new { fields = new { title = "Order quokkafeed", state = "open" } });

        await using var mcp = await ConnectAsync(admin);
        var tools = (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.Contains("search", tools);
        Assert.Contains("query_items", tools);
        Assert.Contains("create_item", tools);
        Assert.DoesNotContain("samples_invoices_pending", tools); // the extension is not enabled here

        var workspaces = await CallAsync(mcp, "list_workspaces", []);
        Assert.Contains(workspaces.GetProperty("workspaces").EnumerateArray(), w => w.GetProperty("name").GetString() == "Projects");
        var lists = await CallAsync(mcp, "list_lists", new() { ["workspaceId"] = ws.ToString() });
        Assert.Contains(lists.GetProperty("lists").EnumerateArray(), l => l.GetProperty("id").GetGuid() == list);

        var created = await CallAsync(mcp, "create_item", new()
        {
            ["workspaceId"] = ws.ToString(),
            ["listId"] = list.ToString(),
            ["fields"] = new Dictionary<string, object> { ["title"] = "Call the vet", ["state"] = "open" },
        });
        var id = created.GetProperty("id").GetGuid();
        await CallAsync(mcp, "update_item", new()
        {
            ["workspaceId"] = ws.ToString(),
            ["listId"] = list.ToString(),
            ["itemId"] = id.ToString(),
            ["fields"] = new Dictionary<string, object> { ["state"] = "done" },
        });
        var open = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["filter"] = "fields/state eq 'open'" });
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
        var missing = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString() }, cancellationToken: Ct);
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task Mcp_needs_a_token_and_stays_in_the_tenant()
    {
        await factory.CreateTenantAsync("mcp-isolation-a");
        await factory.CreateTenantAsync("mcp-isolation-b");
        var a = await ApiClient.CreateAsync(factory, "mcp-isolation-a");
        var b = await ApiClient.CreateAsync(factory, "mcp-isolation-b");
        var ws = await a.CreateWorkspaceAsync("Secret");
        var contentType = await a.CreateContentTypeAsync("Note", [new { name = "x", type = "text" }]);
        var list = await a.CreateListAsync(ws, "Notes", contentType);

        var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant", "mcp-isolation-a");
        var response = await anonymous.PostAsJsonAsync("/v1.0/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var mcp = await ConnectAsync(b);
        var result = await mcp.CallToolAsync("query_items", new Dictionary<string, object?> { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString() }, cancellationToken: Ct);
        Assert.True(result.IsError);
        var workspaces = await CallAsync(mcp, "list_workspaces", []);
        Assert.DoesNotContain(workspaces.GetProperty("workspaces").EnumerateArray(), w => w.GetProperty("id").GetGuid() == ws);
    }

    [Fact]
    public async Task Extension_tools_appear_where_the_extension_is_enabled()
    {
        await factory.CreateTenantAsync("mcp-extension");
        var admin = await ApiClient.CreateAsync(factory, "mcp-extension");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/v1.0/extensions/samples.invoices/enable", null, Ct)).StatusCode);
        var ws = await admin.CreateWorkspaceAsync("Finance");
        var listResponse = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Invoices", templateKey = "samples.invoices.invoices" }, Ct);
        var list = (await listResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 120, status = "pendingApproval" } });
        await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-2", amount = 80 } });

        await using var mcp = await ConnectAsync(admin);
        Assert.Contains("samples_invoices_pending", (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name));
        var pending = await CallAsync(mcp, "samples_invoices_pending", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString() });
        Assert.Equal(["INV-1"], pending.GetProperty("invoices").EnumerateArray().Select(i => i.GetProperty("title").GetString()));

        await admin.PostAsync("/v1.0/extensions/samples.invoices/disable", null, Ct);
        Assert.DoesNotContain("samples_invoices_pending", (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name));
    }

    [Fact]
    public async Task Agents_can_page_describe_and_organize_folders()
    {
        await factory.CreateTenantAsync("mcp-pages");
        var admin = await ApiClient.CreateAsync(factory, "mcp-pages");
        var ws = await admin.CreateWorkspaceAsync("Work");
        var tasksResponse = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Tasks", templateKey = "tasks" }, Ct);
        var tasks = (await tasksResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        var contentType = await admin.CreateContentTypeAsync("Note", [new { name = "state", type = "text" }]);
        var list = await admin.CreateListAsync(ws, "Notes", contentType);

        await using var mcp = await ConnectAsync(admin);
        var home = await CallAsync(mcp, "get_home", []);
        Assert.NotEqual(Guid.Empty, home.GetProperty("documentsListId").GetGuid());

        var lists = await CallAsync(mcp, "list_lists", new() { ["workspaceId"] = ws.ToString() });
        var taskList = lists.GetProperty("lists").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == tasks);
        Assert.Equal("task", taskList.GetProperty("contentTypes")[0].GetProperty("key").GetString());

        var described = await CallAsync(mcp, "describe_list", new() { ["workspaceId"] = ws.ToString(), ["listId"] = tasks.ToString() });
        var status = described.GetProperty("contentTypes")[0].GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("name").GetString() == "status");
        Assert.Contains("notStarted", status.GetProperty("choices").EnumerateArray().Select(choice => choice.GetString()));
        Assert.True(described.GetProperty("contentTypes")[0].GetProperty("fields").EnumerateArray().First().GetProperty("required").GetBoolean());

        await CallAsync(mcp, "create_item", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["fields"] = new Dictionary<string, object> { ["title"] = "one" } });
        await CallAsync(mcp, "create_item", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["fields"] = new Dictionary<string, object> { ["title"] = "two" } });
        await CallAsync(mcp, "create_item", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["fields"] = new Dictionary<string, object> { ["title"] = "three" } });
        var first = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["top"] = 2 });
        Assert.Equal(["one", "two"], Titles(first));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["top"] = 2, ["cursor"] = cursor });
        Assert.Equal(["three"], Titles(second));
        Assert.False(second.TryGetProperty("nextCursor", out _));
        var invalid = await mcp.CallToolAsync("query_items", new Dictionary<string, object?> { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["cursor"] = "nope" }, cancellationToken: Ct);
        Assert.True(invalid.IsError);

        var folder = await CallAsync(mcp, "create_folder", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["title"] = "Contracts" });
        var folderId = folder.GetProperty("id").GetGuid();
        var nested = await CallAsync(mcp, "create_item", new()
        {
            ["workspaceId"] = ws.ToString(),
            ["listId"] = list.ToString(),
            ["parentId"] = folderId.ToString(),
            ["fields"] = new Dictionary<string, object> { ["title"] = "signed" },
        });
        var children = await CallAsync(mcp, "list_children", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["folderId"] = folderId.ToString() });
        Assert.Equal(["signed"], Titles(children));
        var ensured = await CallAsync(mcp, "ensure_folder", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["path"] = "Contracts/2026" });
        Assert.NotEqual(folderId, ensured.GetProperty("folderId").GetGuid());

        var blocked = await mcp.CallToolAsync("delete_item", new Dictionary<string, object?> { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["itemId"] = folderId.ToString() }, cancellationToken: Ct);
        Assert.True(blocked.IsError);
        await CallAsync(mcp, "delete_item", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["itemId"] = ensured.GetProperty("folderId").ToString() });
        await CallAsync(mcp, "move_item", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["itemId"] = nested.GetProperty("id").ToString() });
        var root = await CallAsync(mcp, "list_children", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString() });
        Assert.Contains(Titles(root), title => title == "signed");
        await CallAsync(mcp, "delete_item", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["itemId"] = nested.GetProperty("id").ToString() });
        var after = await CallAsync(mcp, "query_items", new() { ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["filter"] = "fields/title eq 'signed'" });
        Assert.Empty(Titles(after));
    }

    [Fact]
    public async Task Agents_can_upload_and_read_library_files()
    {
        await factory.CreateTenantAsync("mcp-files");
        var admin = await ApiClient.CreateAsync(factory, "mcp-files");
        var ws = await admin.CreateWorkspaceAsync("Records");
        var libraryResponse = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        var library = (await libraryResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        var plain = await admin.CreateListAsync(ws, "Plain");
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% marker\n%%EOF\n");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

        await using var mcp = await ConnectAsync(admin);
        var rejected = await mcp.CallToolAsync("upload_document", new Dictionary<string, object?>
        {
            ["workspaceId"] = ws.ToString(),
            ["listId"] = plain.ToString(),
            ["fileName"] = "a.pdf",
            ["contentBase64"] = Convert.ToBase64String(pdf),
        }, cancellationToken: Ct);
        Assert.True(rejected.IsError);

        var uploaded = await CallAsync(mcp, "upload_document", new()
        {
            ["workspaceId"] = ws.ToString(),
            ["listId"] = library.ToString(),
            ["fileName"] = "Invoice 42.pdf",
            ["title"] = "Invoice 42",
            ["contentBase64"] = Convert.ToBase64String(pdf),
        });
        var itemId = uploaded.GetProperty("itemId").GetGuid();
        Assert.Equal("Invoice 42.pdf", uploaded.GetProperty("file").GetProperty("fileName").GetString());
        var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
        var file = await CallAsync(mcp, "get_file", new() { ["workspaceId"] = ws.ToString(), ["listId"] = library.ToString(), ["itemId"] = itemId.ToString() });
        Assert.Equal(sha, file.GetProperty("file").GetProperty("sha256").GetString());

        // The file is only a marker, not a readable PDF: the library's workflows find no text, and the tool says so.
        await DocumentWorkflowRuns.WaitAsync(admin, ws, itemId, 3);
        var text = await CallAsync(mcp, "read_document", new() { ["workspaceId"] = ws.ToString(), ["listId"] = library.ToString(), ["itemId"] = itemId.ToString() });
        Assert.Equal(0, text.GetProperty("pages").GetArrayLength());
        Assert.StartsWith("No text is stored", text.GetProperty("message").GetString(), StringComparison.Ordinal);

        var stale = await mcp.CallToolAsync("replace_document", new Dictionary<string, object?>
        {
            ["workspaceId"] = ws.ToString(),
            ["listId"] = library.ToString(),
            ["itemId"] = itemId.ToString(),
            ["fileName"] = "scan.png",
            ["contentBase64"] = Convert.ToBase64String(png),
            ["sha256"] = "0000",
        }, cancellationToken: Ct);
        Assert.True(stale.IsError);

        var replaced = await CallAsync(mcp, "replace_document", new()
        {
            ["workspaceId"] = ws.ToString(),
            ["listId"] = library.ToString(),
            ["itemId"] = itemId.ToString(),
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
        await factory.CreateTenantAsync("mcp-readonly");
        var admin = await ApiClient.CreateAsync(factory, "mcp-readonly");
        var created = await admin.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "assistant", scopes = new[] { "mcp.use", "list.read", "workspace.read", "document.read", "search.read" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var secret = (await created.ReadJsonAsync()).GetProperty("secret").GetString();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", "mcp-readonly");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        await using var mcp = await ConnectAsync(client);
        var tools = (await mcp.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).ToList();
        Assert.Contains("describe_list", tools);
        Assert.Contains("read_document", tools);
        Assert.DoesNotContain("create_item", tools);
        Assert.DoesNotContain("upload_document", tools);
        Assert.DoesNotContain("delete_item", tools);
    }

    private static IEnumerable<string?> Titles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("fields").GetProperty("title").GetString());
}
