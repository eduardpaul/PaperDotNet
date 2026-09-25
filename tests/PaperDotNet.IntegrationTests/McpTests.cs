using System.Net;
using System.Net.Http.Json;
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
            ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["fields"] = new Dictionary<string, object> { ["title"] = "Call the vet", ["state"] = "open" },
        });
        var id = created.GetProperty("id").GetGuid();
        await CallAsync(mcp, "update_item", new()
        {
            ["workspaceId"] = ws.ToString(), ["listId"] = list.ToString(), ["itemId"] = id.ToString(), ["fields"] = new Dictionary<string, object> { ["state"] = "done" },
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
}
