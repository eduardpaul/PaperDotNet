using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Saved views of a list (LST-09).</summary>
public sealed class ViewTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private string _workspace = "";
    private string _list = "";

    private string Views => $"/v1.0/workspaces/{_workspace}/lists/{_list}/views";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Finance");
        _list = (await Api.CreateListAsync(_admin, _workspace, "Invoices", new object[]
        {
            new { name = "amount", type = "number" },
            new { name = "status", type = "choice", choices = new[] { "open", "paid" }, defaultValue = "open" },
        })).Id();
        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-1", amount = 100 });
        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-2", amount = 5, status = "paid" });
        await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "INV-3", amount = 50 });
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<List<JsonElement>> ItemsAsync(HttpClient client, string query) =>
        [.. (await (await client.GetAsync($"{Api.Items(_workspace, _list)}?{query}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()];

    [Fact]
    public async Task Views_are_validated_and_applied()
    {
        using var invalid = await _admin.PostAsJsonAsync(Views, new { name = "Broken", filter = "fields/nope eq 1", columns = new[] { "ghost" }, layout = "pie" }, Ct);
        var errors = (await invalid.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("columns", out _));
        Assert.True(errors.TryGetProperty("query", out _));
        Assert.True(errors.TryGetProperty("layout", out _));

        using var created = await _admin.PostAsJsonAsync(Views,
            new { name = "Open", filter = "fields/status eq 'open'", orderBy = "fields/amount", columns = new[] { "title", "amount" }, layout = "board", groupBy = "status" }, Ct);
        var view = await created.JsonAsync(HttpStatusCode.Created);
        Assert.Equal("board", view.GetProperty("layout").GetString());

        // The view's filter, order and columns; the request can narrow the filter and choose its own order.
        var items = await ItemsAsync(_admin, $"viewId={view.Id()}");
        Assert.Equal(["INV-3", "INV-1"], items.Select(i => i.GetProperty("fields").GetProperty("title").GetString()));
        Assert.All(items, i => Assert.False(i.GetProperty("fields").TryGetProperty("status", out _)));
        var narrowed = await ItemsAsync(_admin, $"viewId={view.Id()}&$filter=fields/amount gt 60&$orderby=fields/amount desc");
        Assert.Equal(["INV-1"], narrowed.Select(i => i.GetProperty("fields").GetProperty("title").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{Api.Items(_workspace, _list)}?viewId={Guid.NewGuid()}", Ct)).StatusCode);

        // One default view per list.
        using var paid = await _admin.PostAsJsonAsync(Views, new { name = "Paid", filter = "fields/status eq 'paid'", isDefault = true }, Ct);
        var paidId = (await paid.JsonAsync(HttpStatusCode.Created)).Id();
        using var replaced = await _admin.PutAsJsonAsync($"{Views}/{view.Id()}", new { name = "Open (default)", filter = "fields/status eq 'open'", isDefault = true }, Ct);
        Assert.Equal("table", (await replaced.JsonAsync(HttpStatusCode.OK)).GetProperty("layout").GetString());
        var all = (await (await _admin.GetAsync(Views, Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal(["Open (default)"], all.Where(v => v.GetProperty("isDefault").GetBoolean()).Select(v => v.GetProperty("name").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Views}/{paidId}", Ct)).StatusCode);
        Assert.Single((await (await _admin.GetAsync(Views, Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task Members_use_views_and_managers_change_them()
    {
        using var created = await _admin.PostAsJsonAsync(Views, new { name = "Paid", filter = "fields/status eq 'paid'" }, Ct);
        var viewId = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        using (var user = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct))
        {
            var userId = (await user.JsonAsync(HttpStatusCode.Created)).Id();
            Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId, role = "member" }, Ct)).StatusCode);
        }

        var member = await _host.SignInAsync("member", "member-password-1");
        Assert.Single((await (await member.GetAsync(Views, Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
        Assert.Equal(["INV-2"], (await ItemsAsync(member, $"viewId={viewId}")).Select(i => i.GetProperty("fields").GetProperty("title").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(Views, new { name = "Mine" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"{Views}/{viewId}", Ct)).StatusCode);

        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Views, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(Views, new { name = "Injected" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Views}/{viewId}", new { name = "Taken" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Views}/{viewId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Api.Items(_workspace, _list)}?viewId={viewId}", Ct)).StatusCode);
    }
}
