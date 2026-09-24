using System.Net;
using System.Net.Http.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class ListsTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] InvoiceFields =
    [
        new { name = "amount", type = "currency", currencyCode = "EUR", required = true },
        new { name = "status", type = "choice", choices = new[] { "open", "paid" }, defaultValue = "open" },
        new { name = "tags", type = "choice", choices = new[] { "a", "b", "c" }, allowMultiple = true },
        new { name = "due", type = "date" },
    ];

    /// <summary>A fresh tenant with an admin client, a workspace and an invoice list.</summary>
    private async Task<(HttpClient Client, Guid Workspace, Guid List, string Tenant)> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var workspace = await client.CreateWorkspaceAsync("Finance");
        var contentType = await client.CreateContentTypeAsync("Invoice", InvoiceFields);
        var list = await client.CreateListAsync(workspace, "Invoices", contentType);
        return (client, workspace, list, tenant);
    }

    [Fact]
    public async Task Content_type_definitions_are_validated()
    {
        await factory.CreateTenantAsync("ct-validation");
        var client = await ApiClient.CreateAsync(factory, "ct-validation");

        var response = await client.PostAsJsonAsync("/v1.0/contentTypes", new
        {
            name = "Broken",
            fields = new object[]
            {
                new { name = "title", type = "text" },
                new { name = "x", type = "nope" },
                new { name = "status", type = "choice" },
                new { name = "Bad Name", type = "text" },
            },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadJsonAsync()).GetProperty("errors").GetProperty("fields").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(errors, e => e!.Contains("reserved", StringComparison.Ordinal));
        Assert.Contains(errors, e => e!.Contains("Unknown field type", StringComparison.Ordinal));
        Assert.Contains(errors, e => e!.Contains("choices", StringComparison.Ordinal));
        Assert.Contains(errors, e => e!.Contains("must start with a lowercase letter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Field_types_cannot_change_once_created()
    {
        await factory.CreateTenantAsync("ct-change");
        var client = await ApiClient.CreateAsync(factory, "ct-change");
        var id = await client.CreateContentTypeAsync("Thing", [new { name = "size", type = "number" }]);
        var etag = (await client.GetAsync($"/v1.0/contentTypes/{id}", Ct)).Headers.ETag!.Tag;

        var changed = await client.SendWithEtagAsync(HttpMethod.Put, $"/v1.0/contentTypes/{id}", etag,
            new { name = "Thing", fields = new object[] { new { name = "size", type = "text" } } });
        var added = await client.SendWithEtagAsync(HttpMethod.Put, $"/v1.0/contentTypes/{id}", etag,
            new { name = "Thing", fields = new object[] { new { name = "size", type = "number" }, new { name = "color", type = "text" } } });

        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
    }

    [Fact]
    public async Task Items_are_validated_and_defaults_applied()
    {
        var (client, ws, list, _) = await SetupAsync("items-validation");

        var created = await client.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 12.5, tags = new[] { "a", "a" } } });
        var invalid = await client.PostItemAsync(ws, list, new { fields = new { title = "INV-2", amount = "x", status = "maybe", unknown = 1 } });
        var missing = await client.PostItemAsync(ws, list, new { fields = new { title = "INV-3" } });

        var fields = created.GetProperty("fields");
        Assert.Equal("open", fields.GetProperty("status").GetString());
        Assert.Equal(1, fields.GetProperty("tags").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.ReadJsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("fields.amount", out _));
        Assert.True(errors.TryGetProperty("fields.status", out _));
        Assert.True(errors.TryGetProperty("fields.unknown", out _));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Items_are_updated_with_merge_semantics_and_etags()
    {
        var (client, ws, list, _) = await SetupAsync("items-update");
        var item = await client.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 10, due = "2026-10-01" } });
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item.GetProperty("id").GetGuid()}";
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;

        var noEtag = await client.PatchAsJsonAsync(url, new { fields = new { amount = 20 } }, Ct);
        var updated = await client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { amount = 20, due = (string?)null } });
        var stale = await client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { amount = 30 } });

        Assert.Equal(HttpStatusCode.PreconditionRequired, noEtag.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var fields = (await updated.ReadJsonAsync()).GetProperty("fields");
        Assert.Equal(20, fields.GetProperty("amount").GetDecimal());
        Assert.Equal("INV-1", fields.GetProperty("title").GetString());
        Assert.False(fields.TryGetProperty("due", out _));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Items_can_be_queried_with_odata_options()
    {
        var (client, ws, list, _) = await SetupAsync("items-query");
        await client.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 100, tags = new[] { "a" }, due = "2026-10-01" } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "INV-2", amount = 250, status = "paid", tags = new[] { "a", "b" }, due = "2026-09-01" } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "INV-3", amount = 75, tags = new[] { "c" }, due = "2026-11-15" } });

        Assert.Equal(["INV-1", "INV-2"], await client.QueryTitlesAsync(ws, list, "$filter=fields/amount gt 90"));
        Assert.Equal(["INV-1", "INV-3"], await client.QueryTitlesAsync(ws, list, "$filter=fields/status eq 'open'&$orderby=fields/amount desc"));
        Assert.Equal(["INV-2"], await client.QueryTitlesAsync(ws, list, "$filter=fields/tags/any(t: t eq 'b')"));
        Assert.Equal(["INV-1", "INV-2"], await client.QueryTitlesAsync(ws, list, "$filter=fields/due lt 2026-10-15 and startswith(fields/title,'INV')"));
        Assert.Equal(["INV-2", "INV-3"], await client.QueryTitlesAsync(ws, list, "$filter=fields/status in ('paid','x') or fields/amount le 75"));
        Assert.Equal(["INV-3", "INV-1", "INV-2"], await client.QueryTitlesAsync(ws, list, "$orderby=fields/due desc"));

        var invalid = await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items?$filter=fields/nope eq 1", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$orderby=fields/amount")]
    public async Task Item_queries_page_with_next_links(string order)
    {
        var (client, ws, list, _) = await SetupAsync($"items-paging-{(order.Length == 0 ? "keyset" : "offset")}");
        for (var i = 1; i <= 5; i++)
        {
            await client.CreateItemAsync(ws, list, new { fields = new { title = $"INV-{i}", amount = i } });
        }

        var titles = new List<string>();
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items?$top=2&$count=true&{order}";
        var pages = 0;
        while (url is not null)
        {
            var body = await (await client.GetAsync(url, Ct)).ReadJsonAsync();
            Assert.Equal(5, body.GetProperty("@odata.count").GetInt32());
            titles.AddRange(body.GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()!));
            url = body.TryGetProperty("@odata.nextLink", out var next) ? new Uri(next.GetString()!).PathAndQuery : null;
            pages++;
        }

        Assert.Equal(3, pages);
        Assert.Equal(["INV-1", "INV-2", "INV-3", "INV-4", "INV-5"], titles);
    }

    [Fact]
    public async Task Folders_hold_items_and_protect_their_structure()
    {
        var (client, ws, list, _) = await SetupAsync("folders");
        var folder = (await client.CreateItemAsync(ws, list, new { isFolder = true, fields = new { title = "2026" } })).GetProperty("id").GetGuid();
        var sub = (await client.CreateItemAsync(ws, list, new { isFolder = true, parentId = folder, fields = new { title = "Q1" } })).GetProperty("id").GetGuid();
        await client.CreateItemAsync(ws, list, new { parentId = folder, fields = new { title = "INV-1", amount = 1 } });
        var folderFields = await client.PostItemAsync(ws, list, new { isFolder = true, fields = new { title = "X", amount = 1 } });

        var children = (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{folder}/children", Ct)).ReadJsonAsync())
            .GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()).ToList();
        Assert.Equal(["Q1", "INV-1"], children);
        Assert.Equal(HttpStatusCode.BadRequest, folderFields.StatusCode);

        var folderUrl = $"/v1.0/workspaces/{ws}/lists/{list}/items/{folder}";
        var etag = (await client.GetAsync(folderUrl, Ct)).Headers.ETag!.Tag;
        var intoItself = await client.SendWithEtagAsync(HttpMethod.Patch, folderUrl, etag, new { parentId = sub });
        var deleteNonEmpty = await client.SendWithEtagAsync(HttpMethod.Delete, folderUrl, etag);

        Assert.Equal(HttpStatusCode.BadRequest, intoItself.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteNonEmpty.StatusCode);
    }

    [Fact]
    public async Task Lookup_and_person_fields_reference_existing_targets()
    {
        var (client, ws, customers, _) = await SetupAsync("relations");
        var customer = (await client.CreateItemAsync(ws, customers, new { fields = new { title = "Acme", amount = 1 } })).GetProperty("id").GetGuid();
        var me = (await (await client.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var contract = await client.CreateContentTypeAsync("Contract",
        [
            new { name = "customer", type = "lookup", lookupListId = customers },
            new { name = "owner", type = "person" },
        ]);
        var contracts = await client.CreateListAsync(ws, "Contracts", contract);

        var valid = await client.PostItemAsync(ws, contracts, new { fields = new { title = "C-1", customer, owner = me } });
        var invalid = await client.PostItemAsync(ws, contracts, new { fields = new { title = "C-2", customer = Guid.NewGuid(), owner = Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(["C-1"], await client.QueryTitlesAsync(ws, contracts, $"$filter=fields/customer eq {customer}"));
    }

    [Fact]
    public async Task Views_are_validated_and_applied()
    {
        var (client, ws, list, _) = await SetupAsync("views");
        await client.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 100 } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "INV-2", amount = 5, status = "paid" } });
        var views = $"/v1.0/workspaces/{ws}/lists/{list}/views";

        var invalid = await client.PostAsJsonAsync(views, new { name = "Broken", filter = "fields/nope eq 1", columns = new[] { "ghost" } }, Ct);
        var created = await client.PostAsJsonAsync(views, new { name = "Open", filter = "fields/status eq 'open'", columns = new[] { "title", "amount" }, layout = "board", groupBy = "status" }, Ct);
        var viewId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        var body = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items?viewId={viewId}", Ct)).ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = Assert.Single(body.GetProperty("value").EnumerateArray());
        Assert.Equal("INV-1", item.GetProperty("fields").GetProperty("title").GetString());
        Assert.False(item.GetProperty("fields").TryGetProperty("status", out _));
    }

    [Fact]
    public async Task Workspace_roles_control_list_access()
    {
        var (admin, ws, list, tenant) = await SetupAsync("list-access");
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "contributor", password = "contributor-pw-1" }, Ct);
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "outsider", password = "outsider-pw-12" }, Ct);
        var contributorId = (await (await admin.GetAsync("/v1.0/users", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .First(u => u.GetProperty("userName").GetString() == "contributor").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = contributorId, role = "member" }, Ct);
        var contributor = await ApiClient.CreateAsync(factory, tenant, "contributor", "contributor-pw-1");
        var outsider = await ApiClient.CreateAsync(factory, tenant, "outsider", "outsider-pw-12");

        Assert.Equal(HttpStatusCode.Created, (await contributor.PostItemAsync(ws, list, new { fields = new { title = "By member", amount = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Nope" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostItemAsync(ws, list, new { fields = new { title = "x", amount = 1 } })).StatusCode);
    }

    [Fact]
    public async Task Lists_and_content_types_are_isolated_per_tenant()
    {
        var (clientA, wsA, listA, _) = await SetupAsync("lists-iso-a");
        await factory.CreateTenantAsync("lists-iso-b");
        var clientB = await ApiClient.CreateAsync(factory, "lists-iso-b");
        await clientA.CreateItemAsync(wsA, listA, new { fields = new { title = "Secret", amount = 1 } });

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/workspaces/{wsA}/lists/{listA}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/workspaces/{wsA}/lists/{listA}/items", Ct)).StatusCode);
        var typesB = (await (await clientB.GetAsync("/v1.0/contentTypes", Ct)).ReadJsonAsync()).EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Item"], typesB);
    }
}
