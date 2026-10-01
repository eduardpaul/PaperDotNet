using System.Net;
using System.Net.Http.Headers;

namespace PaperDotNet.IntegrationTests;

/// <summary>The tenant of a request (T16d): custom hosts, a host template and the X-Tenant header, guarded against tokens of other tenants.</summary>
public sealed class TenancyTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(settings: new Dictionary<string, string>
    {
        ["Tenancy:AllowHeader"] = "true",
        ["Tenancy:HostTemplate"] = "{tenant}.dms.test",
    });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private HttpClient Client(string? host = null, string? tenantHeader = null, string? token = null)
    {
        var client = _host.CreateClient();
        if (host is not null)
        {
            client.BaseAddress = new Uri($"http://{host}");
        }

        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant", tenantHeader);
        }

        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static async Task<HttpResponseMessage> PasswordAsync(HttpClient client, string? tenant = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "admin", ["password"] = TestHost.AdminPassword };
        if (tenant is not null)
        {
            form["tenant"] = tenant;
        }

        return await client.PostAsync("/connect/token", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<string> TokenAsync(HttpResponseMessage response) =>
        (await response.JsonAsync(HttpStatusCode.OK)).GetProperty("access_token").GetString()!;

    [Fact]
    public async Task The_host_or_header_names_the_tenant_of_a_sign_in()
    {
        var admin = await _host.SignInAsync();
        await _host.CreateTenantAsync("acme");

        // Host template: acme.dms.test signs in to acme without a tenant parameter.
        var token = await TokenAsync(await PasswordAsync(Client("acme.dms.test")));
        var org = await (await Client("acme.dms.test", token: token).GetAsync("/v1.0/organization", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("acme", org.GetProperty("identifier").GetString());

        // Header, and no host or header: the default tenant.
        var viaHeader = await TokenAsync(await PasswordAsync(Client(tenantHeader: "acme")));
        Assert.Equal("acme", (await (await Client(token: viaHeader).GetAsync("/v1.0/organization", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("identifier").GetString());
        Assert.Equal("default", (await (await admin.GetAsync("/v1.0/organization", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("identifier").GetString());

        // An unknown tenant named explicitly is not replaced by the default.
        var unknown = await Client(tenantHeader: "nope").GetAsync("/v1.0/organization", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("tenantNotFound", (await unknown.JsonAsync(HttpStatusCode.NotFound)).GetProperty("code").GetString());
        Assert.False((await PasswordAsync(Client(tenantHeader: "nope"))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_custom_host_maps_to_its_tenant()
    {
        var created = await PaperDotNet.Host.Cli.AdminCli.RunAsync(_host.Services,
            ["tenant", "create", "--identifier", "globex", "--admin-password", TestHost.AdminPassword, "--host", "DMS.Globex.test"], TextWriter.Null, TextWriter.Null);
        Assert.Equal(0, created);

        var token = await TokenAsync(await PasswordAsync(Client("dms.globex.test")));
        var org = await (await Client("dms.globex.test", token: token).GetAsync("/v1.0/organization", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("globex", org.GetProperty("identifier").GetString());
        Assert.Equal(["dms.globex.test"], org.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));

        // The tenant parameter cannot name another tenant than the host.
        Assert.False((await PasswordAsync(Client("dms.globex.test"), "default")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_token_cannot_be_used_on_another_tenants_host()
    {
        await _host.CreateTenantAsync("mismatch-a");
        await _host.CreateTenantAsync("mismatch-b");
        var tokenA = await TokenAsync(await PasswordAsync(Client(), "mismatch-a"));

        var spoofing = await Client(tenantHeader: "mismatch-b", token: tokenA).GetAsync("/v1.0/workspaces", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, spoofing.StatusCode);
        Assert.Equal("tenantMismatch", (await spoofing.JsonAsync(HttpStatusCode.Forbidden)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("mismatch-b.dms.test", token: tokenA).GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client("mismatch-a.dms.test", token: tokenA).GetAsync("/v1.0/workspaces", Ct)).StatusCode);
    }
}
