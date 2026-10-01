using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.IntegrationTests.Extension;

namespace PaperDotNet.IntegrationTests;

/// <summary>The extension runtime (EXT-01…04, IAM-13, EVT-03) with the test extension <c>tests.tickets</c>.</summary>
public sealed class ExtensionTests : IAsyncLifetime
{
    private const string Id = TicketsExtension.Id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Recurring jobs are checked often, so the test job runs within the test.
    private readonly TestHost _host = new(settings: new Dictionary<string, string> { ["Jobs:SchedulerInterval"] = "00:00:00.200" });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static Task<HttpResponseMessage> EnableAsync(HttpClient admin, bool enabled = true) =>
        admin.PostAsync($"/v1.0/extensions/{Id}/{(enabled ? "enable" : "disable")}", null, Ct);

    private static async Task<Guid> TenantOfAsync(HttpClient client) =>
        Guid.Parse((await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);

    /// <summary>A list from the extension's template (its content type is provisioned when the extension is enabled).</summary>
    private static async Task<(string Workspace, string List)> TicketListAsync(HttpClient admin)
    {
        var workspace = await Api.CreateWorkspaceAsync(admin, "Support");
        using var response = await admin.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists", new { name = "Tickets", templateKey = $"{Id}.tickets" }, Ct);
        return (workspace, (await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var value = await read();
        for (var attempt = 0; attempt < 100 && !done(value); attempt++)
        {
            await Task.Delay(100, Ct);
            value = await read();
        }

        return value;
    }

    [Fact]
    public async Task Installed_extensions_are_listed_and_disabled_by_default()
    {
        var admin = await _host.SignInAsync();
        var list = await (await admin.GetAsync("/v1.0/extensions", Ct)).JsonAsync(HttpStatusCode.OK);
        var tickets = Assert.Single(list.EnumerateArray(), e => e.GetProperty("id").GetString() == Id);

        Assert.False(tickets.GetProperty("enabled").GetBoolean());
        Assert.Equal("1.0.0", tickets.GetProperty("version").GetString());
        var contributions = tickets.GetProperty("contributions");
        Assert.Contains($"{Id}.code", contributions.GetProperty("fieldTypes").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains($"{Id}.tick", contributions.GetProperty("jobs").EnumerateArray().Select(t => t.GetString()));
        Assert.True(contributions.GetProperty("endpoints").GetBoolean());
        Assert.Equal("low", tickets.GetProperty("settings")[1].GetProperty("default").GetString());

        // Extension scopes are part of the catalog (IAM-13).
        var scopes = (await (await admin.GetAsync("/v1.0/scopes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains($"{Id}.admin", scopes);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/v1.0/extensions/unknown.extension", Ct)).StatusCode);
    }

    [Fact]
    public async Task Contributions_are_gated_by_tenant_enablement()
    {
        var admin = await _host.SignInAsync();
        var tenant = await TenantOfAsync(admin);

        // Disabled: no endpoints, no field type, no template, no job.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
        using (var type = await admin.PostAsJsonAsync("/v1.0/contentTypes", new { name = "Mine", fields = new[] { new { name = "code", type = $"{Id}.code" } } }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, type.StatusCode);
        }

        Assert.DoesNotContain($"{Id}.tickets", (await (await admin.GetAsync("/v1.0/listTemplates", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Select(t => t.GetProperty("key").GetString()));
        await Task.Delay(1500, Ct);
        Assert.False(TicketJob.Runs.ContainsKey(tenant));

        // Enabled: the template, field type, mutator, subscriber, endpoint and job work.
        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(admin)).StatusCode);
        var (workspace, list) = await TicketListAsync(admin);
        var ticket = await Api.CreateItemAsync(admin, workspace, list, new { title = "Printer", code = "ab12" });
        Assert.Equal("T-Printer", ticket.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("AB12", ticket.GetProperty("fields").GetProperty("code").GetString());
        using (var invalid = await admin.PostAsJsonAsync(Api.Items(workspace, list), new { fields = new { title = "Bad", code = "a-b" } }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        var stats = await EventuallyAsync(
            async () => (await (await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("added").GetInt32(),
            added => added >= 1);
        Assert.Equal(1, stats);
        Assert.True(await EventuallyAsync(() => Task.FromResult(TicketJob.Runs.ContainsKey(tenant)), ran => ran));

        // Disabled again: the mutator and the endpoint stop; data stays.
        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(admin, enabled: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
        Assert.Equal("Second", (await Api.CreateItemAsync(admin, workspace, list, new { title = "Second" })).GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Api.Items(workspace, list)}/{ticket.Id()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Settings_are_validated_and_change_behavior()
    {
        var admin = await _host.SignInAsync();
        await EnableAsync(admin);
        var settings = $"/v1.0/extensions/{Id}/settings";

        var defaults = await (await admin.GetAsync(settings, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("T-", defaults.GetProperty("prefix").GetString());
        Assert.Equal("low", defaults.GetProperty("priority").GetString());

        using (var invalid = await admin.PutAsJsonAsync(settings, new { prefix = 5, priority = "urgent", unknown = 1 }, Ct))
        {
            var errors = (await invalid.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("errors");
            Assert.True(errors.TryGetProperty("prefix", out _));
            Assert.True(errors.TryGetProperty("priority", out _));
            Assert.True(errors.TryGetProperty("unknown", out _));
        }

        using (var saved = await admin.PutAsJsonAsync(settings, new { prefix = "SUP-", limit = 3 }, Ct))
        {
            var effective = await saved.JsonAsync(HttpStatusCode.OK);
            Assert.Equal("SUP-", effective.GetProperty("prefix").GetString());
            Assert.Equal("low", effective.GetProperty("priority").GetString());
        }

        var (workspace, list) = await TicketListAsync(admin);
        Assert.Equal("SUP-Screen", (await Api.CreateItemAsync(admin, workspace, list, new { title = "Screen" })).GetProperty("fields").GetProperty("title").GetString());

        // The mutator runs for its content type only.
        var plain = (await Api.CreateListAsync(admin, workspace, "Notes", new[] { new { name = "note", type = "text" } })).Id();
        Assert.Equal("Screen", (await Api.CreateItemAsync(admin, workspace, plain, new { title = "Screen" })).GetProperty("fields").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Enabling_grants_member_scopes_and_needs_admin_rights()
    {
        var admin = await _host.SignInAsync();
        using (var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var member = await _host.SignInAsync("member", "member-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await EnableAsync(member)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/v1.0/extensions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/v1.0/extensions/{Id}/settings", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(admin)).StatusCode);
        var scopes = (await (await member.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains($"{Id}.read", scopes);
        Assert.DoesNotContain($"{Id}.admin", scopes);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
    }

    [Fact]
    public async Task Enablement_is_per_tenant_and_content_types_are_managed_by_the_extension()
    {
        var a = await _host.SignInAsync();
        await Api.CreateContentTypeAsync(a, "Ticket", new[] { new { name = "ref", type = "text" } });
        var b = await _host.CreateTenantAsync("other");

        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1.0/ext/{Id}/stats", Ct)).StatusCode);
        var listB = await (await b.GetAsync("/v1.0/extensions", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.False(listB.EnumerateArray().Single(e => e.GetProperty("id").GetString() == Id).GetProperty("enabled").GetBoolean());

        // The tenant's own "Ticket" keeps its name; the managed one cannot be changed.
        var managed = (await (await a.GetAsync("/v1.0/contentTypes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Single(c => c.TryGetProperty("key", out var key) && key.GetString() == $"{Id}.ticket");
        Assert.Equal(Id, managed.GetProperty("extensionId").GetString());
        Assert.Equal("Ticket (2)", managed.GetProperty("name").GetString());
        var url = $"/v1.0/contentTypes/{managed.Id()}";
        var etag = (await a.GetAsync(url, Ct)).Headers.ETag!.Tag;
        using (var change = await a.SendAsync(Api.WithETag(HttpMethod.Put, url, new { name = "Mine", fields = Array.Empty<object>() }, etag), Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        }

        // Enabling again keeps one content type; the other tenant has none.
        await EnableAsync(a);
        Assert.Single((await (await a.GetAsync("/v1.0/contentTypes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray(),
            c => c.TryGetProperty("key", out var key) && key.GetString() == $"{Id}.ticket");
        Assert.DoesNotContain((await (await b.GetAsync("/v1.0/contentTypes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray(),
            c => c.TryGetProperty("key", out var key) && key.GetString() == $"{Id}.ticket");
    }

    [Fact]
    public async Task Extensions_provide_term_sets_when_enabled()
    {
        var a = await _host.SignInAsync();
        var b = await _host.CreateTenantAsync("other-terms");
        static bool IsAreas(JsonElement s) => s.GetProperty("name").GetString() == "Ticket areas";
        static async Task<List<JsonElement>> SetsAsync(HttpClient client) =>
            [.. (await (await client.GetAsync("/v1.0/termStore/sets", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()];
        Assert.DoesNotContain(await SetsAsync(a), IsAreas);

        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(a)).StatusCode);
        var set = Assert.Single(await SetsAsync(a), IsAreas).Id();
        var roots = (await (await a.GetAsync($"/v1.0/termStore/sets/{set}/terms", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(["Hardware", "Software"], roots.Select(t => t.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        var hardware = roots.Single(t => t.GetProperty("name").GetString() == "Hardware").Id();
        var laptops = (await (await a.GetAsync($"/v1.0/termStore/sets/{set}/terms?parentId={hardware}", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Laptops");
        Assert.Equal(["Notebooks"], laptops.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));

        // Enabling again is idempotent; other tenants get nothing.
        await EnableAsync(a, enabled: false);
        await EnableAsync(a);
        Assert.Single(await SetsAsync(a), IsAreas);
        Assert.DoesNotContain(await SetsAsync(b), IsAreas);
    }

    [Fact]
    public async Task Workflow_activities_of_an_extension_run_where_it_is_enabled()
    {
        var admin = await _host.SignInAsync();
        var workspace = await Api.CreateWorkspaceAsync(admin, "Ops");
        var activities = (await (await admin.GetAsync("/v1.0/workflows/activities", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Select(a => a.GetProperty("key").GetString());
        Assert.Contains($"{Id}.echo", activities);

        using var created = await admin.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/workflows", new
        {
            name = "Echo",
            definition = JsonNode.Parse($$"""
                { "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "{{Id}}.echo", "inputs": { "text": "hello" } } } } }
                """),
        }, Ct);
        var workflow = (await created.JsonAsync(HttpStatusCode.Created)).Id();

        async Task<JsonElement> RunAsync()
        {
            using var started = await admin.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/workflows/{workflow}/runs", new { }, Ct);
            var run = (await started.JsonAsync(HttpStatusCode.Accepted)).Id();
            return await EventuallyAsync(
                async () => await (await admin.GetAsync($"/v1.0/workspaces/{workspace}/workflows/runs/{run}", Ct)).JsonAsync(HttpStatusCode.OK),
                r => r.GetProperty("status").GetString() is "completed" or "failed");
        }

        var disabled = await RunAsync();
        Assert.Equal("failed", disabled.GetProperty("status").GetString());
        Assert.Contains("not enabled", disabled.GetProperty("error").GetString(), StringComparison.Ordinal);

        await EnableAsync(admin);
        var enabled = await RunAsync();
        Assert.Equal("completed", enabled.GetProperty("status").GetString());
        Assert.Equal("hello", enabled.GetProperty("outputs").GetProperty("a").GetProperty("echo").GetString());
    }
}
