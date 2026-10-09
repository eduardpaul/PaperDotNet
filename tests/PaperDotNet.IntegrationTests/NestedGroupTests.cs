using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Groups inside groups (ADR-0035): membership, permissions, roles, limits and templates.</summary>
public sealed class NestedGroupTests(PaperDotNetApiFactory factory)
{
    private static readonly XNamespace T = "urn:paperdotnet:template:1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> GroupAsync(HttpClient admin, string name) =>
        (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> NestAsync(HttpClient admin, Guid group, Guid member) =>
        admin.PostAsJsonAsync($"/v1.0/groups/{group}/groups", new { groupId = member }, Ct);

    private static async Task<Guid> UserAsync(HttpClient admin, string name) =>
        (await (await admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();

    [Fact]
    public async Task Members_of_a_group_inside_a_group_get_its_access_until_it_is_taken_out()
    {
        await factory.CreateTenantAsync("nest-access");
        var admin = await ApiClient.CreateAsync(factory, "nest-access");
        var ws = await admin.CreateWorkspaceAsync("Audit");
        var list = await admin.CreateListAsync(ws, "Reports", await admin.CreateContentTypeAsync("Report", []));
        var intern = await UserAsync(admin, "intern");
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = intern, role = "visitor" }, Ct);
        var auditors = await GroupAsync(admin, "Auditors");
        var juniors = await GroupAsync(admin, "Juniors");
        var interns = await GroupAsync(admin, "Interns");
        await admin.PostAsJsonAsync($"/v1.0/groups/{interns}/members", new { userId = intern }, Ct);

        var listUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        var folder = (await admin.CreateItemAsync(ws, list, new { isFolder = true, fields = new { title = "Findings" } })).GetProperty("id").GetGuid();
        await admin.CreateItemAsync(ws, list, new { parentId = folder, fields = new { title = "Q3" } });
        await admin.PostAsJsonAsync($"{listUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await admin.PutAsJsonAsync($"{listUrl}/items/{folder}/permissions/grants",
            new { grants = new[] { new { principalType = "group", principalId = auditors, level = "read" } } }, Ct);

        var client = await ApiClient.CreateAsync(factory, "nest-access", "intern", "intern-password-1");
        Assert.Empty(await client.QueryTitlesAsync(ws, list, ""));

        // Interns → Juniors → Auditors: the grant to Auditors reaches the intern two levels down, at once.
        Assert.Equal(HttpStatusCode.NoContent, (await NestAsync(admin, juniors, interns)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await NestAsync(admin, auditors, juniors)).StatusCode);
        Assert.Equal(["Findings", "Q3"], (await client.QueryTitlesAsync(ws, list, "")).Order(StringComparer.Ordinal));
        var nested = await (await admin.GetAsync($"/v1.0/groups/{auditors}/groups", Ct)).ReadJsonAsync();
        Assert.Equal("Juniors", Assert.Single(nested.EnumerateArray()).GetProperty("name").GetString());

        // Group notifications and inboxes see the members of groups inside.
        var tenant = (await factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<PaperDotNet.Tenancy.Contracts.ITenantDirectory>().FindAsync("nest-access", Ct))!;
        await using (var scope = factory.Services.GetRequiredService<PaperDotNet.Abstractions.ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
            Assert.Contains(intern, await directory.GetGroupMembersAsync(auditors, Ct));
            Assert.Equal(new[] { auditors, interns, juniors }.Order(), (await directory.GetGroupIdsAsync(intern, Ct)).Order());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/groups/{auditors}/groups/{juniors}", Ct)).StatusCode);
        Assert.Empty(await client.QueryTitlesAsync(ws, list, ""));

        // Deleting a group takes it out of the groups it was in.
        await NestAsync(admin, auditors, juniors);
        Assert.Equal(2, (await client.QueryTitlesAsync(ws, list, "")).Count);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/groups/{juniors}", Ct)).StatusCode);
        Assert.Empty(await client.QueryTitlesAsync(ws, list, ""));
        Assert.Equal(0, (await (await admin.GetAsync($"/v1.0/groups/{auditors}/groups", Ct)).ReadJsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Effective_members_and_assignable_people_are_listed_per_tenant()
    {
        await factory.CreateTenantAsync("nest-members-a");
        await factory.CreateTenantAsync("nest-members-b");
        var admin = await ApiClient.CreateAsync(factory, "nest-members-a");
        var other = await ApiClient.CreateAsync(factory, "nest-members-b");
        var lead = await UserAsync(admin, "lead");
        var junior = await UserAsync(admin, "junior");
        var off = await UserAsync(admin, "off");
        var leads = await GroupAsync(admin, "Leads");
        var juniors = await GroupAsync(admin, "Juniors");
        await NestAsync(admin, leads, juniors);
        await admin.PostAsJsonAsync($"/v1.0/groups/{leads}/members", new { userId = lead }, Ct);
        await admin.PostAsJsonAsync($"/v1.0/groups/{juniors}/members", new { userId = junior }, Ct);
        await admin.PostAsJsonAsync($"/v1.0/groups/{juniors}/members", new { userId = off }, Ct);
        var disabling = new HttpRequestMessage(HttpMethod.Patch, $"/v1.0/users/{off}") { Content = JsonContent.Create(new { isDisabled = true }) };
        Assert.True((await admin.SendAsync(disabling, Ct)).IsSuccessStatusCode);
        var application = await admin.PostAsJsonAsync("/v1.0/applications",
            new { displayName = "Robot", clientType = "confidential", grantTypes = new[] { "client_credentials" }, scopes = new[] { "workspace.read" } }, Ct);
        var robot = (await application.ReadJsonAsync()).GetProperty("application").GetProperty("serviceUserId").GetGuid();
        await admin.PostAsJsonAsync($"/v1.0/groups/{leads}/members", new { userId = robot }, Ct);

        static async Task<Guid[]> IdsAsync(HttpClient client, string url)
        {
            var json = await (await client.GetAsync(url, Ct)).ReadJsonAsync();
            var rows = json.ValueKind == System.Text.Json.JsonValueKind.Array ? json : json.GetProperty("value");
            return [.. rows.EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).Order()];
        }

        // Direct members, effective members (groups inside included), and the people among them to pick.
        Assert.Equal(new[] { lead, robot }.Order(), await IdsAsync(admin, $"/v1.0/groups/{leads}/members"));
        Assert.Equal(new[] { lead, junior, off, robot }.Order(), await IdsAsync(admin, $"/v1.0/groups/{leads}/members?transitive=true"));
        Assert.Equal(new[] { lead, junior }.Order(), await IdsAsync(admin, $"/v1.0/groups/{leads}/members?transitive=true&assignable=true"));
        var assignable = await IdsAsync(admin, "/v1.0/users?assignable=true&top=200");
        Assert.Contains(junior, assignable);
        Assert.DoesNotContain(robot, assignable);
        Assert.DoesNotContain(off, assignable);
        Assert.Equal(new[] { junior, robot }.Order(), await IdsAsync(admin, $"/v1.0/users?ids={junior},{leads},{robot}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/v1.0/users?ids=nope", Ct)).StatusCode);

        // Another tenant sees neither the group nor the users.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/groups/{leads}/members?transitive=true", Ct)).StatusCode);
        Assert.Empty(await IdsAsync(other, $"/v1.0/users?ids={junior},{lead}"));
        Assert.DoesNotContain(junior, await IdsAsync(other, "/v1.0/users?assignable=true&top=200"));
    }

    [Fact]
    public async Task Roles_of_a_group_reach_the_groups_inside_it()
    {
        await factory.CreateTenantAsync("nest-roles");
        var admin = await ApiClient.CreateAsync(factory, "nest-roles");
        await UserAsync(admin, "ops");
        var managers = await GroupAsync(admin, "Role managers");
        var team = await GroupAsync(admin, "Ops team");
        var opsId = (await (await admin.GetAsync("/v1.0/users", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(u => u.GetProperty("userName").GetString() == "ops").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync($"/v1.0/groups/{team}/members", new { userId = opsId }, Ct);
        var role = (await (await admin.PostAsJsonAsync("/v1.0/roles", new { name = "Role readers", scopes = new[] { "role.read" } }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync($"/v1.0/roles/{role}/assignments", new { principalId = managers, principalType = "group" }, Ct);

        var ops = await ApiClient.CreateAsync(factory, "nest-roles", "ops", "ops-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/v1.0/roles", Ct)).StatusCode);
        await NestAsync(admin, managers, team);
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync("/v1.0/roles", Ct)).StatusCode);
    }

    [Fact]
    public async Task Nesting_rejects_cycles_and_more_than_ten_levels()
    {
        await factory.CreateTenantAsync("nest-limits");
        var admin = await ApiClient.CreateAsync(factory, "nest-limits");
        var chain = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            chain.Add(await GroupAsync(admin, $"Level {i}"));
        }

        Assert.Equal(HttpStatusCode.Conflict, (await NestAsync(admin, chain[0], chain[0])).StatusCode);
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await NestAsync(admin, chain[i], chain[i + 1])).StatusCode);
        }

        // Level 0 … Level 9 is ten levels; an eleventh is refused, and so is closing the chain into a cycle.
        Assert.Equal(HttpStatusCode.Conflict, (await NestAsync(admin, chain[9], chain[10])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await NestAsync(admin, chain[5], chain[2])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await NestAsync(admin, chain[0], Guid.NewGuid())).StatusCode);

        // Other tenants cannot see or change the nesting.
        await factory.CreateTenantAsync("nest-limits-b");
        var other = await ApiClient.CreateAsync(factory, "nest-limits-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/groups/{chain[0]}/groups", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/groups/{chain[0]}/groups/{chain[1]}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Templates_carry_groups_inside_groups_and_role_grants()
    {
        await factory.CreateTenantAsync("nest-tpl-src");
        await factory.CreateTenantAsync("nest-tpl-dst");
        var source = await ApiClient.CreateAsync(factory, "nest-tpl-src");
        var target = await ApiClient.CreateAsync(factory, "nest-tpl-dst");
        var outer = await GroupAsync(source, "Legal");
        var inner = await GroupAsync(source, "Paralegals");
        await NestAsync(source, outer, inner);
        var ws = await source.CreateWorkspaceAsync("Contracts");
        var list = await source.CreateListAsync(ws, "Drafts", await source.CreateContentTypeAsync("Draft", []));
        var listUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        await source.PostAsJsonAsync($"{listUrl}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        var put = await source.PutAsJsonAsync($"{listUrl}/permissions/grants", new
        {
            grants = new object[]
            {
                new { principalType = "workspaceMembers", principalId = ws, level = "read" },
                new { principalType = "group", principalId = outer, level = "contribute" },
            },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var export = await source.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct);
        var xml = await export.Content.ReadAsStringAsync(Ct);
        var document = XDocument.Parse(xml);
        var legal = document.Descendants(T + "Group").Single(g => g.Attribute("Name")!.Value == "Legal");
        Assert.Equal("Paralegals", Assert.Single(legal.Elements(T + "Member")).Attribute("Group")!.Value);
        Assert.Contains(document.Descendants(T + "Grant"), g => g.Attribute("Role")?.Value == "Members" && g.Attribute("Level")!.Value == "Read");

        var applied = await target.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, Encoding.UTF8, "application/xml"), Ct);
        Assert.True(applied.StatusCode == HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        var groups = (await (await target.GetAsync("/v1.0/groups", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .ToDictionary(g => g.GetProperty("name").GetString()!, g => g.GetProperty("id").GetGuid());
        var nested = await (await target.GetAsync($"/v1.0/groups/{groups["Legal"]}/groups", Ct)).ReadJsonAsync();
        Assert.Equal("Paralegals", Assert.Single(nested.EnumerateArray()).GetProperty("name").GetString());

        var targetWs = (await (await target.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Contracts").GetProperty("id").GetGuid();
        var targetList = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists", Ct)).ReadJsonAsync()).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Drafts").GetProperty("id").GetGuid();
        var permissions = await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetList}/permissions", Ct)).ReadJsonAsync();
        Assert.Equal(
            [$"group:{groups["Legal"]}:contribute", $"workspaceMembers:{targetWs}:read", $"workspaceOwners:{targetWs}:manage"],
            permissions.GetProperty("grants").EnumerateArray()
                .Select(g => $"{g.GetProperty("principalType").GetString()}:{g.GetProperty("principalId").GetGuid()}:{g.GetProperty("level").GetString()}")
                .Order(StringComparer.Ordinal));
    }
}
