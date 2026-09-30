using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class ItemTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private HttpClient _client = null!;
    private string _listId = "";

    public async ValueTask InitializeAsync()
    {
        _client = await _host.SignInAsync();
        _listId = (await Api.CreateListAsync(_client, "Invoices", new object[]
        {
            new { name = "amount", type = "number" },
            new { name = "paid", type = "boolean" },
            new { name = "due", type = "dateTime" },
            new { name = "status", type = "choice", choices = new[] { "open", "closed" }, required = true },
            new { name = "notes", type = "note" },
        })).Id();
        for (var n = 1; n <= 5; n++)
        {
            await Api.CreateItemAsync(_client, _listId, new Dictionary<string, object>
            {
                ["title"] = $"Invoice {n}",
                ["amount"] = n * 10,
                ["paid"] = n > 3,
                ["due"] = $"2026-10-0{n}T12:00:00+02:00",
                ["status"] = n == 5 ? "closed" : "open",
            });
        }
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<(List<string> Titles, JsonElement Page)> QueryAsync(string query)
    {
        var page = await (await _client.GetAsync($"/v1.0/lists/{_listId}/items?{query}")).JsonAsync(HttpStatusCode.OK);
        return ([.. page.GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()!)], page);
    }

    [Theory]
    [InlineData("fields/amount gt 25", "Invoice 3,Invoice 4,Invoice 5")]
    [InlineData("fields/amount ge 20 and fields/paid eq false", "Invoice 2,Invoice 3")]
    [InlineData("fields/paid or fields/amount eq 10", "Invoice 1,Invoice 4,Invoice 5")]
    [InlineData("not (fields/status eq 'open')", "Invoice 5")]
    [InlineData("fields/status in ('closed')", "Invoice 5")]
    [InlineData("fields/due lt 2026-10-03T00:00:00Z", "Invoice 1,Invoice 2")]
    [InlineData("contains(fields/title, 'ice 4')", "Invoice 4")]
    [InlineData("startswith(fields/title, 'Invoice')", "Invoice 1,Invoice 2,Invoice 3,Invoice 4,Invoice 5")]
    [InlineData("fields/notes eq null and fields/amount lt 20", "Invoice 1")]
    [InlineData("contains(fields/title, '%')", "")]
    public async Task Filters_use_the_OData_syntax(string filter, string expected)
    {
        var (titles, _) = await QueryAsync($"$filter={Uri.EscapeDataString(filter)}");
        Assert.Equal(expected, string.Join(',', titles));
    }

    [Fact]
    public async Task Ordering_counting_and_paging_follow_the_query()
    {
        var titles = new List<string>();
        var (first, page) = await QueryAsync("$orderby=fields/amount desc&$top=2&$count=true&$filter=fields/amount gt 10");
        Assert.Equal(4, page.GetProperty("@odata.count").GetInt64());
        titles.AddRange(first);
        while (page.TryGetProperty("@odata.nextLink", out var next))
        {
            page = await (await _client.GetAsync(next.GetString())).JsonAsync(HttpStatusCode.OK);
            titles.AddRange(page.GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()!));
        }

        Assert.Equal(["Invoice 5", "Invoice 4", "Invoice 3", "Invoice 2"], titles);
    }

    [Theory]
    [InlineData("fields/unknown eq 1")]
    [InlineData("fields/amount eq")]
    [InlineData("fields/amount add 1 eq 2")]
    public async Task Invalid_queries_are_bad_requests(string filter)
    {
        using var response = await _client.GetAsync($"/v1.0/lists/{_listId}/items?$filter={Uri.EscapeDataString(filter)}");
        Assert.Equal("invalidQuery", (await response.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Values_are_validated_and_normalized()
    {
        using (var invalid = await _client.PostAsJsonAsync($"/v1.0/lists/{_listId}/items", new { fields = new { amount = "ten", status = "unknown", other = 1 } }))
        {
            var errors = (await invalid.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("errors");
            Assert.True(errors.TryGetProperty("fields.amount", out _));
            Assert.True(errors.TryGetProperty("fields.status", out _));
            Assert.True(errors.TryGetProperty("fields.other", out _));
            Assert.True(errors.TryGetProperty("fields.title", out _));
        }

        var item = await Api.CreateItemAsync(_client, _listId, new { title = "  Spaced  ", status = "open", due = "2026-01-01T01:00:00+01:00" });
        Assert.Equal("Spaced", item.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", item.GetProperty("fields").GetProperty("due").GetString());
    }

    [Fact]
    public async Task Items_change_with_etags_and_null_removes_a_value()
    {
        var item = await Api.CreateItemAsync(_client, _listId, new { title = "Change me", status = "open", amount = 1 });
        var uri = $"/v1.0/lists/{_listId}/items/{item.Id()}";

        using (var changed = await _client.SendAsync(Api.Patch(uri, new { fields = new Dictionary<string, object?> { ["amount"] = null, ["paid"] = true } }, item.ETag())))
        {
            var fields = (await changed.JsonAsync(HttpStatusCode.OK)).GetProperty("fields");
            Assert.False(fields.TryGetProperty("amount", out _));
            Assert.True(fields.GetProperty("paid").GetBoolean());
        }

        using (var required = await _client.SendAsync(Api.Patch(uri, new { fields = new Dictionary<string, object?> { ["status"] = null } }, "\"2\"")))
        {
            await required.JsonAsync(HttpStatusCode.BadRequest);
        }

        using (var stale = await _client.SendAsync(Api.Patch(uri, new { fields = new { paid = false } }, item.ETag())))
        {
            await stale.JsonAsync(HttpStatusCode.PreconditionFailed);
        }

        using (var deleted = await _client.DeleteAsync(uri))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var gone = await _client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Changes_reach_the_audit_log_through_the_outbox()
    {
        var item = await Api.CreateItemAsync(_client, _listId, new { title = "Audited", status = "open" });
        using (var changed = await _client.SendAsync(Api.Patch($"/v1.0/lists/{_listId}/items/{item.Id()}", new { fields = new { amount = 5 } }, item.ETag())))
        {
            await changed.JsonAsync(HttpStatusCode.OK);
        }

        var actions = new List<string>();
        for (var attempt = 0; attempt < 100 && actions.Count < 2; attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var page = await (await _client.GetAsync($"/v1.0/audit?targetId={item.Id()}")).JsonAsync(HttpStatusCode.OK);
            actions = [.. page.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("action").GetString()!)];
        }

        Assert.Equal(["item.updated", "item.created"], actions);
    }
}
