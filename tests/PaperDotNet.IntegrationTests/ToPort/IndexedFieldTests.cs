using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Indexed fields (ADR-0035 step 4): item columns, the value table, backfill, counts and limits.</summary>
public sealed class IndexedFieldTests(PaperDotNetApiFactory factory)
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
        new { name = "tags", type = "keywords", allowMultiple = true, indexed = true },
    ];

    private sealed record Setup(HttpClient Admin, TenantSummary Tenant, Guid Workspace, Guid List, Guid ContentType, Guid Admin1)
    {
        public string ItemsUrl => $"/v1.0/workspaces/{Workspace}/lists/{List}/items";
    }

    private async Task<Setup> SetupAsync(string tenant, object[] fields)
    {
        var summary = await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var ws = await admin.CreateWorkspaceAsync("Sales");
        var contentType = await admin.CreateContentTypeAsync("Deal", fields);
        var list = await admin.CreateListAsync(ws, "Deals", contentType);
        var me = (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        return new Setup(admin, summary, ws, list, contentType, me);
    }

    private static async Task<List<string>> TitlesAsync(Setup setup, string query) =>
        (await setup.Admin.QueryTitlesAsync(setup.Workspace, setup.List, query)).ToList();

    private AsyncServiceScope Scope(Setup setup) =>
        factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(setup.Tenant.Id, setup.Tenant.Identifier);

    /// <summary>The SQL a filter runs, to see which storage it uses.</summary>
    private async Task<string> SqlAsync(Setup setup, string filter)
    {
        await using var scope = Scope(setup);
        var schema = (await scope.ServiceProvider.GetRequiredService<ListSchemaLoader>().LoadAsSystemAsync(setup.Workspace, setup.List, Ct))!;
        var (query, error) = await scope.ServiceProvider.GetRequiredService<ItemQueryRunner>().MatchingAsync(schema, filter, null, Ct);
        Assert.Null(error);
        var sql = query!.ToQueryString();
        return sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
    }

    [Fact]
    public async Task Indexed_fields_are_filtered_sorted_and_counted_through_columns_and_the_value_table()
    {
        var setup = await SetupAsync("idx-query", DealFields);
        var owner = setup.Admin1;
        async Task<Guid> DealAsync(string title, string stage, decimal amount, string closeDate, string[] labels, bool owned) =>
            (await setup.Admin.CreateItemAsync(setup.Workspace, setup.List, new
            {
                fields = new { title, stage, amount, closeDate, labels, owners = owned ? new[] { owner } : [] },
            })).GetProperty("id").GetGuid();

        var alpha = await DealAsync("Alpha", "won", 500, "2026-03-01", ["hot", "big"], owned: true);
        await DealAsync("Beta", "lead", 50, "2026-05-10", ["hot"], owned: false);
        await DealAsync("Gamma", "lost", 1200, "2026-01-20", ["renewal"], owned: true);
        await DealAsync("Delta", "won", 90, "2026-07-02", [], owned: false);

        Assert.Equal(["Alpha", "Delta"], await TitlesAsync(setup, "$filter=fields/stage eq 'won'&$orderby=fields/title"));
        Assert.Equal(["Gamma", "Alpha", "Delta"], await TitlesAsync(setup, "$filter=fields/amount gt 60&$orderby=fields/amount desc"));
        Assert.Equal(["Gamma", "Alpha"], await TitlesAsync(setup, "$filter=fields/closeDate lt 2026-04-01&$orderby=fields/closeDate"));
        Assert.Equal(["Alpha", "Beta"], await TitlesAsync(setup, "$filter=fields/labels/any(l: l eq 'hot')&$orderby=fields/title"));
        Assert.Equal(["Alpha"], await TitlesAsync(setup, "$filter=fields/labels/any(l: l eq 'hot') and fields/labels/any(l: l eq 'big')"));
        Assert.Equal(["Delta", "Gamma"], await TitlesAsync(setup, "$filter=not fields/labels/any(l: l eq 'hot')&$orderby=fields/title"));
        Assert.Equal(["Alpha", "Gamma"], await TitlesAsync(setup, $"$filter=fields/owners/any(o: o eq {owner})&$orderby=fields/title"));

        // The filters use the columns and the value table, not the JSON.
        var sql = await SqlAsync(setup, "fields/stage eq 'won' and fields/amount gt 60 and fields/labels/any(l: l eq 'hot')");
        Assert.Contains("text1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("number1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("item_values", sql, StringComparison.OrdinalIgnoreCase);

        // Counts per value: an item counts for each of its values; items without one count as null.
        var counts = await (await setup.Admin.GetAsync($"{setup.ItemsUrl}/counts?field=labels", Ct)).ReadJsonAsync();
        Assert.Equal(
            ["big:1", "hot:2", "null:1", "renewal:1"],
            counts.GetProperty("value").EnumerateArray().Select(c => $"{(c.TryGetProperty("value", out var v) ? v.GetString() : null) ?? "null"}:{c.GetProperty("count").GetInt64()}").Order(StringComparer.Ordinal));
        var stages = await (await setup.Admin.GetAsync($"{setup.ItemsUrl}/counts?field=stage&$filter=fields/amount gt 60", Ct)).ReadJsonAsync();
        Assert.Equal(["lost:1", "won:2"], stages.GetProperty("value").EnumerateArray()
            .Select(c => $"{c.GetProperty("value").GetString()}:{c.GetProperty("count").GetInt64()}").Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Admin.GetAsync($"{setup.ItemsUrl}/counts?field=nope", Ct)).StatusCode);

        // Changes rewrite the values; purging an item removes them.
        var url = $"{setup.ItemsUrl}/{alpha}";
        var etag = (await setup.Admin.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { labels = new[] { "renewal" } } })).StatusCode);
        Assert.Equal(["Beta"], await TitlesAsync(setup, "$filter=fields/labels/any(l: l eq 'hot')"));
        etag = (await setup.Admin.GetAsync(url, Ct)).Headers.ETag!.Tag;
        await setup.Admin.SendWithEtagAsync(HttpMethod.Delete, url, etag);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Admin.DeleteAsync($"/v1.0/workspaces/{setup.Workspace}/lists/{setup.List}/recycleBin/{alpha}", Ct)).StatusCode);
        await using var scope = Scope(setup);
        Assert.False(await scope.ServiceProvider.GetRequiredService<ListsDbContext>().ItemValues.AnyAsync(v => v.ItemId == alpha, Ct));
    }

    [Fact]
    public async Task Fields_indexed_later_are_backfilled_and_queried_from_json_until_then()
    {
        var setup = await SetupAsync("idx-backfill", [new { name = "amount", type = "number" }, new { name = "tags", type = "choice", allowMultiple = true, choices = new[] { "a", "b" } }]);
        await setup.Admin.CreateItemAsync(setup.Workspace, setup.List, new { fields = new { title = "One", amount = 1, tags = new[] { "a" } } });
        await setup.Admin.CreateItemAsync(setup.Workspace, setup.List, new { fields = new { title = "Two", amount = 2, tags = new[] { "b" } } });

        var typeUrl = $"/v1.0/contentTypes/{setup.ContentType}";
        var etag = (await setup.Admin.GetAsync(typeUrl, Ct)).Headers.ETag!.Tag;
        var replaced = await setup.Admin.SendWithEtagAsync(HttpMethod.Put, typeUrl, etag, new
        {
            name = "Deal",
            fields = new object[]
            {
                new { name = "amount", type = "number", indexed = true },
                new { name = "tags", type = "choice", allowMultiple = true, choices = new[] { "a", "b" }, indexed = true },
            },
        });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        // Planned but not filled yet: queries still read the JSON and stay right.
        Assert.DoesNotContain("number1", await SqlAsync(setup, "fields/amount gt 1"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Two"], await TitlesAsync(setup, "$filter=fields/amount gt 1"));
        Assert.Equal(["One"], await TitlesAsync(setup, "$filter=fields/tags/any(t: t eq 'a')"));

        await using (var scope = Scope(setup))
        {
            await scope.ServiceProvider.GetRequiredService<IndexedFieldBackfillJob>().RunAsync(Ct);
            var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
            var list = await db.Lists.AsNoTracking().SingleAsync(l => l.Id == setup.List, Ct);
            Assert.False(list.IndexPending);
            Assert.All(list.IndexedFields, f => Assert.True(f.Ready));
            Assert.Equal(2, await db.ItemValues.CountAsync(v => v.ListId == setup.List, Ct));
        }

        Assert.Contains("number1", await SqlAsync(setup, "fields/amount gt 1"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Two"], await TitlesAsync(setup, "$filter=fields/amount gt 1"));
        Assert.Equal(["One"], await TitlesAsync(setup, "$filter=fields/tags/any(t: t eq 'a')"));
    }

    [Fact]
    public async Task Indexing_has_limits()
    {
        var setup = await SetupAsync("idx-limits", DealFields);

        // Long text cannot be indexed.
        var invalid = await setup.Admin.PostAsJsonAsync("/v1.0/contentTypes", new { name = "Memo", fields = new[] { new { name = "body", type = "note", indexed = true } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // An indexed multi-value field takes at most 100 values.
        var many = Enumerable.Range(0, 101).Select(n => $"tag {n}").ToArray();
        var tooMany = await setup.Admin.PostItemAsync(setup.Workspace, setup.List, new { fields = new { title = "Crowd", tags = many } });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        // Past the list's columns for a kind, further fields stay unindexed (and are still queried from the JSON).
        var fields = Enumerable.Range(1, 11).Select(n => (object)new { name = $"code{n}", type = "text", indexed = true }).ToArray();
        var wide = await setup.Admin.CreateContentTypeAsync("Wide", fields);
        var list = await setup.Admin.CreateListAsync(setup.Workspace, "Wide", wide);
        await using var scope = Scope(setup);
        var indexed = (await scope.ServiceProvider.GetRequiredService<ListsDbContext>().Lists.AsNoTracking().SingleAsync(l => l.Id == list, Ct)).IndexedFields;
        Assert.Equal(10, indexed.Count(f => f.Kind == IndexKind.Text));
        Assert.DoesNotContain(indexed, f => f.Field == "code11");
    }

    [Fact]
    public async Task Task_lists_index_their_well_known_fields_in_the_same_columns()
    {
        await factory.CreateTenantAsync("idx-tasks");
        var admin = await ApiClient.CreateAsync(factory, "idx-tasks");
        var ws = await admin.CreateWorkspaceAsync("Ops");
        var first = (await (await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Sprint", templateKey = "tasks" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var second = (await (await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Backlog", templateKey = "tasks" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var tenant = (await factory.Services.CreateAsyncScope().ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync("idx-tasks", Ct))!;
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var lists = await scope.ServiceProvider.GetRequiredService<ListsDbContext>().Lists.AsNoTracking().Where(l => l.Id == first || l.Id == second).ToListAsync(Ct);
        foreach (var list in lists)
        {
            var byName = list.IndexedFields.ToDictionary(f => f.Field);
            Assert.Equal("Text1", byName["status"].Column);
            Assert.Equal("Date1", byName["dueDate"].Column);
            Assert.Equal((short)1, byName["assignedTo"].ValueField);
            Assert.All(list.IndexedFields, f => Assert.True(f.Ready));
        }
    }
}
