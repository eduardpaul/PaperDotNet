using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace PaperDotNet.IntegrationTests;

/// <summary>Extension data (EXT-07): own tables in <c>ext_samples_invoices</c> and list items via <c>IListItemStore</c>.</summary>
public sealed class ExtensionStorageTests(PaperDotNetApiFactory factory)
{
    private const string Id = "samples.invoices";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Guid Workspace, Guid List)> SetUpAsync(HttpClient admin, string workspace)
    {
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/v1.0/extensions/{Id}/enable", null, Ct)).StatusCode);
        var ws = await admin.CreateWorkspaceAsync(workspace);
        var response = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Invoices", templateKey = $"{Id}.invoices" }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (ws, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid ws, Guid list, Guid item) =>
        client.PostAsJsonAsync($"/v1.0/ext/{Id}/workspaces/{ws}/lists/{list}/items/{item}/approve", new { comment = "ok" }, Ct);

    [Fact]
    public async Task Approving_updates_the_item_and_records_the_approval()
    {
        await factory.CreateTenantAsync("ext-data");
        var admin = await ApiClient.CreateAsync(factory, "ext-data");
        var (ws, list) = await SetUpAsync(admin, "Finance");
        var draft = await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 10, status = "draft" } });
        var large = await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-2", amount = 2500, status = "draft" } });
        var largeId = large.GetProperty("id").GetGuid();

        var notPending = await ApproveAsync(admin, ws, list, draft.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, notPending.StatusCode);

        var approved = await ApproveAsync(admin, ws, list, largeId);
        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        var record = await approved.ReadJsonAsync();
        Assert.Equal(2500m, record.GetProperty("amount").GetDecimal());
        Assert.Equal(JsonValueKindString, record.GetProperty("approvedBy").ValueKind);

        // The item went through the lists pipeline: new status, new version.
        var item = await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{largeId}", Ct)).ReadJsonAsync();
        Assert.Equal("approved", item.GetProperty("fields").GetProperty("status").GetString());
        var versions = await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{largeId}/versions", Ct)).ReadJsonAsync();
        Assert.True(versions.GetProperty("value").GetArrayLength() >= 2);

        Assert.Equal(HttpStatusCode.Conflict, (await ApproveAsync(admin, ws, list, largeId)).StatusCode);
        var approvals = await (await admin.GetAsync($"/v1.0/ext/{Id}/approvals", Ct)).ReadJsonAsync();
        Assert.Equal(largeId, Assert.Single(approvals.EnumerateArray()).GetProperty("itemId").GetGuid());

        // Extension tables are audited like module tables.
        var audit = await (await admin.GetAsync($"/v1.0/auditLog?entityType=invoices.ApprovalRecord", Ct)).ReadJsonAsync();
        Assert.Single(audit.GetProperty("value").EnumerateArray());
    }

    private const System.Text.Json.JsonValueKind JsonValueKindString = System.Text.Json.JsonValueKind.String;

    [Fact]
    public async Task Extension_data_is_isolated_per_tenant()
    {
        await factory.CreateTenantAsync("ext-data-a");
        await factory.CreateTenantAsync("ext-data-b");
        var a = await ApiClient.CreateAsync(factory, "ext-data-a");
        var b = await ApiClient.CreateAsync(factory, "ext-data-b");
        var (ws, list) = await SetUpAsync(a, "Finance");
        await SetUpAsync(b, "Finance");
        var item = (await a.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 5000 } })).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(a, ws, list, item)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(b, ws, list, item)).StatusCode);
        var approvalsB = await (await b.GetAsync($"/v1.0/ext/{Id}/approvals", Ct)).ReadJsonAsync();
        Assert.Equal(0, approvalsB.GetArrayLength());
    }

    [Fact]
    public async Task Approving_needs_the_extension_scope()
    {
        await factory.CreateTenantAsync("ext-data-scope");
        var admin = await ApiClient.CreateAsync(factory, "ext-data-scope");
        var (ws, list) = await SetUpAsync(admin, "Finance");
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, "ext-data-scope", "member", "member-password-1");
        var item = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 5000 } })).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(member, ws, list, item)).StatusCode);
    }

    [Fact]
    public async Task Jobs_read_every_invoice_list_as_the_system()
    {
        await factory.CreateTenantAsync("ext-data-job");
        var admin = await ApiClient.CreateAsync(factory, "ext-data-job");
        var (ws1, list1) = await SetUpAsync(admin, "North");
        var (ws2, list2) = await SetUpAsync(admin, "South");
        await admin.CreateItemAsync(ws1, list1, new { fields = new { title = "N-1", amount = 5000 } });
        await admin.CreateItemAsync(ws2, list2, new { fields = new { title = "S-1", amount = 7000 } });
        await admin.CreateItemAsync(ws2, list2, new { fields = new { title = "S-2", amount = 9000 } });
        await admin.CreateItemAsync(ws2, list2, new { fields = new { title = "S-3", amount = 5 } });

        await Eventually.WaitForAsync<bool>(async () =>
        {
            var stats = await (await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).ReadJsonAsync();
            return stats.GetProperty("pendingApprovals").GetInt32() == 3 ? true : null;
        });
    }

    [Fact]
    public async Task Postgresql_row_level_security_covers_extension_tables()
    {
        Assert.SkipUnless(PaperDotNetApiFactory.Provider == "postgresql", "Row-level security is PostgreSQL only.");
        await factory.CreateTenantAsync("ext-data-rls");
        var admin = await ApiClient.CreateAsync(factory, "ext-data-rls");
        var (ws, list) = await SetUpAsync(admin, "Finance");
        var item = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 5000 } })).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(admin, ws, list, item)).StatusCode);

        // Raw SQL as the app's role without a tenant: the policy hides every row.
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ext_samples_invoices.approvals", connection);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Ct))!);
    }
}
