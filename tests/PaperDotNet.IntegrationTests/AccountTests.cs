using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Account lifecycle (IAM-14), user preferences (PLT-17) and organization defaults (PLT-18).</summary>
public sealed class AccountTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> CreateUserAsync(HttpClient admin, string userName, string password)
    {
        var response = await admin.PostAsJsonAsync("/v1.0/users", new { userName, password }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AdministratorRoleAsync(HttpClient admin) =>
        (await (await admin.GetAsync("/v1.0/roles", Ct)).ReadJsonAsync()).EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "Administrator").GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, object body, string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;

    [Fact]
    public async Task Users_change_their_own_display_name_only_in_their_tenant()
    {
        await factory.CreateTenantAsync("accounts-profile");
        await factory.CreateTenantAsync("accounts-profile-other");
        var admin = await ApiClient.CreateAsync(factory, "accounts-profile");
        await CreateUserAsync(admin, "carol", "carol-password-1");
        var carol = await ApiClient.CreateAsync(factory, "accounts-profile", "carol", "carol-password-1");

        var updated = await PatchAsync(carol, "/v1.0/me", new { displayName = "  Carol C.  " });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Carol C.", (await updated.ReadJsonAsync()).GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(carol, "/v1.0/me", new { displayName = new string('x', 201) })).StatusCode);

        // An empty name falls back to the user name; the email is not changed by the user.
        var reset = await (await PatchAsync(carol, "/v1.0/me", new { displayName = "", email = "x@example.com" })).ReadJsonAsync();
        Assert.Equal("carol", reset.GetProperty("displayName").GetString());
        Assert.False(reset.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String);

        // Only the caller changes: the administrator, and the same user name in another tenant, keep their names.
        Assert.Equal("admin", (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("userName").GetString());
        Assert.NotEqual("Carol C.", (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("displayName").GetString());
        var otherAdmin = await ApiClient.CreateAsync(factory, "accounts-profile-other");
        await CreateUserAsync(otherAdmin, "carol", "carol-password-1");
        var otherCarol = await ApiClient.CreateAsync(factory, "accounts-profile-other", "carol", "carol-password-1");
        await PatchAsync(carol, "/v1.0/me", new { displayName = "Carol C." });
        Assert.Equal("carol", (await (await otherCarol.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Users_are_updated_disabled_and_their_passwords_reset_or_changed()
    {
        await factory.CreateTenantAsync("accounts-users");
        var admin = await ApiClient.CreateAsync(factory, "accounts-users");
        var bob = await CreateUserAsync(admin, "bob", "bob-password-1");

        var updated = await PatchAsync(admin, $"/v1.0/users/{bob}", new { displayName = "Bob Builder", email = "bob@example.com" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Bob Builder", (await updated.ReadJsonAsync()).GetProperty("displayName").GetString());

        // Disabled: tokens stop working and the password grant fails; enabled again, sign-in works.
        var bobClient = await ApiClient.CreateAsync(factory, "accounts-users", "bob", "bob-password-1");
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, $"/v1.0/users/{bob}", new { isDisabled = true })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await bobClient.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        var anonymous = await ApiClient.CreateAsync(factory, "accounts-users", userName: null);
        Assert.False((await ApiClient.RequestTokenAsync(anonymous, "bob", "bob-password-1")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, $"/v1.0/users/{bob}", new { isDisabled = false })).StatusCode);
        Assert.True((await ApiClient.RequestTokenAsync(anonymous, "bob", "bob-password-1")).IsSuccessStatusCode);

        // Admin reset: the old password and refresh tokens stop working.
        var tokens = await (await ApiClient.RequestTokenAsync(anonymous, "bob", "bob-password-1")).ReadJsonAsync();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/v1.0/users/{bob}/password", new { password = "short" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/v1.0/users/{bob}/password", new { password = "bob-new-password-2" }, Ct)).StatusCode);
        Assert.False((await ApiClient.RequestTokenAsync(anonymous, "bob", "bob-password-1")).IsSuccessStatusCode);
        var refresh = await anonymous.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "paperdotnet",
            ["refresh_token"] = refreshToken,
        }), Ct);
        Assert.False(refresh.IsSuccessStatusCode);

        // Self-service change: the current password is checked.
        var bobAgain = await ApiClient.CreateAsync(factory, "accounts-users", "bob", "bob-new-password-2");
        var wrong = await bobAgain.PostAsJsonAsync("/v1.0/me/password", new { currentPassword = "nope-nope-nope", newPassword = "bob-third-password-3" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True((await wrong.ReadJsonAsync()).GetProperty("errors").TryGetProperty("currentPassword", out _));
        Assert.Equal(HttpStatusCode.NoContent,
            (await bobAgain.PostAsJsonAsync("/v1.0/me/password", new { currentPassword = "bob-new-password-2", newPassword = "bob-third-password-3" }, Ct)).StatusCode);
        Assert.True((await ApiClient.RequestTokenAsync(anonymous, "bob", "bob-third-password-3")).IsSuccessStatusCode);

        // Members cannot manage users; nobody can disable or delete themselves.
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(bobAgain, $"/v1.0/users/{bob}", new { displayName = "x" })).StatusCode);
        var me = (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var self = await PatchAsync(admin, $"/v1.0/users/{me}", new { isDisabled = true });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Equal("cannotChangeSelf", await CodeAsync(self));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/v1.0/users/{me}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Deleted_users_are_anonymized_and_lose_memberships_and_grants()
    {
        await factory.CreateTenantAsync("accounts-delete");
        var admin = await ApiClient.CreateAsync(factory, "accounts-delete");
        var carol = await CreateUserAsync(admin, "carol", "carol-password-1");
        var group = (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = carol }, Ct)).StatusCode);
        var ws = await admin.CreateWorkspaceAsync("Deletions");
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = carol }, Ct)).IsSuccessStatusCode);
        var list = await admin.CreateListAsync(ws, "Docs");
        var listUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{listUrl}/permissions/breakInheritance", new { copyGrants = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{listUrl}/permissions/grants",
            new { grants = new object[] { new { principalType = "user", principalId = carol, level = "read" } } }, Ct)).StatusCode);
        var carolClient = await ApiClient.CreateAsync(factory, "accounts-delete", "carol", "carol-password-1");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/users/{carol}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1.0/users/{carol}", Ct)).StatusCode);
        var users = (await (await admin.GetAsync("/v1.0/users", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray();
        Assert.DoesNotContain(users, u => u.GetProperty("id").GetGuid() == carol);
        var groupMembers = await admin.GetAsync($"/v1.0/groups/{group}/members", Ct);
        Assert.True(groupMembers.IsSuccessStatusCode, await groupMembers.Content.ReadAsStringAsync(Ct));
        Assert.Empty((await groupMembers.ReadJsonAsync()).EnumerateArray());
        Assert.NotEqual(HttpStatusCode.OK, (await carolClient.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/v1.0/users/{carol}", Ct)).StatusCode);

        // The user name is free again; grants and workspace memberships go away in the background.
        await CreateUserAsync(admin, "carol", "carol-password-2");
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var grants = (await (await admin.GetAsync($"{listUrl}/permissions", Ct)).ReadJsonAsync()).GetProperty("grants").EnumerateArray();
            var members = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/members", Ct)).ReadJsonAsync()).EnumerateArray();
            return grants.All(g => g.GetProperty("principalId").GetGuid() != carol)
                && members.All(m => m.GetProperty("userId").GetGuid() != carol) ? true : null;
        });
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_removed()
    {
        await factory.CreateTenantAsync("accounts-admins");
        var admin = await ApiClient.CreateAsync(factory, "accounts-admins");
        var administrator = await AdministratorRoleAsync(admin);
        var me = (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var assignments = (await (await admin.GetAsync($"/v1.0/roles/{administrator}/assignments", Ct)).ReadJsonAsync()).EnumerateArray().ToList();
        var mine = assignments.Single(a => a.GetProperty("principalId").GetGuid() == me).GetProperty("id").GetGuid();

        var alone = await admin.DeleteAsync($"/v1.0/roles/{administrator}/assignments/{mine}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, alone.StatusCode);
        Assert.Equal("lastAdministrator", await CodeAsync(alone));

        // Administrators through a group: dan holds the role once the admin gives it up.
        var dan = await CreateUserAsync(admin, "dan", "dan-password-1");
        var group = (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Admins" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = dan }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync($"/v1.0/roles/{administrator}/assignments", new { principalId = group, principalType = "group" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/roles/{administrator}/assignments/{mine}", Ct)).StatusCode);

        var danClient = await ApiClient.CreateAsync(factory, "accounts-admins", "dan", "dan-password-1");
        Assert.Equal(HttpStatusCode.Conflict, (await danClient.DeleteAsync($"/v1.0/groups/{group}/members/{dan}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await danClient.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await danClient.DeleteAsync($"/v1.0/roles/{administrator}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await danClient.GetAsync("/v1.0/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/v1.0/roles", Ct)).StatusCode);
    }

    [Fact]
    public async Task Groups_and_roles_are_renamed_changed_and_deleted()
    {
        await factory.CreateTenantAsync("accounts-roles");
        var admin = await ApiClient.CreateAsync(factory, "accounts-roles");
        var erin = await CreateUserAsync(admin, "erin", "erin-password-1");
        var erinClient = await ApiClient.CreateAsync(factory, "accounts-roles", "erin", "erin-password-1");

        // Groups: rename with an ETag, name conflicts, delete (grants go in the background).
        var group = (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Sales" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Marketing" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, (await PatchAsync(admin, $"/v1.0/groups/{group}", new { name = "Marketing" })).StatusCode);
        var renamed = await PatchAsync(admin, $"/v1.0/groups/{group}", new { name = "Sales & Support", description = "Customers" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Sales & Support", (await renamed.ReadJsonAsync()).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PatchAsync(admin, $"/v1.0/groups/{group}", new { name = "Old" }, "\"0\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);

        // Roles: built-in roles keep their names; custom roles change and their scopes apply at once.
        var administrator = await AdministratorRoleAsync(admin);
        Assert.Equal("builtInRole", await CodeAsync(await PatchAsync(admin, $"/v1.0/roles/{administrator}", new { name = "Boss" })));
        Assert.Equal("builtInRole", await CodeAsync(await PatchAsync(admin, $"/v1.0/roles/{administrator}", new { scopes = new[] { "user.read" } })));
        var role = (await (await admin.PostAsJsonAsync("/v1.0/roles", new { name = "Auditor", scopes = new[] { "role.read" } }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var assigned = await admin.PostAsJsonAsync($"/v1.0/roles/{role}/assignments", new { principalId = erin, principalType = "user" }, Ct);
        var assignment = (await assigned.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await erinClient.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(admin, $"/v1.0/roles/{role}", new { scopes = new[] { "no.such" } })).StatusCode);
        var changed = await PatchAsync(admin, $"/v1.0/roles/{role}", new { name = "Reviewer", scopes = new[] { "group.read" } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await erinClient.GetAsync("/v1.0/roles", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(admin, $"/v1.0/roles/{role}", new { scopes = new[] { "role.read" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/roles/{role}/assignments/{assignment}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await erinClient.GetAsync("/v1.0/roles", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/roles/{role}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/v1.0/roles/{administrator}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Preferences_inherit_organization_defaults_and_drive_ocr_languages()
    {
        await factory.CreateTenantAsync("accounts-prefs");
        var admin = await ApiClient.CreateAsync(factory, "accounts-prefs");
        await CreateUserAsync(admin, "fay", "fay-password-1");
        var fay = await ApiClient.CreateAsync(factory, "accounts-prefs", "fay", "fay-password-1");

        var initial = await (await fay.GetAsync("/v1.0/me/preferences", Ct)).ReadJsonAsync();
        Assert.Equal("UTC", initial.GetProperty("timeZone").GetString());
        Assert.Equal("eng", initial.GetProperty("documentLanguages").GetString());
        Assert.Equal(7, initial.GetProperty("inherited").GetArrayLength());

        // Organization defaults: only with organization.manage.
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(fay, "/v1.0/organization/preferences", new { timeZone = "Europe/Berlin" })).StatusCode);
        var defaults = await PatchAsync(admin, "/v1.0/organization/preferences", new { timeZone = "Europe/Berlin", documentLanguages = "deu+eng", language = "de-DE" });
        Assert.Equal(HttpStatusCode.OK, defaults.StatusCode);
        Assert.Equal("Europe/Berlin", (await (await fay.GetAsync("/v1.0/organization/preferences", Ct)).ReadJsonAsync()).GetProperty("timeZone").GetString());

        var inherited = await (await fay.GetAsync("/v1.0/me/preferences", Ct)).ReadJsonAsync();
        Assert.Equal("Europe/Berlin", inherited.GetProperty("timeZone").GetString());
        Assert.Equal("de-DE", inherited.GetProperty("language").GetString());

        // Own values: validated, returned as not inherited, and null goes back to the default.
        var invalid = await PatchAsync(fay, "/v1.0/me/preferences", new { timeZone = "Mars/Olympus", theme = "neon", dateFormat = "hh:mm", colour = "red" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.ReadJsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("timeZone", out _) && errors.TryGetProperty("theme", out _) && errors.TryGetProperty("dateFormat", out _) && errors.TryGetProperty("colour", out _));

        var own = await PatchAsync(fay, "/v1.0/me/preferences", new { timeZone = "America/New_York", theme = "dark", dateFormat = "MM/dd/yyyy", timeFormat = "12h" });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var etag = own.Headers.ETag!.Tag;
        var body = await own.ReadJsonAsync();
        Assert.Equal("America/New_York", body.GetProperty("timeZone").GetString());
        Assert.DoesNotContain("timeZone", body.GetProperty("inherited").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PatchAsync(fay, "/v1.0/me/preferences", new { theme = "light" }, "\"0\"")).StatusCode);
        var reset = await PatchAsync(fay, "/v1.0/me/preferences", JsonDocument.Parse("""{ "timeZone": null }""").RootElement, etag);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var afterReset = await reset.ReadJsonAsync();
        Assert.Equal("Europe/Berlin", afterReset.GetProperty("timeZone").GetString());
        Assert.Equal("dark", afterReset.GetProperty("theme").GetString());

        // Libraries without their own OCR languages use the organization's document languages.
        var ws = await admin.CreateWorkspaceAsync("Scans");
        var library = (await (await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Inbox scans", templateKey = "documents" }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var settingsUrl = $"/v1.0/workspaces/{ws}/lists/{library}/documentSettings";
        var settings = await (await admin.GetAsync(settingsUrl, Ct)).ReadJsonAsync();
        Assert.Equal("deu+eng", settings.GetProperty("ocrLanguages").GetString());
        Assert.True(settings.GetProperty("ocrLanguagesInherited").GetBoolean());
        var own2 = await (await admin.PutAsJsonAsync(settingsUrl, new { ocrLanguages = "fra" }, Ct)).ReadJsonAsync();
        Assert.Equal("fra", own2.GetProperty("ocrLanguages").GetString());
        Assert.False(own2.GetProperty("ocrLanguagesInherited").GetBoolean());
        var back = await (await admin.PutAsJsonAsync(settingsUrl, new { ocrLanguages = "" }, Ct)).ReadJsonAsync();
        Assert.True(back.GetProperty("ocrLanguagesInherited").GetBoolean());
    }

    [Fact]
    public async Task Accounts_and_preferences_are_isolated_per_tenant()
    {
        await factory.CreateTenantAsync("accounts-iso-a");
        await factory.CreateTenantAsync("accounts-iso-b");
        var a = await ApiClient.CreateAsync(factory, "accounts-iso-a");
        var b = await ApiClient.CreateAsync(factory, "accounts-iso-b");
        var userA = await CreateUserAsync(a, "gus", "gus-password-1");
        var groupA = (await (await a.PostAsJsonAsync("/v1.0/groups", new { name = "A team" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var roleA = (await (await a.PostAsJsonAsync("/v1.0/roles", new { name = "A role", scopes = new[] { "user.read" } }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(a, "/v1.0/organization/preferences", new { timeZone = "Asia/Tokyo" })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(b, $"/v1.0/users/{userA}", new { isDisabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/v1.0/users/{userA}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/v1.0/users/{userA}/password", new { password = "hijacked-password-1" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(b, $"/v1.0/groups/{groupA}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/v1.0/groups/{groupA}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(b, $"/v1.0/roles/{roleA}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/v1.0/roles/{roleA}", Ct)).StatusCode);
        Assert.Equal("UTC", (await (await b.GetAsync("/v1.0/organization/preferences", Ct)).ReadJsonAsync()).GetProperty("timeZone").GetString());
        Assert.Equal("UTC", (await (await b.GetAsync("/v1.0/me/preferences", Ct)).ReadJsonAsync()).GetProperty("timeZone").GetString());
        Assert.Equal("Asia/Tokyo", (await (await a.GetAsync("/v1.0/me/preferences", Ct)).ReadJsonAsync()).GetProperty("timeZone").GetString());
    }
}
