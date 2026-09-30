using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Querying;

namespace PaperDotNet.IntegrationTests;

/// <summary>Indexed fields: item columns and the value table (ADR-0035).</summary>
public sealed class IndexedFieldTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] DealFields =
    [
        new { name = "stage", type = "choice", choices = new[] { "lead", "won", "lost" }, indexed = true },
        new { name = "amount", type = "number", indexed = true },
        new { name = "closeDate", type = "date", indexed = true },
        new { name = "labels", type = "choice", allowMultiple = true, choices = new[] { "hot", "big", "renewal" }, indexed = true },
        new { name = "owners", type = "person", allowMultiple = true, indexed = true },
        new { name = "notes", type = "note" },
        new { name = "codes", type = "choice", allowMultiple = true, choices = Enumerable.Range(0, 101).Select(n => $"code {n}").ToArray(), indexed = true },
    ];

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private string _workspace = "";
    private Guid _tenant;
    private string _me = "";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Sales");
        var me = await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK);
        _tenant = Guid.Parse(me.GetProperty("tenantId").GetString()!);
        _me = me.Id();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<List<string>> TitlesAsync(string list, string query) =>
        [.. (await (await _admin.GetAsync($"{Api.Items(_workspace, list)}?{query}", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()!)];

    private async Task<Dictionary<string, IndexedField>> IndexedAsync(string listId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
        var id = Guid.Parse(listId);
        var list = await db.Lists.AsNoTracking().SingleAsync(l => l.TenantId == _tenant && l.Id == id, Ct);
        return FieldIndex.Read(list).ToDictionary(f => f.Field);
    }

    /// <summary>Writes a column of an item directly (not the JSON): a query that follows it reads the column.</summary>
    private async Task WriteColumnAsync(string itemId, string column, object? value)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IItemQueries>().WriteIndexColumnsAsync(_tenant, Guid.Parse(itemId), new Dictionary<string, object?> { [column] = value }, Ct);
    }

    private async Task<int> RemoveValueRowsAsync(string itemId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
        var id = Guid.Parse(itemId);
        var rows = await db.ItemValues.Where(v => v.TenantId == _tenant && v.ItemId == id).ToListAsync(Ct);
        db.ItemValues.RemoveRange(rows);
        await db.SaveChangesAsync(Ct);
        return rows.Count;
    }

    private async Task RunBackfillAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetServices<RecurringJobRegistration>().Single(r => r.Name == "lists.indexed-fields");
        await registration.Create(scope.ServiceProvider).RunAsync(_tenant, Ct);
    }

    [Fact]
    public async Task Indexed_fields_are_filtered_and_sorted_through_columns_and_the_value_table()
    {
        var list = (await Api.CreateListAsync(_admin, _workspace, "Deals", DealFields)).Id();
        async Task<string> DealAsync(string title, string stage, decimal amount, string closeDate, string[] labels, bool owned) =>
            (await Api.CreateItemAsync(_admin, _workspace, list, new { title, stage, amount, closeDate, labels, owners = owned ? new[] { _me } : [] })).Id();

        var alpha = await DealAsync("Alpha", "won", 500, "2026-03-01", ["hot", "big"], owned: true);
        await DealAsync("Beta", "lead", 50, "2026-05-10", ["hot"], owned: false);
        var gamma = await DealAsync("Gamma", "lost", 1200, "2026-01-20", ["renewal"], owned: true);
        await DealAsync("Delta", "won", 90, "2026-07-02", [], owned: false);

        var indexed = await IndexedAsync(list);
        Assert.All(indexed.Values, f => Assert.True(f.Ready));
        Assert.DoesNotContain("notes", indexed.Keys);
        Assert.Equal(["closeDate:Date1", "amount:Number1", "stage:Text1"],
            indexed.Values.Where(f => f.Column is not null).Select(f => $"{f.Field}:{f.Column}").OrderBy(c => c[(c.IndexOf(':') + 1)..], StringComparer.Ordinal));

        Assert.Equal(["Alpha", "Delta"], await TitlesAsync(list, "$filter=fields/stage eq 'won'&$orderby=fields/title"));
        Assert.Equal(["Gamma", "Alpha", "Delta"], await TitlesAsync(list, "$filter=fields/amount gt 60&$orderby=fields/amount desc"));
        Assert.Equal(["Gamma", "Alpha"], await TitlesAsync(list, "$filter=fields/closeDate lt 2026-04-01&$orderby=fields/closeDate"));
        Assert.Equal(["Alpha", "Beta"], await TitlesAsync(list, "$filter=fields/labels/any(l: l eq 'hot')&$orderby=fields/title"));
        Assert.Equal(["Alpha"], await TitlesAsync(list, "$filter=fields/labels/any(l: l eq 'hot') and fields/labels/any(l: l eq 'big')"));
        Assert.Equal(["Delta", "Gamma"], await TitlesAsync(list, "$filter=not fields/labels/any(l: l eq 'hot')&$orderby=fields/title"));
        Assert.Equal(["Alpha", "Gamma"], await TitlesAsync(list, $"$filter=fields/owners/any(o: o eq {_me})&$orderby=fields/title"));
        Assert.Equal(["Alpha", "Beta", "Gamma"], await TitlesAsync(list, "$filter=fields/labels/any()&$orderby=fields/title"));

        // The queries read the column and the value table, not the JSON.
        await WriteColumnAsync(gamma, indexed["amount"].Column!, 5.0);
        Assert.Equal(["Alpha", "Delta"], await TitlesAsync(list, "$filter=fields/amount gt 60&$orderby=fields/title"));
        Assert.Equal(3, await RemoveValueRowsAsync(alpha));
        Assert.Equal(["Beta"], await TitlesAsync(list, "$filter=fields/labels/any(l: l eq 'hot')"));

        // Changes rewrite the columns and values; purging an item removes its values.
        var url = $"{Api.Items(_workspace, list)}/{gamma}";
        var etag = (await _admin.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await _admin.SendAsync(Api.Patch(url, new { fields = new { amount = 1300, labels = new[] { "hot" } } }, etag), Ct)).StatusCode);
        Assert.Equal(["Gamma", "Alpha", "Delta"], await TitlesAsync(list, "$filter=fields/amount gt 60&$orderby=fields/amount desc"));
        Assert.Equal(["Beta", "Gamma"], await TitlesAsync(list, "$filter=fields/labels/any(l: l eq 'hot')&$orderby=fields/title"));
        etag = (await _admin.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendAsync(Api.WithETag(HttpMethod.Delete, url, null, etag), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/workspaces/{_workspace}/lists/{list}/recycleBin/{gamma}", Ct)).StatusCode);
        Assert.Equal(0, await RemoveValueRowsAsync(gamma));
    }

    [Fact]
    public async Task Fields_indexed_later_are_backfilled_and_queried_from_json_until_then()
    {
        var contentType = await Api.CreateContentTypeAsync(_admin, "Deal", new object[]
        {
            new { name = "amount", type = "number" },
            new { name = "tags", type = "choice", allowMultiple = true, choices = new[] { "a", "b" } },
        });
        using var created = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/lists", new { name = "Deals", contentTypeIds = new[] { contentType } }, Ct);
        var list = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        await Api.CreateItemAsync(_admin, _workspace, list, new { title = "One", amount = 1, tags = new[] { "a" } });
        var two = (await Api.CreateItemAsync(_admin, _workspace, list, new { title = "Two", amount = 2, tags = new[] { "b" } })).Id();

        var typeUrl = $"/v1.0/contentTypes/{contentType}";
        var etag = (await _admin.GetAsync(typeUrl, Ct)).Headers.ETag!.Tag;
        using var replaced = await _admin.SendAsync(Api.WithETag(HttpMethod.Put, typeUrl, new
        {
            name = "Deal",
            fields = new object[]
            {
                new { name = "amount", type = "number", indexed = true },
                new { name = "tags", type = "choice", allowMultiple = true, choices = new[] { "a", "b" }, indexed = true },
            },
        }, etag), Ct);
        await replaced.JsonAsync(HttpStatusCode.OK);

        // Planned but not filled yet: queries still read the JSON and stay right.
        var planned = await IndexedAsync(list);
        Assert.All(planned.Values, f => Assert.False(f.Ready));
        await WriteColumnAsync(two, planned["amount"].Column!, 0.0);
        Assert.Equal(["Two"], await TitlesAsync(list, "$filter=fields/amount gt 1"));
        Assert.Equal(["One"], await TitlesAsync(list, "$filter=fields/tags/any(t: t eq 'a')"));

        await RunBackfillAsync();
        var ready = await IndexedAsync(list);
        Assert.All(ready.Values, f => Assert.True(f.Ready));
        Assert.Equal(["Two"], await TitlesAsync(list, "$filter=fields/amount gt 1"));
        Assert.Equal(["One"], await TitlesAsync(list, "$filter=fields/tags/any(t: t eq 'a')"));
        Assert.Equal("1", (await (await _admin.GetAsync($"{Api.Items(_workspace, list)}/{two}", Ct)).JsonAsync(HttpStatusCode.OK)).ETag().Trim('"'));

        // Filled: the column is used now.
        await WriteColumnAsync(two, ready["amount"].Column!, 0.0);
        Assert.Empty(await TitlesAsync(list, "$filter=fields/amount gt 1"));
    }

    [Fact]
    public async Task Indexing_has_limits()
    {
        var list = (await Api.CreateListAsync(_admin, _workspace, "Deals", DealFields)).Id();

        // Long text cannot be indexed.
        using var invalid = await _admin.PostAsJsonAsync("/v1.0/contentTypes", new { name = "Memo", fields = new[] { new { name = "body", type = "note", indexed = true } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // An indexed multi-value field takes at most 100 values.
        var many = Enumerable.Range(0, 101).Select(n => $"code {n}").ToArray();
        using var tooMany = await _admin.PostAsJsonAsync(Api.Items(_workspace, list), new { fields = new { title = "Crowd", codes = many } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        // Past the list's columns for a kind, further fields stay unindexed (and are still queried from the JSON).
        var fields = Enumerable.Range(1, 11).Select(n => (object)new { name = $"code{n}", type = "text", indexed = true }).ToArray();
        var wide = (await Api.CreateListAsync(_admin, _workspace, "Wide", fields)).Id();
        var indexed = await IndexedAsync(wide);
        Assert.Equal(10, indexed.Values.Count(f => f.Kind == IndexKinds.Text));
        Assert.DoesNotContain("code11", indexed.Keys);
        await Api.CreateItemAsync(_admin, _workspace, wide, new { title = "W", code1 = "x", code11 = "y" });
        Assert.Equal(["W"], await TitlesAsync(wide, "$filter=fields/code1 eq 'x' and fields/code11 eq 'y'"));
    }
}
