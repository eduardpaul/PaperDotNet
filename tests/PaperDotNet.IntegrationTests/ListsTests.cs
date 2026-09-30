using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Content types, lists and items of a workspace, folders and queries (the lists engine).</summary>
public sealed class ListsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] InvoiceFields =
    [
        new { name = "amount", type = "currency", currencyCode = "EUR", required = true },
        new { name = "status", type = "choice", choices = new[] { "open", "paid" }, defaultValue = "open" },
        new { name = "tags", type = "choice", choices = new[] { "a", "b", "c" }, allowMultiple = true },
        new { name = "due", type = "date" },
    ];

    private readonly TestHost _host = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>An admin client of a new tenant (or the default one), a workspace and an invoice list.</summary>
    private async Task<(HttpClient Client, string Workspace, string List)> SetupAsync(string? tenant = null)
    {
        var client = tenant is null ? await _host.SignInAsync() : await _host.CreateTenantAsync(tenant);
        var workspace = await Api.CreateWorkspaceAsync(client, "Finance");
        var list = await Api.CreateListAsync(client, workspace, "Invoices", InvoiceFields);
        return (client, workspace, list.Id());
    }

    private static Task<HttpResponseMessage> PostItemAsync(HttpClient client, string workspace, string list, object body) =>
        client.PostAsJsonAsync(Api.Items(workspace, list), body, Ct);

    private static async Task<JsonElement> CreateItemAsync(HttpClient client, string workspace, string list, object body)
    {
        using var response = await PostItemAsync(client, workspace, list, body);
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    private static async Task<List<string>> TitlesAsync(HttpClient client, string workspace, string list, string query)
    {
        var body = await (await client.GetAsync($"{Api.Items(workspace, list)}?{query}", Ct)).JsonAsync(HttpStatusCode.OK);
        return [.. body.GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()!)];
    }

    private static async Task<string> ETagAsync(HttpClient client, string url) => (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;

    [Fact]
    public async Task Content_type_definitions_are_validated()
    {
        var client = await _host.SignInAsync();
        using var response = await client.PostAsJsonAsync("/v1.0/contentTypes", new
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

        var errors = (await response.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("errors").GetProperty("fields").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(errors, e => e.Contains("reserved", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Unknown field type", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("choices", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("must start with a lowercase letter", StringComparison.Ordinal));
        Assert.Contains((await (await client.GetAsync("/v1.0/fieldTypes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray(), t => t.GetProperty("name").GetString() == "person");
    }

    [Fact]
    public async Task Field_types_cannot_change_once_created()
    {
        var client = await _host.SignInAsync();
        var id = await Api.CreateContentTypeAsync(client, "Thing", new object[] { new { name = "size", type = "number" } });
        var etag = await ETagAsync(client, $"/v1.0/contentTypes/{id}");

        using var changed = await client.SendAsync(Api.WithETag(HttpMethod.Put, $"/v1.0/contentTypes/{id}", new { name = "Thing", fields = new object[] { new { name = "size", type = "text" } } }, etag), Ct);
        using var added = await client.SendAsync(Api.WithETag(HttpMethod.Put, $"/v1.0/contentTypes/{id}", new { name = "Thing", fields = new object[] { new { name = "size", type = "number" }, new { name = "color", type = "text" } } }, etag), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
    }

    [Fact]
    public async Task Lists_are_created_changed_with_etags_and_deleted()
    {
        var (client, ws, list) = await SetupAsync();
        var url = $"/v1.0/workspaces/{ws}/lists/{list}";
        var created = await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("list", created.GetProperty("kind").GetString());
        Assert.Contains(created.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "amount");

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(Api.Patch(url, new { name = "Bills" }, null), Ct)).StatusCode);
        using (var changed = await client.SendAsync(Api.Patch(url, new { name = "Bills", versioning = "major" }, created.ETag()), Ct))
        {
            var body = await changed.JsonAsync(HttpStatusCode.OK);
            Assert.Equal("Bills", body.GetProperty("name").GetString());
            Assert.Equal("major", body.GetProperty("versioning").GetString());
        }

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Api.Patch(url, new { name = "Old" }, created.ETag()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Bad", kind = "table" }, Ct)).StatusCode);
        var lists = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Bills", Assert.Single(lists.EnumerateArray()).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Api.WithETag(HttpMethod.Delete, url, null, await ETagAsync(client, url)), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url, Ct)).StatusCode);
        Assert.Empty((await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task Content_types_are_added_to_and_removed_from_lists()
    {
        var (client, ws, list) = await SetupAsync();
        var memo = await Api.CreateContentTypeAsync(client, "Memo", new object[] { new { name = "body", type = "note" } });
        var clash = await Api.CreateContentTypeAsync(client, "Clash", new object[] { new { name = "amount", type = "text" } });
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/contentTypes";

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new { contentTypeId = clash }, Ct)).StatusCode);
        var added = await (await client.PostAsJsonAsync(url, new { contentTypeId = memo }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(2, added.GetProperty("contentTypes").GetArrayLength());
        var item = await CreateItemAsync(client, ws, list, new { contentTypeId = memo, fields = new { title = "A memo", body = "Hello" } });
        Assert.Equal(memo, item.GetProperty("contentTypeId").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{url}/{memo}", Ct)).StatusCode);
        var invoice = added.GetProperty("contentTypes")[0].Id();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}/{invoice}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{url}/{memo}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Items_are_validated_and_defaults_applied()
    {
        var (client, ws, list) = await SetupAsync();

        var created = await CreateItemAsync(client, ws, list, new { fields = new { title = "INV-1", amount = 12.5, tags = new[] { "a", "a" } } });
        using var invalid = await PostItemAsync(client, ws, list, new { fields = new { title = "INV-2", amount = "x", status = "maybe", unknown = 1 } });
        using var missing = await PostItemAsync(client, ws, list, new { fields = new { title = "INV-3" } });

        var fields = created.GetProperty("fields");
        Assert.Equal("open", fields.GetProperty("status").GetString());
        Assert.Equal(1, fields.GetProperty("tags").GetArrayLength());
        var errors = (await invalid.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("fields.amount", out _));
        Assert.True(errors.TryGetProperty("fields.status", out _));
        Assert.True(errors.TryGetProperty("fields.unknown", out _));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Items_are_updated_with_merge_semantics_and_etags()
    {
        var (client, ws, list) = await SetupAsync();
        var item = await CreateItemAsync(client, ws, list, new { fields = new { title = "INV-1", amount = 10, due = "2026-10-01" } });
        var url = $"{Api.Items(ws, list)}/{item.Id()}";
        var etag = await ETagAsync(client, url);

        using var noEtag = await client.PatchAsJsonAsync(url, new { fields = new { amount = 20 } }, Ct);
        using var updated = await client.SendAsync(Api.Patch(url, new { fields = new { amount = 20, due = (string?)null } }, etag), Ct);
        using var stale = await client.SendAsync(Api.Patch(url, new { fields = new { amount = 30 } }, etag), Ct);

        Assert.Equal(HttpStatusCode.PreconditionRequired, noEtag.StatusCode);
        var fields = (await updated.JsonAsync(HttpStatusCode.OK)).GetProperty("fields");
        Assert.Equal(20, fields.GetProperty("amount").GetDecimal());
        Assert.Equal("INV-1", fields.GetProperty("title").GetString());
        Assert.False(fields.TryGetProperty("due", out _));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Items_created_with_their_id_can_be_created_again_safely()
    {
        var (client, ws, list) = await SetupAsync();
        var id = Guid.NewGuid();
        using var first = await PostItemAsync(client, ws, list, new { id, fields = new { title = "INV-9", amount = 5 } });
        using var again = await PostItemAsync(client, ws, list, new { id, fields = new { title = "Changed", amount = 6 } });

        Assert.Equal(id.ToString(), (await first.JsonAsync(HttpStatusCode.Created)).Id());
        Assert.Equal("INV-9", (await again.JsonAsync(HttpStatusCode.OK)).GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal(first.Headers.ETag, again.Headers.ETag);

        // The id of an item in another list, of a deleted item, or of another tenant's item is a conflict.
        var memos = (await Api.CreateListAsync(client, ws, "Memos")).Id();
        Assert.Equal(HttpStatusCode.Conflict, (await PostItemAsync(client, ws, memos, new { id, fields = new { title = "Memo" } })).StatusCode);
        var deleted = (await CreateItemAsync(client, ws, list, new { fields = new { title = "Gone", amount = 1 } })).Id();
        var url = $"{Api.Items(ws, list)}/{deleted}";
        (await client.SendAsync(Api.WithETag(HttpMethod.Delete, url, null, await ETagAsync(client, url)), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await PostItemAsync(client, ws, list, new { id = deleted, fields = new { title = "Back", amount = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostItemAsync(client, ws, list, new { id = Guid.Empty, fields = new { title = "Empty", amount = 1 } })).StatusCode);

        var (clientB, wsB, listB) = await SetupAsync("items-create-id-b");
        using var taken = await PostItemAsync(clientB, wsB, listB, new { id, fields = new { title = "Mine", amount = 1 } });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.DoesNotContain("INV-9", await taken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_can_be_queried_with_odata_options()
    {
        var (client, ws, list) = await SetupAsync();
        await CreateItemAsync(client, ws, list, new { fields = new { title = "INV-1", amount = 100, tags = new[] { "a" }, due = "2026-10-01" } });
        await CreateItemAsync(client, ws, list, new { fields = new { title = "INV-2", amount = 250, status = "paid", tags = new[] { "a", "b" }, due = "2026-09-01" } });
        await CreateItemAsync(client, ws, list, new { fields = new { title = "INV-3", amount = 75, tags = new[] { "c" }, due = "2026-11-15" } });

        Assert.Equal(["INV-1", "INV-2"], await TitlesAsync(client, ws, list, "$filter=fields/amount gt 90"));
        Assert.Equal(["INV-1", "INV-3"], await TitlesAsync(client, ws, list, "$filter=fields/status eq 'open'&$orderby=fields/amount desc"));
        Assert.Equal(["INV-2"], await TitlesAsync(client, ws, list, "$filter=fields/tags/any(t: t eq 'b')"));
        Assert.Equal(["INV-1", "INV-2", "INV-3"], await TitlesAsync(client, ws, list, "$filter=fields/tags/any()"));
        Assert.Equal(["INV-1", "INV-2"], await TitlesAsync(client, ws, list, "$filter=fields/due lt 2026-10-15 and startswith(fields/title,'INV')"));
        Assert.Equal(["INV-2", "INV-3"], await TitlesAsync(client, ws, list, "$filter=fields/status in ('paid','x') or fields/amount le 75"));
        Assert.Equal(["INV-3", "INV-1", "INV-2"], await TitlesAsync(client, ws, list, "$orderby=fields/due desc"));
        Assert.Equal(["INV-2"], await TitlesAsync(client, ws, list, "$filter=tolower(fields/status) eq 'paid'"));
        Assert.Equal(["INV-1", "INV-2", "INV-3"], await TitlesAsync(client, ws, list, "$filter=createdBy eq @me and fields/due lt @today add duration'P3650D' or isFolder eq false"));

        var selected = await (await client.GetAsync($"{Api.Items(ws, list)}?$select=title&$top=1", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(["title"], selected.GetProperty("value")[0].GetProperty("fields").EnumerateObject().Select(p => p.Name));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Api.Items(ws, list)}?$filter=fields/nope eq 1", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$orderby=fields/amount")]
    [InlineData("$orderby=isFolder desc,fields/title")]
    public async Task Item_queries_page_with_next_links(string order)
    {
        var (client, ws, list) = await SetupAsync();
        for (var i = 1; i <= 5; i++)
        {
            await CreateItemAsync(client, ws, list, new { fields = new { title = $"INV-{i}", amount = i } });
        }

        var titles = new List<string>();
        string? url = $"{Api.Items(ws, list)}?$top=2&$count=true&{order}";
        var pages = 0;
        while (url is not null)
        {
            var body = await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);

            // The total comes with the first page only (issue 0001).
            Assert.Equal(pages == 0 ? 5 : null, body.TryGetProperty("@odata.count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : (int?)null);
            titles.AddRange(body.GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()!));
            url = body.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String ? new Uri(next.GetString()!).PathAndQuery : null;
            pages++;
        }

        Assert.Equal(3, pages);
        Assert.Equal(["INV-1", "INV-2", "INV-3", "INV-4", "INV-5"], titles);
    }

    [Fact]
    public async Task Folders_hold_items_and_protect_their_structure()
    {
        var (client, ws, list) = await SetupAsync();
        var folder = (await CreateItemAsync(client, ws, list, new { isFolder = true, fields = new { title = "2026" } })).Id();
        var sub = (await CreateItemAsync(client, ws, list, new { isFolder = true, parentId = folder, fields = new { title = "Q1" } })).Id();
        var invoice = (await CreateItemAsync(client, ws, list, new { parentId = folder, fields = new { title = "INV-1", amount = 1 } })).Id();
        using var folderFields = await PostItemAsync(client, ws, list, new { isFolder = true, fields = new { title = "X", amount = 1 } });

        var children = (await (await client.GetAsync($"{Api.Items(ws, list)}/{folder}/children", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()).ToList();
        Assert.Equal(["Q1", "INV-1"], children);
        Assert.Equal(HttpStatusCode.Created, folderFields.StatusCode); // Folders hold field values too (LST-19).

        var folderUrl = $"{Api.Items(ws, list)}/{folder}";
        var etag = await ETagAsync(client, folderUrl);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Api.Patch(folderUrl, new { parentId = sub }, etag), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Api.WithETag(HttpMethod.Delete, folderUrl, null, etag), Ct)).StatusCode);

        // Moving the invoice to the list root.
        var invoiceUrl = $"{Api.Items(ws, list)}/{invoice}";
        using var moved = await client.SendAsync(Api.Patch(invoiceUrl, new { parentId = (Guid?)null }, await ETagAsync(client, invoiceUrl)), Ct);
        Assert.Equal(JsonValueKind.Null, (await moved.JsonAsync(HttpStatusCode.OK)).GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task Lookup_and_person_fields_reference_existing_targets()
    {
        var (client, ws, customers) = await SetupAsync();
        var customer = (await CreateItemAsync(client, ws, customers, new { fields = new { title = "Acme", amount = 1 } })).Id();
        var me = (await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id();
        var contracts = (await Api.CreateListAsync(client, ws, "Contracts", new object[]
        {
            new { name = "customer", type = "lookup", lookupListId = customers },
            new { name = "owner", type = "person" },
        })).Id();

        using var valid = await PostItemAsync(client, ws, contracts, new { fields = new { title = "C-1", customer, owner = me } });
        using var invalid = await PostItemAsync(client, ws, contracts, new { fields = new { title = "C-2", customer = Guid.NewGuid(), owner = Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(["C-1"], await TitlesAsync(client, ws, contracts, $"$filter=fields/customer eq {customer}"));
        Assert.Equal(["C-1"], await TitlesAsync(client, ws, contracts, "$filter=fields/owner eq @me"));
    }

    [Fact]
    public async Task Workspace_roles_control_list_access()
    {
        var (admin, ws, list) = await SetupAsync();
        async Task<string> UserAsync(string name)
        {
            using var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            return (await created.JsonAsync(HttpStatusCode.Created)).Id();
        }

        var contributorId = await UserAsync("contributor");
        var visitorId = await UserAsync("visitor");
        await UserAsync("outsider");
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = contributorId, role = "member" }, Ct);
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = visitorId, role = "visitor" }, Ct);
        var contributor = await _host.SignInAsync("contributor", "contributor-password-1");
        var visitor = await _host.SignInAsync("visitor", "visitor-password-1");
        var outsider = await _host.SignInAsync("outsider", "outsider-password-1");
        await CreateItemAsync(admin, ws, list, new { fields = new { title = "By owner", amount = 1 } });

        Assert.Equal(HttpStatusCode.Created, (await PostItemAsync(contributor, ws, list, new { fields = new { title = "By member", amount = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Nope" }, Ct)).StatusCode);
        Assert.Equal(["By owner", "By member"], await TitlesAsync(visitor, ws, list, ""));
        Assert.Single((await (await visitor.GetAsync($"/v1.0/workspaces/{ws}/lists", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostItemAsync(visitor, ws, list, new { fields = new { title = "By visitor", amount = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{Api.Items(ws, list)}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostItemAsync(outsider, ws, list, new { fields = new { title = "x", amount = 1 } })).StatusCode);

        // Leaving the workspace ends access at once.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/workspaces/{ws}/members/{visitorId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"{Api.Items(ws, list)}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Lists_and_content_types_are_isolated_per_tenant()
    {
        var (clientA, wsA, listA) = await SetupAsync();
        var clientB = await _host.CreateTenantAsync("lists-iso-b");
        var item = await CreateItemAsync(clientA, wsA, listA, new { fields = new { title = "Secret", amount = 1 } });
        var contentType = (await (await clientA.GetAsync("/v1.0/contentTypes", Ct)).JsonAsync(HttpStatusCode.OK))[0].Id();

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/workspaces/{wsA}/lists/{listA}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync(Api.Items(wsA, listA), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Api.Items(wsA, listA)}/{item.Id()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/contentTypes/{contentType}", Ct)).StatusCode);
        Assert.Empty((await (await clientB.GetAsync("/v1.0/contentTypes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());

        // A workspace of the other tenant with the same id does not give access either.
        var wsB = await Api.CreateWorkspaceAsync(clientB, "Mine");
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/workspaces/{wsB}/lists/{listA}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostItemAsync(clientB, wsB, listA, new { fields = new { title = "Injected" } })).StatusCode);
    }
}
