using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.IntegrationTests;

public sealed class WorkflowDomainInputTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Launch_and_approval_forms_enforce_relationship_and_taxonomy_scopes()
    {
        await factory.CreateTenantAsync("workflow-domain-inputs");
        var client = await ApiClient.CreateAsync(factory, "workflow-domain-inputs");
        async Task<Guid> Create(string url, object body)
        {
            var response = await client.PostAsJsonAsync(url, body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        }
        var workspace = await client.CreateWorkspaceAsync("Inputs");
        var list = await Create($"/v1.0/workspaces/{workspace}/lists", new { name = "Tasks", templateKey = "tasks" });
        var item = await Create($"/v1.0/workspaces/{workspace}/lists/{list}/items", new { fields = new { title = "Target" } });
        var type = await Create("/v1.0/relationshipTypes", new { name = "Depends on", directed = true });
        var group = await Create("/v1.0/termStore/groups", new { name = "Allowed" });
        var set = await Create("/v1.0/termStore/sets", new { groupId = group, name = "Tags" });
        var term = await Create($"/v1.0/termStore/sets/{set}/terms", new { name = "One" });
        var other = await Create($"/v1.0/termStore/sets/{set}/terms", new { name = "Two" });
        var outsideGroup = await Create("/v1.0/termStore/groups", new { name = "Outside" });
        var outsideSet = await Create("/v1.0/termStore/sets", new { groupId = outsideGroup, name = "Tags" });
        var outside = await Create($"/v1.0/termStore/sets/{outsideSet}/terms", new { name = "Outside" });
        var keyword = await Create("/v1.0/termStore/keywords", new { name = "Urgent" });
        var peopleGroup = await Create("/v1.0/groups", new { name = "Reviewers" });
        var reviewer = await Create("/v1.0/users", new { userName = "reviewer", password = "reviewer-password-1" });
        var outsider = await Create("/v1.0/users", new { userName = "outsider", password = "outsider-password-1" });
        var otherPeopleGroup = await Create("/v1.0/groups", new { name = "Other reviewers" });
        await client.PostAsJsonAsync($"/v1.0/groups/{peopleGroup}/members", new { userId = reviewer }, Ct);
        await client.PostAsJsonAsync($"/v1.0/groups/{otherPeopleGroup}/members", new { userId = outsider }, Ct);

        // Members of a group inside the group count; disabled users and service accounts are never people to pick.
        var seniors = await Create("/v1.0/groups", new { name = "Senior reviewers" });
        var senior = await Create("/v1.0/users", new { userName = "senior", password = "senior-password-1" });
        var disabled = await Create("/v1.0/users", new { userName = "disabled", password = "disabled-password-1" });
        await client.PostAsJsonAsync($"/v1.0/groups/{seniors}/members", new { userId = senior }, Ct);
        await client.PostAsJsonAsync($"/v1.0/groups/{peopleGroup}/groups", new { groupId = seniors }, Ct);
        await client.PostAsJsonAsync($"/v1.0/groups/{peopleGroup}/members", new { userId = disabled }, Ct);
        var disabling = new HttpRequestMessage(HttpMethod.Patch, $"/v1.0/users/{disabled}") { Content = JsonContent.Create(new { isDisabled = true }) };
        Assert.True((await client.SendAsync(disabling, Ct)).IsSuccessStatusCode);
        var application = await client.PostAsJsonAsync("/v1.0/applications",
            new { displayName = "Robot", clientType = "confidential", grantTypes = new[] { "client_credentials" }, scopes = new[] { "workspace.read" } }, Ct);
        Assert.True(application.IsSuccessStatusCode, await application.Content.ReadAsStringAsync(Ct));
        var robot = (await application.ReadJsonAsync()).GetProperty("application").GetProperty("serviceUserId").GetGuid();
        await client.PostAsJsonAsync($"/v1.0/groups/{peopleGroup}/members", new { userId = robot }, Ct);
        var schema = JsonNode.Parse($$$"""
            {"type":"object","properties":{
              "targets":{"type":"array","items":{"type":"string"},"minItems":1,"uniqueItems":true,"x-paperdotnet":{"kind":"relationship","relationshipType":"{{{type}}}"}},
              "tag":{"type":"string","x-paperdotnet":{"kind":"terms","groupId":"{{{group}}}"}},
              "tags":{"type":"array","items":{"type":"string"},"x-paperdotnet":{"kind":"terms","termSetId":"{{{set}}}","termIds":["{{{term}}}"]}},
              "keyword":{"type":"string","x-paperdotnet":{"kind":"keywords","termIds":["{{{keyword}}}"]}},
              "reviewers":{"type":"array","items":{"type":"string"},"x-paperdotnet":{"kind":"people","memberOf":"{{{peopleGroup}}}","people":true,"groups":false}},
              "anyone":{"type":"string","x-paperdotnet":{"kind":"people"}},
              "reviewGroup":{"type":"string","x-paperdotnet":{"kind":"people","people":false,"groups":true}}
            },"required":["targets","tag","tags","keyword","reviewers","reviewGroup","anyone"]}
            """)!.AsObject();
        var root = $"/v1.0/workspaces/{workspace}/workflows";
        var workflow = await Create(root, new
        {
            name = "Domain forms",
            scope = "workspace",
            trigger = new { type = "manual" },
            inputSchema = schema,
            steps = new[] { new { type = "approval", name = "Review", assignees = new[] { "admin" }, inputSchema = schema } },
        });
        JsonObject Valid() => new()
        {
            ["targets"] = new JsonArray(item.ToString()),
            ["tag"] = term.ToString(),
            ["tags"] = new JsonArray(term.ToString()),
            ["keyword"] = keyword.ToString(),
            ["reviewers"] = new JsonArray(reviewer.ToString(), senior.ToString()),
            ["reviewGroup"] = peopleGroup.ToString(),
            ["anyone"] = outsider.ToString(),
        };
        var invalid = new List<JsonObject>();
        foreach (var (name, value) in new (string, JsonNode)[]
        {
            ("targets", new JsonArray()), ("targets", new JsonArray(item.ToString(), item.ToString())),
            ("targets", new JsonArray(item.ToString(), item.ToString("N"))),
            ("targets", new JsonArray(Guid.NewGuid().ToString())), ("tag", JsonValue.Create(outside.ToString())),
            ("tag", JsonValue.Create("")), ("tags", new JsonArray()), ("tags", new JsonArray(other.ToString())),
            ("tags", new JsonArray(outside.ToString())), ("keyword", JsonValue.Create(term.ToString())),
            ("reviewers", new JsonArray(outsider.ToString())), ("reviewers", new JsonArray(peopleGroup.ToString())),
            ("reviewGroup", JsonValue.Create(reviewer.ToString())), ("reviewers", new JsonArray(disabled.ToString())),
            ("reviewers", new JsonArray(robot.ToString())), ("anyone", JsonValue.Create(robot.ToString())),
            ("anyone", JsonValue.Create(disabled.ToString())), ("anyone", JsonValue.Create(Guid.NewGuid().ToString())),
        })
        {
            var input = Valid();
            input[name] = value;
            invalid.Add(input);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { inputs = input }, Ct)).StatusCode);
        }
        var launched = await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { inputs = Valid() }, Ct);
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        var runId = (await launched.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        var approval = await Eventually.WaitForAsync(async () =>
        {
            var page = await (await client.GetAsync("/v1.0/me/approvals", Ct)).ReadJsonAsync();
            return page.GetProperty("value").EnumerateArray().Select(row => (JsonElement?)row).FirstOrDefault();
        }, TimeSpan.FromSeconds(30));
        var decision = $"/v1.0/me/approvals/{approval.GetProperty("id").GetGuid()}/decision";
        foreach (var inputs in invalid)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(decision, new { outcome = "approved", inputs }, Ct)).StatusCode);
        }
        var decided = await client.PostAsJsonAsync(decision, new { outcome = "approved", inputs = Valid() }, Ct);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal(item, (await decided.ReadJsonAsync()).GetProperty("inputs").GetProperty("targets")[0].GetGuid());
        var run = await Eventually.WaitForAsync(async () =>
        {
            var value = await (await client.GetAsync($"{root}/runs/{runId}", Ct)).ReadJsonAsync();
            return value.GetProperty("status").GetString() == "completed" ? value : (JsonElement?)null;
        }, TimeSpan.FromSeconds(30));
        Assert.Equal(keyword, run.GetProperty("outputs").GetProperty("Review").GetProperty("input").GetProperty("keyword").GetGuid());
    }

    [Fact]
    public async Task People_inputs_assign_approvals_and_notify_the_selected_users_and_the_members_of_selected_groups()
    {
        await factory.CreateTenantAsync("workflow-people-inputs");
        var admin = await ApiClient.CreateAsync(factory, "workflow-people-inputs");
        async Task<Guid> Create(string url, object body)
        {
            var response = await admin.PostAsJsonAsync(url, body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        }

        var alice = await Create("/v1.0/users", new { userName = "alice", password = "alice-password-1" });
        var bob = await Create("/v1.0/users", new { userName = "bob", password = "bob-password-1" });
        await Create("/v1.0/users", new { userName = "carol", password = "carol-password-1" });
        var leads = await Create("/v1.0/groups", new { name = "Leads" });
        var juniors = await Create("/v1.0/groups", new { name = "Juniors" });
        await admin.PostAsJsonAsync($"/v1.0/groups/{juniors}/members", new { userId = bob }, Ct);
        await admin.PostAsJsonAsync($"/v1.0/groups/{leads}/groups", new { groupId = juniors }, Ct);

        // The documented example (docs/workflows.md): a people input names who approves and who is told.
        var workspace = await admin.CreateWorkspaceAsync("People");
        var root = $"/v1.0/workspaces/{workspace}/workflows";
        var workflow = await Create(root, new
        {
            name = "Review request",
            scope = "workspace",
            trigger = new { type = "manual" },
            inputSchema = JsonNode.Parse("""
                {"type":"object","properties":{
                  "reviewers":{"type":"array","title":"Reviewers","items":{"type":"string"},"x-paperdotnet":{"kind":"people"}},
                  "watcher":{"type":"string","title":"Watcher","x-paperdotnet":{"kind":"people","groups":false}}
                },"required":["reviewers"]}
                """),
            steps = new object[]
            {
                new { type = "action", action = "notify", inputs = new { to = new[] { "{input:watcher}" }, title = "Review requested" } },
                new { type = "approval", name = "Review", assignees = new[] { "{input:reviewers}" } },
            },
        });
        var launched = await admin.PostAsJsonAsync($"{root}/{workflow}/runs",
            new { inputs = new { reviewers = new[] { alice, leads }, watcher = alice } }, Ct);
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);

        async Task<int> PendingAsync(string user)
        {
            var client = await ApiClient.CreateAsync(factory, "workflow-people-inputs", user, $"{user}-password-1");
            var page = await (await client.GetAsync("/v1.0/me/approvals?status=pending", Ct)).ReadJsonAsync();
            return page.GetProperty("value").GetArrayLength();
        }

        // Alice by id, Bob as a member of a group inside the selected group; Carol was not selected.
        await Eventually.WaitForAsync(async () => await PendingAsync("alice") == 1 ? true : (bool?)null, TimeSpan.FromSeconds(30));
        Assert.Equal(1, await PendingAsync("bob"));
        Assert.Equal(0, await PendingAsync("carol"));
        var aliceClient = await ApiClient.CreateAsync(factory, "workflow-people-inputs", "alice", "alice-password-1");
        var notifications = await (await aliceClient.GetAsync("/v1.0/me/notifications", Ct)).ReadJsonAsync();
        Assert.Contains(notifications.GetProperty("value").EnumerateArray(), n => n.GetProperty("title").GetString() == "Review requested");
    }

    [Fact]
    public async Task Deleting_a_user_or_group_takes_it_out_of_people_inputs()
    {
        await factory.CreateTenantAsync("workflow-people-cleanup");
        var admin = await ApiClient.CreateAsync(factory, "workflow-people-cleanup");
        async Task<Guid> Create(string url, object body)
        {
            var response = await admin.PostAsJsonAsync(url, body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        }

        var dana = await Create("/v1.0/users", new { userName = "dana", password = "dana-password-1" });
        var kept = await Create("/v1.0/users", new { userName = "kept", password = "kept-password-1" });
        var team = await Create("/v1.0/groups", new { name = "Team" });
        await admin.PostAsJsonAsync($"/v1.0/groups/{team}/members", new { userId = dana }, Ct);
        await admin.PostAsJsonAsync($"/v1.0/groups/{team}/members", new { userId = kept }, Ct);
        var schema = JsonNode.Parse($$$"""
            {"type":"object","properties":{
              "reviewers":{"type":"array","items":{"type":"string"},"default":["{{{dana}}}","{{{kept}}}"],
                "x-paperdotnet":{"kind":"people","memberOf":"{{{team}}}","groups":false}},
              "owner":{"type":"string","default":"{{{team}}}","x-paperdotnet":{"kind":"people"}}
            }}
            """)!.AsObject();
        var workspace = await admin.CreateWorkspaceAsync("Cleanup");
        var root = $"/v1.0/workspaces/{workspace}/workflows";
        var workflow = await Create(root, new
        {
            name = "Cleanup",
            scope = "workspace",
            trigger = new { type = "manual" },
            inputSchema = schema,
            steps = new[] { new { type = "approval", name = "Review", assignees = new[] { "admin" }, inputSchema = schema } },
        });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{root}/{workflow}/runs", new { inputs = new { } }, Ct)).StatusCode);
        await Eventually.WaitForAsync(async () =>
            (await (await admin.GetAsync("/v1.0/me/approvals?status=pending", Ct)).ReadJsonAsync()).GetProperty("value").GetArrayLength() == 1 ? true : (bool?)null,
            TimeSpan.FromSeconds(30));
        var before = (await (await admin.GetAsync($"{root}/{workflow}", Ct)).ReadJsonAsync()).GetProperty("@odata.etag").GetString();

        // The group goes: the field is no longer limited to it and no default names it; the user goes from defaults.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/groups/{team}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/users/{dana}", Ct)).StatusCode);
        var current = await Eventually.WaitForAsync(async () =>
        {
            var value = await (await admin.GetAsync($"{root}/{workflow}", Ct)).ReadJsonAsync();
            return value.GetProperty("inputSchema").GetProperty("properties").GetProperty("reviewers").GetProperty("default").GetArrayLength() == 1
                   && !value.GetProperty("inputSchema").GetProperty("properties").GetProperty("owner").TryGetProperty("default", out _)
                ? value
                : (JsonElement?)null;
        }, TimeSpan.FromSeconds(30));
        var reviewers = current.GetProperty("inputSchema").GetProperty("properties").GetProperty("reviewers");
        Assert.Equal(kept, reviewers.GetProperty("default")[0].GetGuid());
        Assert.False(reviewers.GetProperty("x-paperdotnet").TryGetProperty("memberOf", out _));
        Assert.False(reviewers.GetProperty("x-paperdotnet").GetProperty("groups").GetBoolean());
        Assert.NotEqual(before, current.GetProperty("@odata.etag").GetString());

        // The pending approval's form, and the approval step's form in the saved definition, changed too.
        var approval = (await (await admin.GetAsync("/v1.0/me/approvals?status=pending", Ct)).ReadJsonAsync()).GetProperty("value")[0];
        var form = approval.GetProperty("inputSchema").GetProperty("properties");
        Assert.False(form.GetProperty("reviewers").GetProperty("x-paperdotnet").TryGetProperty("memberOf", out _));
        Assert.Equal(kept, Assert.Single(form.GetProperty("reviewers").GetProperty("default").EnumerateArray()).GetGuid());
        Assert.False(current.GetProperty("steps")[0].GetProperty("inputSchema").GetProperty("properties").GetProperty("owner").TryGetProperty("default", out _));

        // Saving the definition as it is now works again (the group no longer has to exist).
        var saved = JsonNode.Parse(current.GetRawText())!.AsObject();
        var body = new JsonObject
        {
            ["name"] = "Cleanup again",
            ["scope"] = "workspace",
            ["trigger"] = new JsonObject { ["type"] = "manual" },
            ["inputSchema"] = saved["inputSchema"]!.DeepClone(),
            ["steps"] = saved["steps"]!.DeepClone(),
        };
        var update = new HttpRequestMessage(HttpMethod.Put, $"{root}/{workflow}") { Content = JsonContent.Create(body) };
        update.Headers.TryAddWithoutValidation("If-Match", current.GetProperty("@odata.etag").GetString());
        var updated = await admin.SendAsync(update, Ct);
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync(Ct));
    }
}
