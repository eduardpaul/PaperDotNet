using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>List templates (LST-16) and searchable fields (SRC-06).</summary>
public sealed class TemplateTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> CreateFromTemplateAsync(HttpClient client, Guid ws, string name, string templateKey)
    {
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }

    private static async Task<List<JsonElement>> ViewsAsync(HttpClient client, Guid ws, Guid list) =>
        (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/views", Ct)).ReadJsonAsync()).EnumerateArray().ToList();

    [Fact]
    public async Task Every_built_in_template_creates_a_working_list()
    {
        await factory.CreateTenantAsync("tpl-all");
        var client = await ApiClient.CreateAsync(factory, "tpl-all");
        var ws = await client.CreateWorkspaceAsync("Team");

        var templates = (await (await client.GetAsync("/v1.0/listTemplates", Ct)).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal(["calendar", "contacts", "documents", "notes", "tasks"], templates.Select(t => t.GetProperty("key").GetString()).Order(StringComparer.Ordinal));

        foreach (var template in templates)
        {
            var key = template.GetProperty("key").GetString()!;
            var list = await CreateFromTemplateAsync(client, ws, $"My {key}", key);
            var listId = list.GetProperty("id").GetGuid();
            Assert.Equal(key, list.GetProperty("templateKey").GetString());

            var views = await ViewsAsync(client, ws, listId);
            Assert.Equal(template.GetProperty("views").GetArrayLength(), views.Count);
            foreach (var view in views)
            {
                var query = await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{listId}/items?viewId={view.GetProperty("id").GetGuid()}", Ct);
                Assert.True(query.StatusCode == HttpStatusCode.OK, $"{key}/{view.GetProperty("name").GetString()}: {await query.Content.ReadAsStringAsync(Ct)}");
            }
        }

        var documents = templates.Single(t => t.GetProperty("key").GetString() == "documents");
        Assert.True(documents.GetProperty("isLibrary").GetBoolean());
    }

    [Fact]
    public async Task Task_lists_have_defaults_views_and_share_the_content_type()
    {
        await factory.CreateTenantAsync("tpl-tasks");
        var client = await ApiClient.CreateAsync(factory, "tpl-tasks");
        var ws = await client.CreateWorkspaceAsync("Team");
        var first = await CreateFromTemplateAsync(client, ws, "Sprint", "tasks");
        var second = await CreateFromTemplateAsync(client, ws, "Backlog", "tasks");
        var listId = first.GetProperty("id").GetGuid();

        Assert.Equal(
            first.GetProperty("contentTypes")[0].GetProperty("id").GetGuid(),
            second.GetProperty("contentTypes")[0].GetProperty("id").GetGuid());

        var open = await client.CreateItemAsync(ws, listId, new { fields = new { title = "Write docs", dueDate = "2026-10-01" } });
        await client.CreateItemAsync(ws, listId, new { fields = new { title = "Ship it", status = "completed" } });
        Assert.Equal("notStarted", open.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal("normal", open.GetProperty("fields").GetProperty("priority").GetString());

        var views = await ViewsAsync(client, ws, listId);
        var active = views.Single(v => v.GetProperty("name").GetString() == "Active tasks");
        Assert.True(active.GetProperty("isDefault").GetBoolean());
        Assert.Equal(["Write docs"], await client.QueryTitlesAsync(ws, listId, $"viewId={active.GetProperty("id").GetGuid()}"));
        var board = views.Single(v => v.GetProperty("name").GetString() == "Board");
        Assert.Equal("board", board.GetProperty("layout").GetString());
        Assert.Equal("status", board.GetProperty("groupBy").GetString());
    }

    [Fact]
    public async Task Customized_built_in_content_types_are_kept()
    {
        await factory.CreateTenantAsync("tpl-custom");
        var client = await ApiClient.CreateAsync(factory, "tpl-custom");
        var ws = await client.CreateWorkspaceAsync("Team");
        var contacts = await CreateFromTemplateAsync(client, ws, "Clients", "contacts");
        var contentTypeId = contacts.GetProperty("contentTypes")[0].GetProperty("id").GetGuid();

        var url = $"/v1.0/contentTypes/{contentTypeId}";
        var current = await (await client.GetAsync(url, Ct)).ReadJsonAsync();
        var fields = current.GetProperty("fields").EnumerateArray().Select(f => (object)f).Append(new { name = "vatId", type = "text" }).ToArray();
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await client.SendWithEtagAsync(HttpMethod.Put, url, etag, new { name = "Contact", fields })).StatusCode);

        var another = await CreateFromTemplateAsync(client, ws, "Suppliers", "contacts");
        var columns = another.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("vatId", columns);
    }

    [Fact]
    public async Task Templates_are_validated()
    {
        await factory.CreateTenantAsync("tpl-invalid");
        var client = await ApiClient.CreateAsync(factory, "tpl-invalid");
        var ws = await client.CreateWorkspaceAsync("Team");
        var item = await client.CreateContentTypeAsync("Thing", [new { name = "size", type = "number" }]);

        var unknown = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "X", templateKey = "nope" }, Ct);
        var both = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Y", templateKey = "tasks", contentTypeIds = new[] { item } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task Field_search_weights_decide_what_is_found_and_how_it_ranks()
    {
        await factory.CreateTenantAsync("tpl-search");
        var client = await ApiClient.CreateAsync(factory, "tpl-search");
        var ws = await client.CreateWorkspaceAsync("Team");
        var contentType = await client.CreateContentTypeAsync("Ticket",
        [
            new { name = "code", type = "text", search = "high" },
            new { name = "details", type = "note" },
            new { name = "secret", type = "note", search = "none" },
        ]);
        var list = await client.CreateListAsync(ws, "Tickets", contentType);
        await client.CreateItemAsync(ws, list, new { fields = new { title = "In details", details = "a zebra crossing" } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "In code", code = "ZEBRA-1" } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "In secret", secret = "zebra" } });

        List<string> titles = [];
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var result = await (await client.GetAsync("/v1.0/search?q=zebra", Ct)).ReadJsonAsync();
            titles = result.GetProperty("value").EnumerateArray().Select(h => h.GetProperty("title").GetString()!).ToList();
            return titles.Count == 2 ? true : null;
        });

        Assert.Equal(["In code", "In details"], titles);
    }
}
