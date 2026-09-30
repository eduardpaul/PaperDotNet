using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Account lifecycle (IAM-14), groups inside groups (ADR-0035) and roles.</summary>
public sealed class IdentityTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<Guid> UserAsync(string userName, string? password = null)
    {
        using var response = await _admin.PostAsJsonAsync("/v1.0/users", new { userName, password = password ?? $"{userName}-password-1" }, Ct);
        return Guid.Parse((await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private async Task<Guid> GroupAsync(string name)
    {
        using var response = await _admin.PostAsJsonAsync("/v1.0/groups", new { name }, Ct);
        return Guid.Parse((await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private async Task<Guid> RoleAsync(string name, params string[] scopes)
    {
        using var response = await _admin.PostAsJsonAsync("/v1.0/roles", new { name, scopes }, Ct);
        return Guid.Parse((await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private async Task<Guid> AdministratorRoleAsync() =>
        Guid.Parse((await (await _admin.GetAsync("/v1.0/roles", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "Administrator").Id());

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, object body, string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return JsonElement.Parse(text).TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<bool> CanSignInAsync(string userName, string password)
    {
        using var response = await _host.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = userName,
            ["password"] = password,
        }), Ct);
        return response.IsSuccessStatusCode;
    }

    [Fact]
    public async Task Users_change_their_own_display_name()
    {
        await UserAsync("carol");
        var carol = await _host.SignInAsync("carol", "carol-password-1");

        using var updated = await PatchAsync(carol, "/v1.0/me", new { displayName = "  Carol C.  " });
        Assert.Equal("Carol C.", (await updated.JsonAsync(HttpStatusCode.OK)).GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(carol, "/v1.0/me", new { displayName = new string('x', 201) })).StatusCode);

        // An empty name falls back to the user name; the administrator keeps theirs.
        using var reset = await PatchAsync(carol, "/v1.0/me", new { displayName = "" });
        Assert.Equal("carol", (await reset.JsonAsync(HttpStatusCode.OK)).GetProperty("displayName").GetString());
        Assert.Equal("admin", (await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Users_are_updated_disabled_and_their_passwords_reset_or_changed()
    {
        var bob = await UserAsync("bob");

        using (var updated = await PatchAsync(_admin, $"/v1.0/users/{bob}", new { displayName = "Bob Builder", email = "bob@example.com" }))
        {
            Assert.Equal("bob@example.com", (await updated.JsonAsync(HttpStatusCode.OK)).GetProperty("email").GetString());
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(_admin, $"/v1.0/users/{bob}", new { email = "not-an-address" })).StatusCode);
        using (var cleared = await PatchAsync(_admin, $"/v1.0/users/{bob}", new { email = "" }))
        {
            Assert.Equal(JsonValueKind.Null, (await cleared.JsonAsync(HttpStatusCode.OK)).GetProperty("email").ValueKind);
        }

        // Disabled: tokens stop working and the password grant fails; enabled again, sign-in works.
        var bobClient = await _host.SignInAsync("bob", "bob-password-1");
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, $"/v1.0/users/{bob}", new { isDisabled = true })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await bobClient.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.False(await CanSignInAsync("bob", "bob-password-1"));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, $"/v1.0/users/{bob}", new { isDisabled = false })).StatusCode);

        // Administrator reset: the old password, access tokens and refresh tokens stop working.
        var tokens = await TestHost.RequestTokenAsync(_host.CreateClient(), new() { ["grant_type"] = "password", ["username"] = "bob", ["password"] = "bob-password-1" });
        bobClient = await _host.SignInAsync("bob", "bob-password-1");
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync($"/v1.0/users/{bob}/password", new { password = "short" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/users/{bob}/password", new { password = "bob-new-password-2" }, Ct)).StatusCode);
        Assert.False(await CanSignInAsync("bob", "bob-password-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await bobClient.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        using (var refresh = await _host.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        }), Ct))
        {
            Assert.False(refresh.IsSuccessStatusCode);
        }

        // Self-service change: the current password is checked.
        var bobAgain = await _host.SignInAsync("bob", "bob-new-password-2");
        using (var wrong = await bobAgain.PostAsJsonAsync("/v1.0/me/password", new { currentPassword = "nope-nope-nope", newPassword = "bob-third-password-3" }, Ct))
        {
            Assert.True((await wrong.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty("currentPassword", out _));
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await bobAgain.PostAsJsonAsync("/v1.0/me/password", new { currentPassword = "bob-new-password-2", newPassword = "bob-third-password-3" }, Ct)).StatusCode);
        Assert.True(await CanSignInAsync("bob", "bob-third-password-3"));

        // Members cannot manage users; nobody can disable or delete themselves.
        bobAgain = await _host.SignInAsync("bob", "bob-third-password-3");
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(bobAgain, $"/v1.0/users/{bob}", new { displayName = "x" })).StatusCode);
        var me = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id());
        using var self = await PatchAsync(_admin, $"/v1.0/users/{me}", new { isDisabled = true });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Equal("cannotChangeSelf", await CodeAsync(self));
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.DeleteAsync($"/v1.0/users/{me}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Deleted_users_are_anonymized_and_lose_memberships()
    {
        var carol = await UserAsync("carol");
        var group = await GroupAsync("Team");
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = carol }, Ct)).StatusCode);
        var carolClient = await _host.SignInAsync("carol", "carol-password-1");

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/users/{carol}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/v1.0/users/{carol}", Ct)).StatusCode);
        var users = (await (await _admin.GetAsync("/v1.0/users", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray();
        Assert.DoesNotContain(users, u => Guid.Parse(u.Id()) == carol);
        Assert.Empty((await (await _admin.GetAsync($"/v1.0/groups/{group}/members", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
        Assert.NotEqual(HttpStatusCode.OK, (await carolClient.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/v1.0/users/{carol}", Ct)).StatusCode);

        // The user name is free again.
        await UserAsync("carol", "carol-password-2");
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_removed()
    {
        var administrator = await AdministratorRoleAsync();
        var me = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id());
        var assignments = (await (await _admin.GetAsync($"/v1.0/roles/{administrator}/assignments", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToList();
        var mine = assignments.Single(a => a.GetProperty("principalId").GetGuid() == me).Id();

        using var alone = await _admin.DeleteAsync($"/v1.0/roles/{administrator}/assignments/{mine}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, alone.StatusCode);
        Assert.Equal("lastAdministrator", await CodeAsync(alone));

        // Administrators through a group: dan holds the role once the admin gives it up.
        var dan = await UserAsync("dan");
        var group = await GroupAsync("Admins");
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = dan }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await _admin.PostAsJsonAsync($"/v1.0/roles/{administrator}/assignments", new { principalId = group, principalType = "group" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/roles/{administrator}/assignments/{mine}", Ct)).StatusCode);

        var danClient = await _host.SignInAsync("dan", "dan-password-1");
        Assert.Equal(HttpStatusCode.Conflict, (await danClient.DeleteAsync($"/v1.0/groups/{group}/members/{dan}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await danClient.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await danClient.DeleteAsync($"/v1.0/roles/{administrator}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await danClient.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.GetAsync("/v1.0/roles", Ct)).StatusCode);
    }

    [Fact]
    public async Task Groups_and_roles_are_renamed_changed_and_deleted()
    {
        var erin = await UserAsync("erin");
        var erinClient = await _host.SignInAsync("erin", "erin-password-1");

        // Groups: rename with an ETag, name conflicts, delete.
        var group = await GroupAsync("Sales");
        await GroupAsync("Marketing");
        Assert.Equal(HttpStatusCode.Conflict, (await PatchAsync(_admin, $"/v1.0/groups/{group}", new { name = "Marketing" })).StatusCode);
        using (var renamed = await PatchAsync(_admin, $"/v1.0/groups/{group}", new { name = "Sales & Support", description = "Customers" }))
        {
            Assert.Equal("Sales & Support", (await renamed.JsonAsync(HttpStatusCode.OK)).GetProperty("name").GetString());
        }

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PatchAsync(_admin, $"/v1.0/groups/{group}", new { name = "Old" }, "\"0\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);

        // Roles: built-in roles keep their names; custom roles change and their scopes apply at once.
        var administrator = await AdministratorRoleAsync();
        Assert.Equal("builtInRole", await CodeAsync(await PatchAsync(_admin, $"/v1.0/roles/{administrator}", new { name = "Boss" })));
        Assert.Equal("builtInRole", await CodeAsync(await PatchAsync(_admin, $"/v1.0/roles/{administrator}", new { scopes = new[] { "user.read" } })));
        var role = await RoleAsync("Auditor", "role.read");
        using var assigned = await _admin.PostAsJsonAsync($"/v1.0/roles/{role}/assignments", new { principalId = erin, principalType = "user" }, Ct);
        var assignment = (await assigned.JsonAsync(HttpStatusCode.Created)).Id();
        Assert.Equal(HttpStatusCode.OK, (await erinClient.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(_admin, $"/v1.0/roles/{role}", new { scopes = new[] { "no.such" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, $"/v1.0/roles/{role}", new { name = "Reviewer", scopes = new[] { "group.read" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await erinClient.GetAsync("/v1.0/roles", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, $"/v1.0/roles/{role}", new { scopes = new[] { "role.read" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/roles/{role}/assignments/{assignment}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await erinClient.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/roles/{role}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.DeleteAsync($"/v1.0/roles/{administrator}", Ct)).StatusCode);
        Assert.Contains((await (await erinClient.GetAsync("/v1.0/scopes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray(), s => s.GetProperty("name").GetString() == "list.read");
    }

    [Fact]
    public async Task Roles_of_a_group_reach_the_groups_inside_it()
    {
        var ops = await UserAsync("ops");
        var managers = await GroupAsync("Role managers");
        var team = await GroupAsync("Ops team");
        var interns = await GroupAsync("Interns");
        await _admin.PostAsJsonAsync($"/v1.0/groups/{interns}/members", new { userId = ops }, Ct);
        var role = await RoleAsync("Role readers", "role.read");
        await _admin.PostAsJsonAsync($"/v1.0/roles/{role}/assignments", new { principalId = managers, principalType = "group" }, Ct);

        var client = await _host.SignInAsync("ops", "ops-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1.0/roles", Ct)).StatusCode);

        // Interns → Ops team → Role managers: the role reaches ops two levels down, at once.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/groups/{team}/groups", new { groupId = interns }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/groups/{managers}/groups", new { groupId = team }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Equal("Ops team", Assert.Single((await (await _admin.GetAsync($"/v1.0/groups/{managers}/groups", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()).GetProperty("name").GetString());

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
            var tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
            Assert.Contains(ops, await directory.GetGroupMembersAsync(tenant, managers, Ct));
            Assert.Equal(new[] { managers, team, interns }.Order(), (await directory.GetGroupIdsAsync(tenant, ops, Ct)).Order());
        }

        // Taking the group out, or deleting it, ends the access.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/groups/{managers}/groups/{team}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1.0/roles", Ct)).StatusCode);
        await _admin.PostAsJsonAsync($"/v1.0/groups/{managers}/groups", new { groupId = team }, Ct);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/groups/{team}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Empty((await (await _admin.GetAsync($"/v1.0/groups/{managers}/groups", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task Nesting_rejects_cycles_and_more_than_ten_levels()
    {
        var chain = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            chain.Add(await GroupAsync($"Level {i}"));
        }

        Task<HttpResponseMessage> NestAsync(Guid group, Guid member) => _admin.PostAsJsonAsync($"/v1.0/groups/{group}/groups", new { groupId = member }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, (await NestAsync(chain[0], chain[0])).StatusCode);
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await NestAsync(chain[i], chain[i + 1])).StatusCode);
        }

        // Level 0 … Level 9 is ten levels; an eleventh is refused, and so is closing the chain into a cycle.
        Assert.Equal(HttpStatusCode.Conflict, (await NestAsync(chain[9], chain[10])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await NestAsync(chain[5], chain[2])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await NestAsync(chain[0], Guid.NewGuid())).StatusCode);

        // Other tenants cannot see or change the groups.
        var other = await _host.CreateTenantAsync("nest-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/groups/{chain[0]}/groups", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/groups/{chain[0]}/groups/{chain[1]}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/v1.0/groups/{chain[0]}/members", new { userId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Empty((await (await other.GetAsync("/v1.0/groups", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
        Assert.Equal(2, (await (await other.GetAsync("/v1.0/roles", Ct)).JsonAsync(HttpStatusCode.OK)).GetArrayLength());
    }

    [Fact]
    public async Task Preferences_inherit_organization_defaults()
    {
        await UserAsync("fay");
        var fay = await _host.SignInAsync("fay", "fay-password-1");

        var initial = await (await fay.GetAsync("/v1.0/me/preferences", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("UTC", initial.GetProperty("timeZone").GetString());
        Assert.Equal("eng", initial.GetProperty("documentLanguages").GetString());
        Assert.Equal(7, initial.GetProperty("inherited").GetArrayLength());

        // Organization defaults: only with organization.manage.
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(fay, "/v1.0/organization/preferences", new { timeZone = "Europe/Berlin" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, "/v1.0/organization/preferences", new { timeZone = "Europe/Berlin", documentLanguages = "deu+eng", language = "de-DE" })).StatusCode);
        Assert.Equal("Europe/Berlin", (await (await fay.GetAsync("/v1.0/organization/preferences", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("timeZone").GetString());
        var inherited = await (await fay.GetAsync("/v1.0/me/preferences", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Europe/Berlin", inherited.GetProperty("timeZone").GetString());
        Assert.Equal("de-DE", inherited.GetProperty("language").GetString());

        // Own values: validated, returned as not inherited, and null goes back to the default.
        using (var invalid = await PatchAsync(fay, "/v1.0/me/preferences", new { timeZone = "Mars/Olympus", theme = "neon", dateFormat = "hh:mm", colour = "red", language = "not a culture" }))
        {
            var errors = (await invalid.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("errors");
            Assert.All(new[] { "timeZone", "theme", "dateFormat", "colour", "language" }, name => Assert.True(errors.TryGetProperty(name, out _), name));
        }

        using var own = await PatchAsync(fay, "/v1.0/me/preferences", new { timeZone = "America/New_York", theme = "dark", dateFormat = "MM/dd/yyyy", timeFormat = "12h" });
        var body = await own.JsonAsync(HttpStatusCode.OK);
        var etag = own.Headers.ETag!.Tag;
        Assert.Equal("America/New_York", body.GetProperty("timeZone").GetString());
        Assert.DoesNotContain("timeZone", body.GetProperty("inherited").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PatchAsync(fay, "/v1.0/me/preferences", new { theme = "light" }, "\"0\"")).StatusCode);
        using var reset = await PatchAsync(fay, "/v1.0/me/preferences", JsonElement.Parse("""{ "timeZone": null }"""), etag);
        var afterReset = await reset.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Europe/Berlin", afterReset.GetProperty("timeZone").GetString());
        Assert.Equal("dark", afterReset.GetProperty("theme").GetString());

        await using var scope = _host.Services.CreateAsyncScope();
        var me = await (await fay.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK);
        var values = await scope.ServiceProvider.GetRequiredService<IUserPreferences>()
            .GetAsync(Guid.Parse(me.GetProperty("tenantId").GetString()!), Guid.Parse(me.Id()), Ct);
        Assert.Equal("Europe/Berlin", values.TimeZone);
        Assert.Equal("deu+eng", values.DocumentLanguages);
    }

    [Fact]
    public async Task Accounts_and_preferences_are_isolated_per_tenant()
    {
        var other = await _host.CreateTenantAsync("iso-b");
        var user = await UserAsync("gus");
        var group = await GroupAsync("A team");
        var role = await RoleAsync("A role", "user.read");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, "/v1.0/organization/preferences", new { timeZone = "Asia/Tokyo" })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/users/{user}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(other, $"/v1.0/users/{user}", new { isDisabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/users/{user}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/v1.0/users/{user}/password", new { password = "hijacked-password-1" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(other, $"/v1.0/groups/{group}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/groups/{group}/members", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(other, $"/v1.0/roles/{role}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/roles/{role}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/v1.0/roles/{role}/assignments", new { principalId = user, principalType = "user" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/roles/{role}/assignments", Ct)).StatusCode);
        Assert.Equal("UTC", (await (await other.GetAsync("/v1.0/organization/preferences", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("timeZone").GetString());
        Assert.Equal("UTC", (await (await other.GetAsync("/v1.0/me/preferences", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("timeZone").GetString());
        Assert.Equal("Asia/Tokyo", (await (await _admin.GetAsync("/v1.0/me/preferences", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("timeZone").GetString());
        Assert.Equal("iso-b", (await (await other.GetAsync("/v1.0/organization", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("identifier").GetString());
    }
}
