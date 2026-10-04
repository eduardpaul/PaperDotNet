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
        var schema = JsonNode.Parse($$$"""
            {"type":"object","properties":{
              "targets":{"type":"array","items":{"type":"string"},"minItems":1,"uniqueItems":true,"x-paperdotnet":{"kind":"relationship","relationshipType":"{{{type}}}"}},
              "tag":{"type":"string","x-paperdotnet":{"kind":"terms","groupId":"{{{group}}}"}},
              "tags":{"type":"array","items":{"type":"string"},"x-paperdotnet":{"kind":"terms","termSetId":"{{{set}}}","termIds":["{{{term}}}"]}},
              "keyword":{"type":"string","x-paperdotnet":{"kind":"keywords","termIds":["{{{keyword}}}"]}}
            },"required":["targets","tag","tags","keyword"]}
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
        };
        var invalid = new List<JsonObject>();
        foreach (var (name, value) in new (string, JsonNode)[]
        {
            ("targets", new JsonArray()), ("targets", new JsonArray(item.ToString(), item.ToString())),
            ("targets", new JsonArray(item.ToString(), item.ToString("N"))),
            ("targets", new JsonArray(Guid.NewGuid().ToString())), ("tag", JsonValue.Create(outside.ToString())),
            ("tag", JsonValue.Create("")), ("tags", new JsonArray()), ("tags", new JsonArray(other.ToString())),
            ("tags", new JsonArray(outside.ToString())), ("keyword", JsonValue.Create(term.ToString())),
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
}
