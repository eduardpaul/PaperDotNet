using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>JSON batching (API-04).</summary>
public sealed class BatchTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Dictionary<string, JsonElement>> BatchAsync(HttpClient client, params object[] requests)
    {
        var response = await client.PostAsJsonAsync("/v1.0/$batch", new { requests }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("responses").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
    }

    private static int Status(JsonElement response) => response.GetProperty("status").GetInt32();

    [Fact]
    public async Task A_batch_runs_requests_in_order_with_their_own_status()
    {
        await factory.CreateTenantAsync("batch-basic");
        var client = await ApiClient.CreateAsync(factory, "batch-basic");
        var ws = await client.CreateWorkspaceAsync("Batch");
        var contentType = await client.CreateContentTypeAsync("Row", [new { name = "note", type = "text" }]);
        var list = await client.CreateListAsync(ws, "Rows", contentType);
        var items = $"/workspaces/{ws}/lists/{list}/items";

        var responses = await BatchAsync(client,
            new { id = "me", method = "GET", url = "/me" },
            new { id = "add", method = "POST", url = items, body = new { fields = new { title = "From batch", note = "n" } } },
            new { id = "query", method = "GET", url = $"/v1.0{items}?$filter=fields/note eq 'n'", dependsOn = new[] { "add" } },
            new { id = "missing", method = "GET", url = $"{items}/{Guid.NewGuid()}" },
            new { id = "after-missing", method = "DELETE", url = $"{items}/{Guid.NewGuid()}", dependsOn = new[] { "missing" } },
            new { id = "invalid", method = "POST", url = items, body = new { fields = new { note = 42 } } });

        Assert.Equal(200, Status(responses["me"]));
        Assert.Equal("admin", responses["me"].GetProperty("body").GetProperty("userName").GetString());
        Assert.Equal(201, Status(responses["add"]));
        Assert.True(responses["add"].GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.Equal("From batch", responses["add"].GetProperty("body").GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal(200, Status(responses["query"]));
        Assert.Equal(1, responses["query"].GetProperty("body").GetProperty("value").GetArrayLength());
        Assert.Equal(404, Status(responses["missing"]));
        Assert.Equal(424, Status(responses["after-missing"]));
        Assert.Equal(400, Status(responses["invalid"]));

        // The ETag from one response works in a later batch (If-Match header).
        var id = responses["add"].GetProperty("body").GetProperty("id").GetGuid();
        var etag = responses["add"].GetProperty("headers").GetProperty("ETag").GetString();
        var update = await BatchAsync(client, new { id = "1", method = "PATCH", url = $"{items}/{id}", headers = new Dictionary<string, string> { ["If-Match"] = etag! }, body = new { fields = new { note = "changed" } } });
        Assert.Equal(200, Status(update["1"]));
    }

    [Fact]
    public async Task Batches_are_validated_and_need_authentication()
    {
        await factory.CreateTenantAsync("batch-rules");
        var client = await ApiClient.CreateAsync(factory, "batch-rules");
        static async Task<HttpStatusCode> PostAsync(HttpClient c, object body) => (await c.PostAsJsonAsync("/v1.0/$batch", body, Ct)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(client, new { requests = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(client, new { requests = Enumerable.Range(0, 21).Select(i => new { id = $"{i}", method = "GET", url = "/me" }) }));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(client, new { requests = new[] { new { id = "1", method = "GET", url = "/$batch" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(client, new { requests = new[] { new { id = "1", method = "GET", url = "https://example.com/v1.0/me" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(client, new { requests = new[] { new { id = "1", method = "TRACE", url = "/me" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(client, new
        {
            requests = new object[] { new { id = "1", method = "GET", url = "/me", dependsOn = new[] { "2" } }, new { id = "2", method = "GET", url = "/me" } },
        }));

        var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant", "batch-rules");
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(anonymous, new { requests = new[] { new { id = "1", method = "GET", url = "/me" } } }));
    }

    [Fact]
    public async Task Sub_requests_stay_in_the_callers_tenant()
    {
        await factory.CreateTenantAsync("batch-isolation-a");
        await factory.CreateTenantAsync("batch-isolation-b");
        var a = await ApiClient.CreateAsync(factory, "batch-isolation-a");
        var b = await ApiClient.CreateAsync(factory, "batch-isolation-b");
        var workspace = await a.CreateWorkspaceAsync("Only A");

        var responses = await BatchAsync(b,
            new { id = "1", method = "GET", url = $"/workspaces/{workspace}" },
            new { id = "2", method = "GET", url = $"/workspaces/{workspace}", headers = new Dictionary<string, string> { ["X-Tenant"] = "batch-isolation-a" } });
        Assert.Equal(404, Status(responses["1"]));
        Assert.True(Status(responses["2"]) is 401 or 403 or 404, responses["2"].ToString());
    }
}
