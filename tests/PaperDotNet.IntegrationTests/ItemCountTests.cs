using System.Net;

namespace PaperDotNet.IntegrationTests;

/// <summary>Counts per value of a field, for boards, group-by and facets (ADR-0035).</summary>
public sealed class ItemCountTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private string _workspace = "";
    private string _list = "";

    private string Counts => $"{Api.Items(_workspace, _list)}/counts";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Finance");
        _list = (await Api.CreateListAsync(_admin, _workspace, "Invoices", new object[]
        {
            new { name = "amount", type = "number" },
            new { name = "paid", type = "boolean" },
            new { name = "status", type = "choice", choices = new[] { "open", "paid", "late" } },
            new { name = "tags", type = "choice", choices = new[] { "a", "b", "c" }, allowMultiple = true },
        })).Id();
        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-1", amount = 10, paid = true, status = "paid", tags = new[] { "a", "b" } });
        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-2", amount = 10, paid = false, status = "open", tags = new[] { "a" } });
        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-3", amount = 2.5, paid = false, status = "open" });
        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-4" });
        using var folder = await _admin.PostAsJsonAsync(Api.Items(_workspace, _list), new { isFolder = true, fields = new { title = "Archive" } }, Ct);
        await folder.JsonAsync(HttpStatusCode.Created);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<Dictionary<string, long>> CountAsync(HttpClient client, string query)
    {
        var body = await (await client.GetAsync($"{Counts}?{query}", Ct)).JsonAsync(HttpStatusCode.OK);
        return body.GetProperty("value").EnumerateArray().ToDictionary(c => c.GetProperty("value").GetString() ?? "(none)", c => c.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Values_are_counted_per_field_and_filter()
    {
        Assert.Equal(new Dictionary<string, long> { ["open"] = 2, ["paid"] = 1, ["(none)"] = 1 }, await CountAsync(_admin, "field=status"));
        Assert.Equal(new Dictionary<string, long> { ["10"] = 2, ["2.5"] = 1, ["(none)"] = 1 }, await CountAsync(_admin, "field=amount"));
        Assert.Equal(new Dictionary<string, long> { ["false"] = 2, ["true"] = 1, ["(none)"] = 1 }, await CountAsync(_admin, "field=paid"));

        // Multi-value fields: once per value, plus the items without one.
        Assert.Equal(new Dictionary<string, long> { ["a"] = 2, ["b"] = 1, ["(none)"] = 2 }, await CountAsync(_admin, "field=tags"));

        Assert.Equal(new Dictionary<string, long> { ["open"] = 1, ["paid"] = 1 }, await CountAsync(_admin, "field=status&$filter=fields/amount eq 10"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync($"{Counts}?field=nope", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync($"{Counts}?field=status&$filter=fields/nope eq 1", Ct)).StatusCode);
    }

    [Fact]
    public async Task Counts_see_only_readable_items_of_the_tenant()
    {
        string folder;
        using (var created = await _admin.PostAsJsonAsync(Api.Items(_workspace, _list), new { isFolder = true, fields = new { title = "Private" } }, Ct))
        {
            folder = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        }

        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-5", status = "late" });
        using (var created = await _admin.PostAsJsonAsync(Api.Items(_workspace, _list), new { parentId = folder, fields = new { title = "INV-6", status = "late" } }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"{Api.Items(_workspace, _list)}/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        using (var user = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct))
        {
            var userId = (await user.JsonAsync(HttpStatusCode.Created)).Id();
            Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId, role = "member" }, Ct)).StatusCode);
        }

        var member = await _host.SignInAsync("member", "member-password-1");
        Assert.Equal(2, (await CountAsync(_admin, "field=status"))["late"]);
        Assert.Equal(1, (await CountAsync(member, "field=status"))["late"]);

        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Counts}?field=status", Ct)).StatusCode);
    }
}
