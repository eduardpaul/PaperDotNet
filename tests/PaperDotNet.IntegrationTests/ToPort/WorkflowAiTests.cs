using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>AI steps with page images (AI-07): waits for Documents (T15); the other AI tests are in <c>WorkflowAiTests</c>.</summary>
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
