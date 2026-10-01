using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>List templates (LST-16).</summary>
public sealed class TemplateTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _client = null!;
    private string _workspace = "";

    public async ValueTask InitializeAsync()
    {
        _client = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_client, "Team");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<JsonElement> CreateFromTemplateAsync(string name, string templateKey)
    {
        using var response = await _client.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/lists", new { name, templateKey }, Ct);
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    private async Task<List<JsonElement>> ViewsAsync(string list) =>
        [.. (await (await _client.GetAsync($"/v1.0/workspaces/{_workspace}/lists/{list}/views", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()];

    [Fact]
    public async Task Every_built_in_template_creates_a_working_list()
    {
        // Calendar adds its template when it is ported (T11b).
        var templates = (await (await _client.GetAsync("/v1.0/listTemplates", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal(["contacts", "documents", "notes", "tasks"], templates.Select(t => t.GetProperty("key").GetString()).Order(StringComparer.Ordinal));

        foreach (var template in templates)
        {
            var key = template.GetProperty("key").GetString()!;
            var list = await CreateFromTemplateAsync($"My {key}", key);
            Assert.Equal(key, list.GetProperty("templateKey").GetString());

            var views = await ViewsAsync(list.Id());
            Assert.Equal(template.GetProperty("views").GetArrayLength(), views.Count);
            foreach (var view in views)
            {
                using var query = await _client.GetAsync($"{Api.Items(_workspace, list.Id())}?viewId={view.Id()}", Ct);
                await query.JsonAsync(HttpStatusCode.OK);
            }
        }

        var documents = templates.Single(t => t.GetProperty("key").GetString() == "documents");
        Assert.True(documents.GetProperty("isLibrary").GetBoolean());
        var library = await CreateFromTemplateAsync("Files", "documents");
        Assert.Equal("library", library.GetProperty("kind").GetString());
        Assert.Equal("major", library.GetProperty("versioning").GetString());
    }

    [Fact]
    public async Task Lists_of_a_template_share_its_content_type_and_customizations_are_kept()
    {
        var contacts = await CreateFromTemplateAsync("Clients", "contacts");
        var contentTypeId = contacts.GetProperty("contentTypes")[0].Id();

        var url = $"/v1.0/contentTypes/{contentTypeId}";
        using var current = await _client.GetAsync(url, Ct);
        var body = await current.JsonAsync(HttpStatusCode.OK);
        var fields = body.GetProperty("fields").EnumerateArray().Select(f => (object)f).Append(new { name = "vatId", type = "text" }).ToArray();
        using var replaced = await _client.SendAsync(Api.WithETag(HttpMethod.Put, url, new { name = body.GetProperty("name").GetString(), fields }, current.Headers.ETag!.Tag), Ct);
        await replaced.JsonAsync(HttpStatusCode.OK);

        var another = await CreateFromTemplateAsync("Suppliers", "contacts");
        Assert.Equal(contentTypeId, another.GetProperty("contentTypes")[0].Id());
        Assert.Contains("vatId", another.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Templates_are_validated()
    {
        var thing = await Api.CreateContentTypeAsync(_client, "Thing", new[] { new { name = "size", type = "number" } });
        var lists = $"/v1.0/workspaces/{_workspace}/lists";
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(lists, new { name = "X", templateKey = "nope" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(lists, new { name = "Y", templateKey = "contacts", contentTypeIds = new[] { thing } }, Ct)).StatusCode);

        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(4, (await (await other.GetAsync("/v1.0/listTemplates", Ct)).JsonAsync(HttpStatusCode.OK)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(lists, new { name = "Z", templateKey = "contacts" }, Ct)).StatusCode);
    }
}
