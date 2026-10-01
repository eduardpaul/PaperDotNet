using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Extensions.Testing;
using PaperDotNet.Lists.Contracts;

[assembly: AssemblyFixture(typeof(PaperDotNet.Samples.Invoices.Tests.InvoicesHost))]

namespace PaperDotNet.Samples.Invoices.Tests;

/// <summary>One host for the test run; every test creates its own tenant.</summary>
public sealed class InvoicesHost() : ExtensionTestHost(new InvoicesExtension());

public sealed class InvoicesExtensionTests(InvoicesHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Guid Workspace, Guid List)> CreateInvoiceListAsync(HttpClient admin)
    {
        var workspace = await (await admin.PostAsJsonAsync("/v1.0/workspaces", new { name = "Finance" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ws = workspace.GetProperty("id").GetGuid();
        var list = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Invoices", templateKey = $"{InvoicesExtension.Id}.invoices" }, Ct);
        Assert.Equal(HttpStatusCode.Created, list.StatusCode);
        return (ws, (await list.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Large_invoices_wait_for_approval_and_can_be_approved()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);

        var item = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .CreateAsync(ws, list, new JsonObject { ["title"] = "INV-1", ["amount"] = 5000 }, null, Ct), Ct);
        Assert.Equal("pendingApproval", item.Item!.Fields["status"]!.GetValue<string>());

        var approve = await admin.PostAsJsonAsync($"/v1.0/ext/{InvoicesExtension.Id}/workspaces/{ws}/lists/{list}/items/{item.Item.Id}/approve", new { comment = "ok" }, Ct);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/v1.0/ext/{InvoicesExtension.Id}/workspaces/{ws}/lists/{list}/items/{item.Item.Id}/approve", new { }, Ct)).StatusCode);

        var approved = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin).GetAsync(ws, list, item.Item.Id, Ct), Ct);
        Assert.Equal("approved", approved!.Fields["status"]!.GetValue<string>());

        // The approval is a row of the extension's own table, visible only in its tenant.
        var approvals = await (await admin.GetAsync($"/v1.0/ext/{InvoicesExtension.Id}/approvals", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var approval = Assert.Single(approvals.EnumerateArray());
        Assert.Equal(item.Item.Id, approval.GetProperty("itemId").GetGuid());
        Assert.Equal("ok", approval.GetProperty("comment").GetString());
        Assert.Equal(5000, approval.GetProperty("amount").GetDecimal());
        Assert.Equal(tenant.AdminUserId, approval.GetProperty("approvedBy").GetGuid());

        var other = await host.CreateTenantAsync(cancellationToken: Ct);
        using var otherAdmin = await other.CreateClientAsync(cancellationToken: Ct);
        Assert.Equal(0, (await (await otherAdmin.GetAsync($"/v1.0/ext/{InvoicesExtension.Id}/approvals", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetArrayLength());
    }

    [Fact]
    public async Task The_reminder_job_counts_invoices_waiting_for_approval()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);
        foreach (var amount in new[] { 50, 2000, 3000 })
        {
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items", new { fields = new { title = $"INV {amount}", amount } }, Ct)).StatusCode);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement stats;
        do
        {
            await Task.Delay(200, Ct);
            stats = await (await admin.GetAsync($"/v1.0/ext/{InvoicesExtension.Id}/stats", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        }
        while ((stats.GetProperty("pendingApprovals").GetInt32() != 2 || stats.GetProperty("itemsAdded").GetInt32() != 3) && DateTime.UtcNow < deadline);

        Assert.Equal(2, stats.GetProperty("pendingApprovals").GetInt32());
        Assert.Equal(3, stats.GetProperty("itemsAdded").GetInt32());
    }

    [Fact]
    public async Task The_mutator_finds_the_extension_content_type_when_the_tenant_has_one_of_the_same_name()
    {
        var tenant = await host.CreateTenantAsync(enableExtensions: false, cancellationToken: Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/v1.0/contentTypes", new { name = "Invoice", fields = Array.Empty<object>() }, Ct)).StatusCode);
        await tenant.EnableAsync(InvoicesExtension.Id, Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);

        var item = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items", new { fields = new { title = "INV-1", amount = 5000 } }, Ct);

        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        Assert.Equal("pendingApproval", (await item.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("fields").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Settings_change_the_threshold()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        await tenant.ConfigureAsync(InvoicesExtension.Id, new { approvalThreshold = 10000 }, Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);

        var item = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .CreateAsync(ws, list, new JsonObject { ["title"] = "INV-1", ["amount"] = 5000 }, null, Ct), Ct);

        Assert.Equal("draft", item.Item!.Fields["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Members_cannot_approve()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        using var member = await tenant.CreateUserAsync("member", cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);

        var response = await member.PostAsJsonAsync($"/v1.0/ext/{InvoicesExtension.Id}/workspaces/{ws}/lists/{list}/items/{Guid.Empty}/approve", new { }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Members get the read scope of the manifest.
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/v1.0/ext/{InvoicesExtension.Id}/stats", Ct)).StatusCode);
    }
}
