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
    private static readonly string[] Approvers = ["creator"];

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

    [Fact]
    public async Task A_run_waits_until_the_invoice_is_paid()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);
        var workflow = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows", new
        {
            name = "Chase payment",
            definition = JsonNode.Parse("""
                {
                  "trigger": { "type": "manual", "list": "Invoices" },
                  "flow": { "start": "wait", "nodes": {
                    "wait": { "activity": "samples.invoices.awaitPayment", "inputs": { "days": 14 }, "next": { "paid": "thanks" } },
                    "thanks": { "activity": "item.update", "inputs": { "fields": { "title": "Paid" } } }
                  } }
                }
                """),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, workflow.StatusCode);
        var workflowId = (await workflow.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var item = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .CreateAsync(ws, list, new JsonObject { ["title"] = "INV-2", ["amount"] = 10 }, null, Ct), Ct);
        var started = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows/{workflowId}/runs", new { listId = list, itemId = item.Item!.Id }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = $"/v1.0/workspaces/{ws}/workflows/runs/{(await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid()}";

        async Task<string> StatusAsync(string wanted)
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var status = (await (await admin.GetAsync(run, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString()!;
                if (status == wanted)
                {
                    return status;
                }

                await Task.Delay(100, Ct);
            }

            return "timed out waiting for " + wanted;
        }

        Assert.Equal("waiting", await StatusAsync("waiting"));
        await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .UpdateAsync(ws, list, item.Item.Id, new JsonObject { ["status"] = "paid" }, null, Ct), Ct);
        Assert.Equal("completed", await StatusAsync("completed"));
        var paid = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin).GetAsync(ws, list, item.Item.Id, Ct), Ct);
        Assert.Equal("Paid", paid!.Fields["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Large_invoices_raise_the_approval_trigger_for_workflows()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);
        var triggers = await (await admin.GetAsync("/v1.0/workflows/triggers", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains(triggers.EnumerateArray(), t => t.GetProperty("key").GetString() == "samples.invoices.approvalNeeded");
        var workflow = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows", new
        {
            name = "Flag",
            definition = JsonNode.Parse("""
                { "trigger": { "type": "samples.invoices.approvalNeeded", "list": "Invoices" },
                  "flow": { "start": "flag", "nodes": { "flag": { "activity": "item.update", "inputs": { "fields": { "title": "Check {title} ({data:amount})" } } } } } }
                """),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, workflow.StatusCode);

        var big = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .CreateAsync(ws, list, new JsonObject { ["title"] = "INV-9", ["amount"] = 5000 }, null, Ct), Ct);
        var small = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .CreateAsync(ws, list, new JsonObject { ["title"] = "INV-10", ["amount"] = 5 }, null, Ct), Ct);
        string? title = null;
        for (var attempt = 0; attempt < 300 && title != "Check INV-9 (5000)"; attempt++)
        {
            await Task.Delay(100, Ct);
            title = (await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin).GetAsync(ws, list, big.Item!.Id, Ct), Ct))!
                .Fields["title"]!.GetValue<string>();
        }

        Assert.Equal("Check INV-9 (5000)", title);
        Assert.Equal("INV-10", (await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .GetAsync(ws, list, small.Item!.Id, Ct), Ct))!.Fields["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_extension_ships_a_workflow_offered_where_it_is_enabled()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);
        var builtIns = $"/v1.0/workspaces/{ws}/workflows/builtIns";
        const string key = "samples.invoices.approveAndCollect";
        var catalog = await (await admin.GetAsync(builtIns, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains(catalog.EnumerateArray(), b => b.GetProperty("key").GetString() == key);
        var set = await admin.PutAsJsonAsync($"{builtIns}/{key}", new { enabled = true, parameters = new { list = "Invoices", approvers = Approvers } }, Ct);
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync(Ct));

        // It runs with the extension's trigger and activities and the core ones: approve, mark approved, wait for payment.
        var invoice = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .CreateAsync(ws, list, new JsonObject { ["title"] = "Big", ["amount"] = 5000 }, null, Ct), Ct);
        string? approval = null;
        for (var attempt = 0; attempt < 300 && approval is null; attempt++)
        {
            await Task.Delay(100, Ct);
            var pending = await (await admin.GetAsync("/v1.0/me/approvals?status=pending", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            approval = pending.GetProperty("value").EnumerateArray().Select(a => a.GetProperty("id").GetString()).FirstOrDefault();
        }

        Assert.NotNull(approval);
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome = "approved" }, Ct)).IsSuccessStatusCode);
        async Task<string?> StatusAsync() => (await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().ActingAs(tenant.Admin)
            .GetAsync(ws, list, invoice.Item!.Id, Ct), Ct))!.Fields["status"]!.GetValue<string>();
        for (var attempt = 0; attempt < 300 && await StatusAsync() != "approved"; attempt++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Equal("approved", await StatusAsync());

        // Not offered where the extension is turned off.
        await tenant.DisableAsync(InvoicesExtension.Id, Ct);
        catalog = await (await admin.GetAsync(builtIns, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.DoesNotContain(catalog.EnumerateArray(), b => b.GetProperty("key").GetString() == key);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{builtIns}/{key}", new { enabled = true }, Ct)).StatusCode);
    }
}
