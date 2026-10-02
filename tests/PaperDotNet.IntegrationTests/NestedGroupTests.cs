using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Groups inside groups (ADR-0035): item access through nesting, the directory, and templates.</summary>
public sealed class NestedGroupTests : IAsyncLifetime
{
    private static readonly XNamespace T = "urn:paperdotnet:template:1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<string> GroupAsync(HttpClient admin, string name)
    {
        using var response = await admin.PostAsJsonAsync("/v1.0/groups", new { name }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static Task<HttpResponseMessage> NestAsync(HttpClient admin, string group, string member) =>
        admin.PostAsJsonAsync($"/v1.0/groups/{group}/groups", new { groupId = member }, Ct);

    private static async Task<List<string?>> TitlesAsync(HttpClient client, string ws, string list) =>
        [.. (await (await client.GetAsync(Api.Items(ws, list), Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Select(i => i.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal)];

    private static JsonElement.ArrayEnumerator Values(JsonElement response) =>
        (response.ValueKind == JsonValueKind.Array ? response : response.GetProperty("value")).EnumerateArray();

    [Fact]
    public async Task Members_of_a_group_inside_a_group_get_its_access_until_it_is_taken_out()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Audit");
        var list = (await Api.CreateListAsync(_admin, ws, "Reports")).Id();
        using (var created = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = "intern", password = "intern-password-1" }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var intern = (await (await (await _host.SignInAsync("intern", "intern-password-1")).GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id();
        await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = intern, role = "visitor" }, Ct);
        var auditors = await GroupAsync(_admin, "Auditors");
        var juniors = await GroupAsync(_admin, "Juniors");
        var interns = await GroupAsync(_admin, "Interns");
        await _admin.PostAsJsonAsync($"/v1.0/groups/{interns}/members", new { userId = intern }, Ct);

        var listUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        using var folderResponse = await _admin.PostAsJsonAsync(Api.Items(ws, list), new { isFolder = true, fields = new { title = "Findings" } }, Ct);
        var folder = (await folderResponse.JsonAsync(HttpStatusCode.Created)).Id();
        using (var inside = await _admin.PostAsJsonAsync(Api.Items(ws, list), new { parentId = folder, fields = new { title = "Q3" } }, Ct))
        {
            await inside.JsonAsync(HttpStatusCode.Created);
        }

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"{listUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"{listUrl}/items/{folder}/permissions/grants",
            new { grants = new[] { new { principalType = "group", principalId = auditors, level = "read" } } }, Ct)).StatusCode);

        var client = await _host.SignInAsync("intern", "intern-password-1");
        Assert.Empty(await TitlesAsync(client, ws, list));

        // Interns → Juniors → Auditors: the grant to Auditors reaches the intern two levels down, at once.
        Assert.Equal(HttpStatusCode.NoContent, (await NestAsync(_admin, juniors, interns)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await NestAsync(_admin, auditors, juniors)).StatusCode);
        Assert.Equal(["Findings", "Q3"], await TitlesAsync(client, ws, list));

        // Group notifications and inboxes see the members of groups inside.
        var tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
            Assert.Contains(Guid.Parse(intern), await directory.GetGroupMembersAsync(tenant, Guid.Parse(auditors), Ct));
            Assert.Equal(new[] { auditors, interns, juniors }.Select(Guid.Parse).Order(), (await directory.GetGroupIdsAsync(tenant, Guid.Parse(intern), Ct)).Order());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/groups/{auditors}/groups/{juniors}", Ct)).StatusCode);
        Assert.Empty(await TitlesAsync(client, ws, list));

        // Deleting a group takes it out of the groups it was in.
        await NestAsync(_admin, auditors, juniors);
        Assert.Equal(2, (await TitlesAsync(client, ws, list)).Count);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/groups/{juniors}", Ct)).StatusCode);
        Assert.Empty(await TitlesAsync(client, ws, list));
        Assert.Empty(Values(await (await _admin.GetAsync($"/v1.0/groups/{auditors}/groups", Ct)).JsonAsync(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task Templates_carry_groups_inside_groups_and_role_grants()
    {
        var target = await _host.CreateTenantAsync("nest-tpl-dst");
        var outer = await GroupAsync(_admin, "Legal");
        var inner = await GroupAsync(_admin, "Paralegals");
        await NestAsync(_admin, outer, inner);
        var ws = await Api.CreateWorkspaceAsync(_admin, "Contracts");
        var list = (await Api.CreateListAsync(_admin, ws, "Drafts")).Id();
        var listUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        await _admin.PostAsJsonAsync($"{listUrl}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        var put = await _admin.PutAsJsonAsync($"{listUrl}/permissions/grants", new
        {
            grants = new object[]
            {
                new { principalType = "workspaceMembers", principalId = ws, level = "read" },
                new { principalType = "group", principalId = outer, level = "contribute" },
            },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var xml = await (await _admin.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct)).Content.ReadAsStringAsync(Ct);
        var document = XDocument.Parse(xml);
        var legal = document.Descendants(T + "Group").Single(g => g.Attribute("Name")!.Value == "Legal");
        Assert.Equal("Paralegals", Assert.Single(legal.Elements(T + "Member")).Attribute("Group")!.Value);

        var applied = await target.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, Encoding.UTF8, "application/xml"), Ct);
        Assert.True(applied.StatusCode == HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        var groups = Values(await (await target.GetAsync("/v1.0/groups", Ct)).JsonAsync(HttpStatusCode.OK))
            .ToDictionary(g => g.GetProperty("name").GetString()!, g => g.Id());
        var nested = await (await target.GetAsync($"/v1.0/groups/{groups["Legal"]}/groups", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Paralegals", Assert.Single(Values(nested)).GetProperty("name").GetString());

        var targetWs = Values(await (await target.GetAsync("/v1.0/workspaces", Ct)).JsonAsync(HttpStatusCode.OK)).Single(w => w.GetProperty("name").GetString() == "Contracts").Id();
        var targetList = Values(await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists", Ct)).JsonAsync(HttpStatusCode.OK))
            .Single(l => l.GetProperty("name").GetString() == "Drafts").Id();
        var permissions = await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetList}/permissions", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Contains($"group:{groups["Legal"]}:contribute", permissions.GetProperty("grants").EnumerateArray()
            .Select(g => $"{g.GetProperty("principalType").GetString()}:{g.GetProperty("principalId").GetString()}:{g.GetProperty("level").GetString()}"));
    }
}
