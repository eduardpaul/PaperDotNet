using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>AI activities of workflows (ADR-0036 slice 9c, AI-02…04, AI-06, AI-07) against a deterministic chat model (<see cref="ReadingChatClient"/>).</summary>
public sealed class WorkflowAiTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.ReadJsonAsync();
    }

    private static async Task<JsonElement> FinishedRunAsync(HttpClient client, string workflows, Guid workflow, Guid item)
    {
        JsonElement run = default;
        await Eventually.WaitForAsync(async () =>
        {
            var runs = (await GetAsync(client, $"{workflows}/runs?workflowId={workflow}&itemId={item}")).GetProperty("value").EnumerateArray().ToList();
            run = runs.FirstOrDefault();
            return runs.Count == 1 && run.GetProperty("status").GetString() is "completed" or "failed" ? true : (bool?)null;
        }, TimeSpan.FromSeconds(30));
        return run;
    }

    [Fact]
    public async Task Ai_activities_extract_classify_summarize_and_prompt()
    {
        var tenant = await factory.CreateTenantAsync("wf-ai");
        var admin = await ApiClient.CreateAsync(factory, "wf-ai");
        var ws = await admin.CreateWorkspaceAsync("Household");
        var workflows = $"/v1.0/workspaces/{ws}/workflows";
        var tasks = await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Tasks", templateKey = "tasks" });
        var group = await PostIdAsync(admin, "/v1.0/termStore/groups", new { name = "Documents" });
        var kinds = await PostIdAsync(admin, "/v1.0/termStore/sets", new { groupId = group, name = "Kinds" });
        var receiptTerm = await PostIdAsync(admin, $"/v1.0/termStore/sets/{kinds}/terms", new { name = "Receipt" });
        await PostIdAsync(admin, $"/v1.0/termStore/sets/{kinds}/terms", new { name = "Contract" });
        var type = await admin.CreateContentTypeAsync("Receipt",
        [
            new { name = "text", type = "note" },
            new { name = "store", type = "text" },
            new { name = "purchaseDate", type = "date" },
            new { name = "total", type = "currency", currencyCode = "EUR" },
            new { name = "payment", type = "choice", choices = new[] { "cash", "card" } },
            new { name = "kind", type = "managedMetadata", termSetId = kinds },
            new { name = "summary", type = "note" },
        ]);
        var receipts = await admin.CreateListAsync(ws, "Receipts", type);
        string Item(Guid id) => $"/v1.0/workspaces/{ws}/lists/{receipts}/items/{id}";

        // Extraction fills the fields the model is sure about; a missing value leads to a review task.
        var extract = await PostIdAsync(admin, workflows, new
        {
            name = "Read receipts",
            trigger = new { type = "itemAdded", list = "Receipts" },
            flow = new
            {
                start = "extract",
                nodes = new Dictionary<string, object>
                {
                    ["extract"] = new
                    {
                        activity = "ai.extract",
                        inputs = new { fields = new[] { "store", "purchaseDate", "total", "payment" }, minConfidence = 0.8 },
                        next = new { done = "classify", lowConfidence = "review" },
                    },
                    ["review"] = new { activity = "task.create", inputs = new { list = "Tasks", title = "Check {title}: {step:extract.uncertain}" }, next = new { done = "classify" } },
                    ["classify"] = new { activity = "ai.classify", inputs = new { termSet = "Documents/Kinds", field = "kind" }, next = new { done = "summarize" } },
                    ["summarize"] = new { activity = "ai.summarize", inputs = new { field = "summary", maxWords = 20 } },
                },
            },
        });

        var full = (await admin.CreateItemAsync(ws, receipts, new
        {
            fields = new { title = "Aldi receipt", text = "A receipt\nstore: Aldi\npurchaseDate: 2026-09-28\ntotal: 12.50 EUR\npayment: card" },
        })).GetProperty("id").GetGuid();
        var run = await FinishedRunAsync(admin, workflows, extract, full);
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var fields = (await GetAsync(admin, Item(full))).GetProperty("fields");
        Assert.Equal("Aldi", fields.GetProperty("store").GetString());
        Assert.Equal("2026-09-28", fields.GetProperty("purchaseDate").GetString());
        Assert.Equal(12.5m, fields.GetProperty("total").GetDecimal());
        Assert.Equal("card", fields.GetProperty("payment").GetString());
        Assert.Equal(receiptTerm.ToString(), fields.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(fields.GetProperty("summary").GetString()));
        Assert.Equal(4, run.GetProperty("outputs").GetProperty("extract").GetProperty("applied").GetArrayLength());
        Assert.Equal("Receipt", run.GetProperty("outputs").GetProperty("classify").GetProperty("term").GetString());

        var partial = (await admin.CreateItemAsync(ws, receipts, new
        {
            fields = new { title = "Market note", text = "store: Market\npayment: cash" },
        })).GetProperty("id").GetGuid();
        Assert.Equal("completed", (await FinishedRunAsync(admin, workflows, extract, partial)).GetProperty("status").GetString());
        Assert.Contains("Check Market note: purchaseDate, total", await admin.QueryTitlesAsync(ws, tasks, ""));
        Assert.Equal("Market", (await GetAsync(admin, Item(partial))).GetProperty("fields").GetProperty("store").GetString());

        // Prompts: text or JSON by a schema; the same model and input reuse the earlier answer.
        var ask = await PostIdAsync(admin, workflows, new
        {
            name = "Ask",
            trigger = new { type = "manual", list = "Receipts" },
            flow = new
            {
                start = "ask",
                nodes = new Dictionary<string, object>
                {
                    ["ask"] = new { activity = "ai.prompt", inputs = new { prompt = "Is {store} a supermarket?" }, next = new { done = "json" } },
                    ["json"] = new
                    {
                        activity = "ai.prompt",
                        inputs = new { prompt = "Categorize {title}", schema = new { type = "object", properties = new { category = new { type = "string" } }, required = new[] { "category" } } },
                        next = new { done = "note" },
                    },
                    ["note"] = new { activity = "item.update", inputs = new { fields = new { summary = "{step:ask.text} / {step:json.json.category}" } } },
                },
            },
        });
        var callsBefore = ReadingChatClient.Instance.Calls;
        Assert.True((await admin.PostAsJsonAsync($"{Item(full)}/workflows", new { workflow = "Ask" }, Ct)).IsSuccessStatusCode);
        var asked = await FinishedRunAsync(admin, workflows, ask, full);
        Assert.Equal("completed", asked.GetProperty("status").GetString());
        Assert.Equal("Echo: Is Aldi a supermarket? / category", (await GetAsync(admin, Item(full))).GetProperty("fields").GetProperty("summary").GetString());
        Assert.Equal(callsBefore + 2, ReadingChatClient.Instance.Calls);

        await admin.PostAsJsonAsync($"{workflows}/runs/{asked.GetProperty("id").GetGuid()}/cancel", new { }, Ct);
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<AiWorkflowsDbContext>();
            var calls = await db.AiCalls.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync(Ct);
            Assert.Contains(calls, c => c.Activity == "ai.extract" && c.Model == "reading-model" && c.InputTokens > 0 && !c.Cached && c.Source == "workflow:Read receipts");
            Assert.All(calls, c => Assert.NotNull(c.RunId));

            // Once the day's budget is used up, AI activities fail (the run can be retried later).
            db.AiCalls.Add(new AiCall
            {
                Id = Ids.New(),
                Activity = "ai.prompt",
                Source = "test",
                Model = "reading-model",
                InputHash = "x",
                InputTokens = 2_000_000,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var another = (await admin.CreateItemAsync(ws, receipts, new { fields = new { title = "Late", text = "store: Late" } })).GetProperty("id").GetGuid();
        var over = await FinishedRunAsync(admin, workflows, extract, another);
        Assert.Equal("failed", over.GetProperty("status").GetString());
        Assert.Contains("budget", over.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("extract", over.GetProperty("failedNode").GetString());

        // The same question again comes from the cache: no call to the model, recorded as cached.
        var before = ReadingChatClient.Instance.Calls;
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<AiWorkflowsDbContext>();
            await db.AiCalls.Where(c => c.Source == "test").ExecuteDeleteAsync(Ct);
        }

        Assert.True((await admin.PostAsJsonAsync($"{Item(full)}/workflows", new { workflow = "Ask" }, Ct)).IsSuccessStatusCode);
        await Eventually.WaitForAsync(async () =>
            (await GetAsync(admin, $"{workflows}/runs?workflowId={ask}&itemId={full}")).GetProperty("value").EnumerateArray()
                .Count(r => r.GetProperty("status").GetString() == "completed") == 2 ? true : (bool?)null, TimeSpan.FromSeconds(30));
        Assert.Equal(before, ReadingChatClient.Instance.Calls);
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AiWorkflowsDbContext>().AiCalls.CountAsync(c => c.Cached, Ct));
        }

        // The catalog describes the AI activities.
        var catalog = (await GetAsync(admin, "/v1.0/workflows/activities")).EnumerateArray().ToList();
        Assert.Contains("lowConfidence", catalog.Single(a => a.GetProperty("key").GetString() == "ai.extract").GetProperty("ports").ToString(), StringComparison.Ordinal);
        Assert.All(new[] { "ai.classify", "ai.summarize", "ai.prompt" }, key => Assert.Contains(catalog, a => a.GetProperty("key").GetString() == key));
    }

    [Fact]
    public async Task Ai_steps_send_the_pages_of_a_document_as_images()
    {
        await factory.CreateTenantAsync("wf-ai-images");
        var admin = await ApiClient.CreateAsync(factory, "wf-ai-images");
        var ws = await admin.CreateWorkspaceAsync("Scans");
        var workflows = $"/v1.0/workspaces/{ws}/workflows";
        var library = await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Scans", templateKey = "documents" });
        static object Flow(object? images) => new
        {
            start = "ask",
            nodes = new Dictionary<string, object>
            {
                ["ask"] = new { activity = "ai.prompt", inputs = new { prompt = "What is on it?", includeImages = images }, next = new { done = "save" } },
                ["save"] = new { activity = "item.update", inputs = new { fields = new { description = "{step:ask.text}" } } },
            },
        };

        var invalid = await admin.PostAsJsonAsync(workflows, new { name = "Bad", trigger = new { type = "manual", list = "Scans" }, flow = Flow("yes") }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("includeImages", await invalid.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var look = await PostIdAsync(admin, workflows, new { name = "Look", trigger = new { type = "manual", list = "Scans" }, flow = Flow(2) });

        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("A receipt", 20, new UglyToad.PdfPig.Core.PdfPoint(40, 760),
            builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica));
        var upload = await admin.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents",
            new MultipartFormDataContent { { new ByteArrayContent(builder.Build()), "file", "scan.pdf" } }, Ct);
        var item = (await upload.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var url = $"/v1.0/workspaces/{ws}/lists/{library}/items/{item}";
        Assert.True((await admin.PostAsJsonAsync($"{url}/workflows", new { workflow = "Look" }, Ct)).IsSuccessStatusCode);

        // The one page goes as an image (asked for two); the model's answer says it saw it.
        var run = await FinishedRunAsync(admin, workflows, look, item);
        Assert.True(run.GetProperty("status").GetString() == "completed", run.ToString());
        Assert.Equal("Echo: What is on it? [1 image(s)]", (await GetAsync(admin, url)).GetProperty("fields").GetProperty("description").GetString());
    }
}
