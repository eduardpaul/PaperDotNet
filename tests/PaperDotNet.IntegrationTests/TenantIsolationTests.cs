using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;

namespace PaperDotNet.IntegrationTests;

/// <summary>Mandatory tenant isolation checks (docs/technical-approach.md §4).</summary>
public sealed class TenantIsolationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Data_of_another_tenant_is_invisible()
    {
        var a = await factory.CreateTenantAsync("isolation-a");
        var b = await factory.CreateTenantAsync("isolation-b");
        var clientA = await ApiClient.CreateAsync(factory, a.Identifier);
        var clientB = await ApiClient.CreateAsync(factory, b.Identifier);
        var workspaceA = await clientA.CreateWorkspaceAsync("Secret A");

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/workspaces/{workspaceA}", Ct)).StatusCode);
        var listB = (await (await clientB.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value");
        Assert.DoesNotContain(listB.EnumerateArray(), w => w.GetProperty("id").GetGuid() == workspaceA);

        var usersB = (await (await clientB.GetAsync("/v1.0/users", Ct)).ReadJsonAsync()).GetProperty("value");
        Assert.Equal(1, usersB.GetArrayLength());
    }

    [Fact]
    public async Task Postgresql_row_level_security_hides_other_tenants_without_ef()
    {
        Assert.SkipUnless(PaperDotNetApiFactory.Provider == "postgresql", "Row-level security is PostgreSQL only.");
        var a = await factory.CreateTenantAsync("rls-a");
        var b = await factory.CreateTenantAsync("rls-b");
        await (await ApiClient.CreateAsync(factory, a.Identifier)).CreateWorkspaceAsync("RLS A");
        await (await ApiClient.CreateAsync(factory, b.Identifier)).CreateWorkspaceAsync("RLS B");

        // Raw SQL as the app's role: only the database policies protect the data here.
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        async Task<long> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM workspaces.workspaces"));
        await ScalarAsync($"SELECT count(*) FROM (SELECT set_config('app.tenant_id', '{a.Id}', false)) s");
        Assert.True(await ScalarAsync("SELECT count(*) FROM workspaces.workspaces") >= 1);
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM workspaces.workspaces WHERE tenant_id <> '{a.Id}'"));

        await using var update = new NpgsqlCommand($"UPDATE workspaces.workspaces SET name = 'hacked' WHERE tenant_id = '{b.Id}'", connection);
        Assert.Equal(0, await update.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task A_token_cannot_be_used_against_another_tenant()
    {
        var a = await factory.CreateTenantAsync("mismatch-a");
        await factory.CreateTenantAsync("mismatch-b");
        var tokenA = await ApiClient.GetTokenAsync(await ApiClient.CreateAsync(factory, a.Identifier, userName: null), "admin", PaperDotNetApiFactory.AdminPassword);

        var spoofing = factory.CreateClient();
        spoofing.DefaultRequestHeaders.Add("X-Tenant", "mismatch-b");
        spoofing.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var response = await spoofing.GetAsync("/v1.0/workspaces", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenantMismatch", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task User_names_are_unique_per_tenant_only()
    {
        var a = await factory.CreateTenantAsync("names-a");
        var b = await factory.CreateTenantAsync("names-b");
        var clientA = await ApiClient.CreateAsync(factory, a.Identifier);
        var clientB = await ApiClient.CreateAsync(factory, b.Identifier);

        var inA = await clientA.PostAsJsonAsync("/v1.0/users", new { userName = "sam", password = "sam-password-123" }, Ct);
        var inB = await clientB.PostAsJsonAsync("/v1.0/users", new { userName = "sam", password = "sam-password-123" }, Ct);
        var duplicate = await clientA.PostAsJsonAsync("/v1.0/users", new { userName = "sam", password = "sam-password-123" }, Ct);

        Assert.Equal(HttpStatusCode.Created, inA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inB.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task An_explicitly_requested_unknown_tenant_is_not_replaced_by_the_default()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", "does-not-exist");

        var response = await client.PostAsJsonAsync("/v1.0/auth/login", new { userName = "admin", password = PaperDotNetApiFactory.AdminPassword }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tenantNotFound", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }
}
