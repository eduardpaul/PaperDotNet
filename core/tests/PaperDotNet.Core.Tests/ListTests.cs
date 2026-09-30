using System.Net;

namespace PaperDotNet.Core.Tests;

public sealed class ListTests : IAsyncLifetime
{
    private readonly CoreHostFactory _host = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Lists_are_created_read_changed_with_etags_and_deleted()
    {
        var client = await _host.SignInAsync();
        var list = await Api.CreateListAsync(client, "Invoices", new[] { new { name = "amount", type = "number" } });
        Assert.Equal("\"1\"", list.ETag());

        using (var duplicate = await client.PostAsJsonAsync("/v1.0/lists", new { name = "Invoices" }))
        {
            await duplicate.JsonAsync(HttpStatusCode.Conflict);
        }

        using (var noIfMatch = await client.SendAsync(Api.Patch($"/v1.0/lists/{list.Id()}", new { name = "Bills" }, null)))
        {
            await noIfMatch.JsonAsync(HttpStatusCode.PreconditionRequired);
        }

        using (var changed = await client.SendAsync(Api.Patch($"/v1.0/lists/{list.Id()}", new { name = "Bills" }, list.ETag())))
        {
            Assert.Equal("\"2\"", (await changed.JsonAsync(HttpStatusCode.OK)).ETag());
        }

        using (var stale = await client.SendAsync(Api.Patch($"/v1.0/lists/{list.Id()}", new { name = "Old" }, list.ETag())))
        {
            await stale.JsonAsync(HttpStatusCode.PreconditionFailed);
        }

        using (var typeChange = await client.SendAsync(Api.Patch($"/v1.0/lists/{list.Id()}", new { fields = new[] { new { name = "amount", type = "text" } } }, "\"2\"")))
        {
            await typeChange.JsonAsync(HttpStatusCode.BadRequest);
        }

        var page = await (await client.GetAsync("/v1.0/lists")).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Bills", Assert.Single(page.GetProperty("value").EnumerateArray()).GetProperty("name").GetString());

        using (var deleted = await client.DeleteAsync($"/v1.0/lists/{list.Id()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var gone = await client.GetAsync($"/v1.0/lists/{list.Id()}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Invalid_field_definitions_are_rejected()
    {
        var client = await _host.SignInAsync();
        using var response = await client.PostAsJsonAsync("/v1.0/lists", new
        {
            name = "Broken",
            fields = new object[] { new { name = "title", type = "text" }, new { name = "x", type = "unknown" }, new { name = "c", type = "choice" } },
        });
        var problem = await response.JsonAsync(HttpStatusCode.BadRequest);
        Assert.Equal(3, problem.GetProperty("errors").EnumerateObject().Count());
    }

    [Fact]
    public async Task Lists_page_with_skiptoken()
    {
        var client = await _host.SignInAsync();
        for (var n = 0; n < 5; n++)
        {
            await Api.CreateListAsync(client, $"List {n}");
        }

        var names = new List<string>();
        var next = "/v1.0/lists?$top=2";
        while (next is not null)
        {
            var page = await (await client.GetAsync(next)).JsonAsync(HttpStatusCode.OK);
            names.AddRange(page.GetProperty("value").EnumerateArray().Select(l => l.GetProperty("name").GetString()!));
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        Assert.Equal(["List 0", "List 1", "List 2", "List 3", "List 4"], names);
    }
}
