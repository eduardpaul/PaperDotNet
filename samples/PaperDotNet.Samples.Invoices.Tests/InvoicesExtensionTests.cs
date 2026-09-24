using System.Net;
using System.Net.Http.Json;
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

        var item = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>()
            .CreateAsync(ws, list, new JsonObject { ["title"] = "INV-1", ["amount"] = 5000 }, null, Ct), cancellationToken: Ct);
        Assert.Equal("pendingApproval", item.Item!.Fields["status"]!.GetValue<string>());

        var approve = await admin.PostAsJsonAsync($"/v1.0/ext/{InvoicesExtension.Id}/workspaces/{ws}/lists/{list}/items/{item.Item.Id}/approve", new { comment = "ok" }, Ct);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var approved = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>().GetAsync(ws, list, item.Item.Id, Ct), cancellationToken: Ct);
        Assert.Equal("approved", approved!.Fields["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_change_the_threshold()
    {
        var tenant = await host.CreateTenantAsync(cancellationToken: Ct);
        await tenant.ConfigureAsync(InvoicesExtension.Id, new { approvalThreshold = 10000 }, Ct);
        using var admin = await tenant.CreateClientAsync(cancellationToken: Ct);
        var (ws, list) = await CreateInvoiceListAsync(admin);

        var item = await tenant.RunAsync(services => services.GetRequiredService<IListItemStore>()
            .CreateAsync(ws, list, new JsonObject { ["title"] = "INV-1", ["amount"] = 5000 }, null, Ct), cancellationToken: Ct);

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
    }
}
