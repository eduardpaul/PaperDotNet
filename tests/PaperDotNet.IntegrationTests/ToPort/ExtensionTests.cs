using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Extension runtime (EXT-01…04, IAM-13, EVT-03) with the sample extension <c>samples.invoices</c>.</summary>
public sealed class ExtensionTests(PaperDotNetApiFactory factory)
{
    private const string Id = "samples.invoices";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] InvoiceFields =
    [
        new { name = "amount", type = "number" },
        new { name = "status", type = "choice", choices = new[] { "draft", "pendingApproval", "approved" } },
        new { name = "iban", type = "samples.invoices.iban" },
    ];

    private static Task<HttpResponseMessage> EnableAsync(HttpClient admin, bool enabled = true) =>
        admin.PostAsync($"/v1.0/extensions/{Id}/{(enabled ? "enable" : "disable")}", null, Ct);

    /// <summary>A list from the extension's template (content type provisioned on enable).</summary>
    private static async Task<Guid> CreateInvoiceListAsync(HttpClient admin, Guid workspace)
    {
        var response = await admin.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists", new { name = "Invoices", templateKey = $"{Id}.invoices" }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Installed_extensions_are_listed_and_disabled_by_default()
    {
        await factory.CreateTenantAsync("ext-catalog");
        var admin = await ApiClient.CreateAsync(factory, "ext-catalog");

        var list = await (await admin.GetAsync("/v1.0/extensions", Ct)).ReadJsonAsync();
        var sample = Assert.Single(list.EnumerateArray(), e => e.GetProperty("id").GetString() == Id);

        Assert.False(sample.GetProperty("enabled").GetBoolean());
        Assert.Equal("1.0.0", sample.GetProperty("version").GetString());
        var contributions = sample.GetProperty("contributions");
        Assert.Contains("samples.invoices.iban", contributions.GetProperty("fieldTypes").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("samples.invoices.reminders", contributions.GetProperty("jobs").EnumerateArray().Select(t => t.GetString()));
        Assert.True(contributions.GetProperty("endpoints").GetBoolean());

        // Extension scopes are part of the catalog (IAM-13).
        var scopes = (await (await admin.GetAsync("/v1.0/scopes", Ct)).ReadJsonAsync()).EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("samples.invoices.approve", scopes);
    }

    [Fact]
    public async Task Contributions_are_gated_by_tenant_enablement()
    {
        await factory.CreateTenantAsync("ext-gating");
        var admin = await ApiClient.CreateAsync(factory, "ext-gating");

        // Disabled: no endpoints, no field type.
        var stats = await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct);
        Assert.Equal(HttpStatusCode.NotFound, stats.StatusCode);
        Assert.Equal("extensionDisabled", (await stats.ReadJsonAsync()).GetProperty("code").GetString());
        var blocked = await admin.PostAsJsonAsync("/v1.0/contentTypes", new { name = "Payment", fields = InvoiceFields }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var templates = await (await admin.GetAsync("/v1.0/listTemplates", Ct)).ReadJsonAsync();
        Assert.DoesNotContain(templates.EnumerateArray(), t => t.GetProperty("key").GetString() == $"{Id}.invoices");

        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);

        var ws = await admin.CreateWorkspaceAsync("Finance");
        var list = await CreateInvoiceListAsync(admin, ws);

        // The extension's field type validates; its receiver applies the approval rule (threshold 1000).
        var badIban = await admin.PostItemAsync(ws, list, new { fields = new { title = "INV-0", amount = 10, iban = "DE00 1234" } });
        Assert.Equal(HttpStatusCode.BadRequest, badIban.StatusCode);
        var small = await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 500, status = "draft", iban = "de89 3704 0044 0532 0130 00" } });
        var large = await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-2", amount = 5000, status = "draft" } });
        Assert.Equal("draft", small.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal("DE89370400440532013000", small.GetProperty("fields").GetProperty("iban").GetString());
        Assert.Equal("pendingApproval", large.GetProperty("fields").GetProperty("status").GetString());

        // Subscriber and recurring job run for this tenant.
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var counters = await (await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).ReadJsonAsync();
            return counters.GetProperty("itemsAdded").GetInt32() >= 2 && counters.GetProperty("reminderRuns").GetInt32() >= 1 ? true : null;
        });

        // Disabled again: the receiver stops, the endpoint disappears, existing data stays usable.
        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(admin, enabled: false)).StatusCode);
        var afterDisable = await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-3", amount = 9000, status = "draft" } });
        Assert.Equal("draft", afterDisable.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
    }

    [Fact]
    public async Task Settings_are_validated_and_change_behavior()
    {
        await factory.CreateTenantAsync("ext-settings");
        var admin = await ApiClient.CreateAsync(factory, "ext-settings");
        await EnableAsync(admin);

        var defaults = await (await admin.GetAsync($"/v1.0/extensions/{Id}/settings", Ct)).ReadJsonAsync();
        Assert.Equal(1000, defaults.GetProperty("approvalThreshold").GetDecimal());
        Assert.Equal("EUR", defaults.GetProperty("currency").GetString());

        var invalid = await admin.PutAsJsonAsync($"/v1.0/extensions/{Id}/settings", new { approvalThreshold = "high", currency = "GBP", unknown = 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.ReadJsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("approvalThreshold", out _));
        Assert.True(errors.TryGetProperty("currency", out _));
        Assert.True(errors.TryGetProperty("unknown", out _));

        var saved = await admin.PutAsJsonAsync($"/v1.0/extensions/{Id}/settings", new { approvalThreshold = 10000 }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("EUR", (await saved.ReadJsonAsync()).GetProperty("currency").GetString());

        var ws = await admin.CreateWorkspaceAsync("Finance");
        var list = await CreateInvoiceListAsync(admin, ws);
        var item = await admin.CreateItemAsync(ws, list, new { fields = new { title = "INV-1", amount = 5000, status = "draft" } });
        Assert.Equal("draft", item.GetProperty("fields").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Receivers_only_run_for_their_content_types()
    {
        await factory.CreateTenantAsync("ext-filter");
        var admin = await ApiClient.CreateAsync(factory, "ext-filter");
        await EnableAsync(admin);
        var ws = await admin.CreateWorkspaceAsync("Finance");
        var quote = await admin.CreateContentTypeAsync("Quote", InvoiceFields[..2]);
        var list = await admin.CreateListAsync(ws, "Quotes", quote);

        var item = await admin.CreateItemAsync(ws, list, new { fields = new { title = "Q-1", amount = 5000, status = "draft" } });

        Assert.Equal("draft", item.GetProperty("fields").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Enabling_grants_member_scopes_and_needs_admin_rights()
    {
        await factory.CreateTenantAsync("ext-scopes");
        var admin = await ApiClient.CreateAsync(factory, "ext-scopes");
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, "ext-scopes", "member", "member-password-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await EnableAsync(member)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/v1.0/extensions", Ct)).StatusCode);

        await EnableAsync(admin);
        await Eventually.WaitForAsync<bool>(async () => (await member.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode == HttpStatusCode.OK ? true : null);
        var me = await (await member.GetAsync("/v1.0/me", Ct)).ReadJsonAsync();
        var scopes = me.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("samples.invoices.read", scopes);
        Assert.DoesNotContain("samples.invoices.approve", scopes);
    }

    [Fact]
    public async Task Enablement_is_per_tenant()
    {
        await factory.CreateTenantAsync("ext-tenant-a");
        await factory.CreateTenantAsync("ext-tenant-b");
        var a = await ApiClient.CreateAsync(factory, "ext-tenant-a");
        var b = await ApiClient.CreateAsync(factory, "ext-tenant-b");

        await EnableAsync(a);

        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
        var listB = await (await b.GetAsync("/v1.0/extensions", Ct)).ReadJsonAsync();
        Assert.False(listB.EnumerateArray().Single(e => e.GetProperty("id").GetString() == Id).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/v1.0/extensions/unknown.extension", Ct)).StatusCode);
    }

    [Fact]
    public async Task Extension_content_types_are_provisioned_and_managed_by_the_extension()
    {
        await factory.CreateTenantAsync("ext-content");
        var admin = await ApiClient.CreateAsync(factory, "ext-content");
        await admin.CreateContentTypeAsync("Invoice", [new { name = "ref", type = "text" }]);

        await EnableAsync(admin);

        var contentTypes = (await (await admin.GetAsync("/v1.0/contentTypes", Ct)).ReadJsonAsync()).EnumerateArray().ToList();
        var managed = Assert.Single(contentTypes, c => c.TryGetProperty("key", out var key) && key.GetString() == $"{Id}.invoice");
        Assert.Equal(Id, managed.GetProperty("extensionId").GetString());
        Assert.Equal("Invoice (2)", managed.GetProperty("name").GetString()); // the tenant's own "Invoice" keeps its name

        var url = $"/v1.0/contentTypes/{managed.GetProperty("id").GetGuid()}";
        var etag = (await admin.GetAsync(url, Ct)).Headers.ETag!.Tag;
        var change = await admin.SendWithEtagAsync(HttpMethod.Put, url, etag, new { name = "Mine", fields = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);

        // Enabling again keeps a single, in-sync content type.
        await EnableAsync(admin);
        var again = (await (await admin.GetAsync("/v1.0/contentTypes", Ct)).ReadJsonAsync()).EnumerateArray();
        Assert.Single(again, c => c.TryGetProperty("key", out var key) && key.GetString() == $"{Id}.invoice");

        var templates = await (await admin.GetAsync("/v1.0/listTemplates", Ct)).ReadJsonAsync();
        var invoices = Assert.Single(templates.EnumerateArray(), t => t.GetProperty("key").GetString() == $"{Id}.invoices");
        Assert.Equal(["All invoices", "Needs approval"], invoices.GetProperty("views").EnumerateArray().Select(v => v.GetString()));
    }
}
