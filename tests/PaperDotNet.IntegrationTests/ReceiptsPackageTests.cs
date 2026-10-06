using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// The sample package <c>samples/receipts-package</c>: applied as it ships, it reads a receipt tagged "ticket" in the AI batch
/// window and fills its fields and lines. No code of its own: the workflow JSON maps the answer (item.update with typed
/// tokens, forEach, item.create, item.delete).
/// </summary>
public sealed class ReceiptsPackageTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Reading = """
        {"store": "LIDL", "date": "2026-09-28", "currency": "EUR", "total": 6.19,
         "lines": [{"description": "Milk", "quantity": 1, "unitPrice": 1.19, "amount": 1.19},
                   {"description": "Bread", "quantity": 2, "unitPrice": 2.50, "amount": 5.00}]}
        """;

    private static string Template()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PaperDotNet.slnx")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "samples", "receipts-package", "template.xml"));
    }

    private static byte[] ReceiptPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        string[] lines = ["LIDL", "28.09.2026", "Milk 1 x 1.19", "Bread 2 x 2.50 5.00", "TOTAL EUR 6.19"];
        for (var i = 0; i < lines.Length; i++)
        {
            page.AddText(lines[i], 20, new PdfPoint(40, 760 - (i * 36)), font);
        }

        return builder.Build();
    }

    private async Task InTenantAsync(TenantSummary tenant, Func<IServiceProvider, WorkflowsDbContext, Task> action)
    {
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        await action(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>());
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("skipped")]
    public async Task The_receipts_package_reads_tagged_receipts_in_the_batch_window(string selection)
    {
        var tenant = await factory.CreateTenantAsync($"wf-batch-api-receipts-{selection}");
        var admin = await ApiClient.CreateAsync(factory, $"wf-batch-api-receipts-{selection}");
        var batchApi = FakeBatchClient.For(tenant.Identifier)!;
        // The "model" reads the receipt from its image (the package sends no text).
        batchApi.Answer = line => line.Images is { Count: > 0 } ? Reading : "{}";

        // The package as it ships, with a batch schedule that does not come by itself during the test.
        var apply = await admin.PostAsync($"/v1.0/provisioning/apply?parameters[BatchSchedule]={Uri.EscapeDataString("0 0 1 1 *")}",
            new StringContent(Template(), Encoding.UTF8, "application/xml"), Ct);
        var applied = await apply.ReadJsonAsync();
        Assert.True(apply.IsSuccessStatusCode, applied.ToString());
        Assert.Empty(applied.GetProperty("warnings").EnumerateArray());
        var again = await admin.PostAsync($"/v1.0/provisioning/apply?parameters[BatchSchedule]={Uri.EscapeDataString("0 0 1 1 *")}",
            new StringContent(Template(), Encoding.UTF8, "application/xml"), Ct);
        Assert.Empty((await again.ReadJsonAsync()).GetProperty("changes").EnumerateArray());

        var ws = (await (await admin.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Receipts").GetProperty("id").GetGuid();
        var lists = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists", Ct)).ReadJsonAsync()).EnumerateArray()
            .ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("id").GetGuid());
        var workflows = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows", Ct)).ReadJsonAsync()).EnumerateArray()
            .Where(w => !w.GetProperty("system").GetBoolean())
            .ToDictionary(w => w.GetProperty("name").GetString()!, w => w.GetProperty("id").GetGuid());
        // Besides its own and the product's system workflows, the workspace lists the library's document workflows ("… (Receipts)").
        Assert.Equal(["AI batch", "Read receipts"], workflows.Keys.Where(k => !k.EndsWith("(Receipts)", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        // The library makes thumbnails and page images, but reads no text and runs no OCR.
        var receipts = $"/v1.0/workspaces/{ws}/lists/{lists["Receipts"]}";
        var libraryWorkflows = (await (await admin.GetAsync($"{receipts}/workflows/builtIns", Ct)).ReadJsonAsync()).EnumerateArray()
            .ToDictionary(w => w.GetProperty("key").GetString()!, w => w.GetProperty("enabled").GetBoolean());
        Assert.Equal(new Dictionary<string, bool> { ["paperdotnet.storageoptimization.optimize"] = false, ["documents.ocr"] = false, ["documents.pages"] = true, ["documents.text"] = false, ["documents.thumbnail"] = true, ["search.index"] = true, ["search.indexEnriched"] = false },
            libraryWorkflows);

        // A receipt is uploaded (its images are made), then tagged "ticket": its reading waits for the batch.
        var upload = await admin.PostAsync($"{receipts}/documents", new MultipartFormDataContent { { new ByteArrayContent(selection == "pdf" ? ReceiptPdf() : StorageOptimizationTests.ReceiptImage(selection == "skipped" ? "" : "RECEIPT TOTAL 123.45")), "file", selection == "pdf" ? "lidl.pdf" : "receipt.png" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var receipt = (await upload.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        await Eventually.WaitForAsync(async () =>
        {
            var runs = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?itemId={receipt}", Ct)).ReadJsonAsync())
                .GetProperty("value").EnumerateArray().Where(r => r.GetProperty("workflowId").GetGuid() != workflows["Read receipts"]).ToList();
            return runs.Count >= 2 && runs.All(r => r.GetProperty("status").GetString() == "completed") ? true : (bool?)null;
        }, TimeSpan.FromSeconds(60));
        Guid? approvalId = null;
        if (selection is "approved" or "rejected")
        {
            var approval = await Eventually.WaitForAsync(async () =>
            {
                var pending = (await (await admin.GetAsync("/v1.0/me/approvals", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
                    .FirstOrDefault(a => a.GetProperty("itemId").GetGuid() == receipt);
                return pending.ValueKind == JsonValueKind.Object ? pending : (JsonElement?)null;
            }, TimeSpan.FromSeconds(60));
            // The image is still untagged. Upload alone must prepare a proposal, and no AI question is queued.
            Assert.Empty(batchApi.Submitted);
            await InTenantAsync(tenant, async (_, db) => Assert.False(await db.Bookmarks.AnyAsync(b => b.Kind.StartsWith("ai."), Ct)));
            approvalId = approval.GetProperty("id").GetGuid();
        }
        if (selection == "skipped")
        {
            await Eventually.WaitForAsync(async () =>
            {
                var runs = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?workflowId={workflows["Read receipts"]}&itemId={receipt}", Ct)).ReadJsonAsync())
                    .GetProperty("value").EnumerateArray().ToList();
                return runs.Count == 1 && runs[0].GetProperty("status").GetString() == "completed" ? true : (bool?)null;
            }, TimeSpan.FromSeconds(60));
            Assert.Empty(batchApi.Submitted);
        }
        var item = $"{receipts}/items/{receipt}";
        var tag = await admin.SendWithEtagAsync(HttpMethod.Patch, item, (await admin.GetAsync(item, Ct)).Headers.ETag!.Tag, new { fields = new { tags = new[] { "ticket" } } });
        Assert.True(tag.IsSuccessStatusCode, await tag.Content.ReadAsStringAsync(Ct));
        if (approvalId is { } reviewId)
        {
            // Tag changes must leave the open review intact. Extraction reads the current tag after the decision.
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/v1.0/me/approvals/{reviewId}/review", Ct)).StatusCode);
            await InTenantAsync(tenant, async (_, db) => Assert.False(await db.Bookmarks.AnyAsync(b => b.Kind.StartsWith("ai."), Ct)));
            var decision = await admin.PostAsJsonAsync($"/v1.0/me/approvals/{reviewId}/decision", new { outcome = selection }, Ct);
            Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync(Ct));
            await Eventually.WaitForAsync(async () =>
            {
                var file = await admin.GetAsync($"{receipts}/items/{receipt}/file", Ct);
                return file.Content.Headers.ContentType?.MediaType == (selection == "approved" ? "image/webp" : "image/png") ? true : (bool?)null;
            }, TimeSpan.FromSeconds(30));
        }


        Guid? readingRunId = null;
        async Task<JsonElement> RunAsync(params string[] statuses)
        {
            JsonElement run = default;
            await Eventually.WaitForAsync(async () =>
            {
                var response = await admin.GetAsync(readingRunId is { } id ? $"/v1.0/workspaces/{ws}/workflows/runs/{id}"
                    : $"/v1.0/workspaces/{ws}/workflows/runs?workflowId={workflows["Read receipts"]}&itemId={receipt}", Ct);
                var body = await response.ReadJsonAsync();
                run = readingRunId is not null ? body : body.GetProperty("value").EnumerateArray().FirstOrDefault();
                return run.ValueKind == JsonValueKind.Object && statuses.Contains(run.GetProperty("status").GetString()) && (statuses.Length != 1 || run.GetProperty("node").GetString() == "read") ? true : (bool?)null;
            }, TimeSpan.FromSeconds(30));
            return run;
        }

        var waiting = await RunAsync("waiting");
        readingRunId = waiting.GetProperty("id").GetGuid();
        if (selection == "skipped")
        {
            Assert.Contains("already selected", waiting.GetProperty("outputs").GetProperty("prepare").GetProperty("reason").GetString()!, StringComparison.Ordinal);
        }
        Assert.Empty(batchApi.Submitted);

        // The batch window: the schedule comes due, the batch is sent, and polling collects its answers.
        async Task BatchWindowAsync(int batches)
        {
            var batch = workflows["AI batch"];
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                InTenantAsync(tenant, (services, _) => services.GetRequiredService<WorkflowScheduleJob>().RunAsync(Ct))));
            await InTenantAsync(tenant, (_, db) =>
                db.Schedules.Where(x => x.Id == batch).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));
            await InTenantAsync(tenant, (services, _) => services.GetRequiredService<WorkflowScheduleJob>().RunAsync(Ct));
            await Eventually.WaitForAsync(() => Task.FromResult(batchApi.Submitted.Count == batches ? true : (bool?)null), TimeSpan.FromSeconds(30));
            await Eventually.WaitForAsync(async () =>
            {
                await InTenantAsync(tenant, async (services, db) =>
                {
                    await db.Bookmarks.Where(b => b.Kind == "ai.batch.poll" && b.CompletedAt == null)
                        .ExecuteUpdateAsync(u => u.SetProperty(b => b.ResumeAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
                    await services.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
                });
                var response = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs/{readingRunId}", Ct);
                var status = (await response.ReadJsonAsync()).GetProperty("status").GetString();
                return status is "completed" or "failed" ? true : (bool?)null;
            }, TimeSpan.FromSeconds(60));
        }

        await BatchWindowAsync(1);
        var line = Assert.Single(Assert.Single(batchApi.Submitted).Lines);
        Assert.DoesNotContain("LIDL", line.Input, StringComparison.Ordinal); // no text: the model reads the image
        Assert.NotNull(line.Schema?["properties"]?["lines"]);
        var image = Assert.Single(line.Images!);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.True(image.Content.Length > 1000);
        await InTenantAsync(tenant, async (services, _) =>
        {
            var selected = await services.GetRequiredService<IItemPageImageSource>().GetPageImagesAsync(receipt, 1, Ct);
            Assert.Equal(Assert.Single(selected).Content, image.Content);
        });
        var done = await RunAsync("completed", "failed");
        Assert.True(done.GetProperty("status").GetString() == "completed", done.ToString());

        // The receipt's fields and one item per line, linked to the receipt.
        var fields = (await (await admin.GetAsync(item, Ct)).ReadJsonAsync()).GetProperty("fields");
        Assert.True(fields.TryGetProperty("store", out _), done.ToString());
        Assert.Equal("LIDL", fields.GetProperty("store").GetString());
        Assert.Equal("2026-09-28", fields.GetProperty("purchaseDate").GetString());
        Assert.Equal("EUR", fields.GetProperty("currency").GetString());
        Assert.Equal(6.19m, fields.GetProperty("total").GetDecimal());
        Assert.Equal("Read", fields.GetProperty("status").GetString());
        var firstItems = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{lists["Receipt lines"]}/items", Ct)).ReadJsonAsync())
            .GetProperty("value").EnumerateArray().ToList();
        var firstLines = firstItems.Select(i => i.GetProperty("id").GetGuid()).ToList();
        var saved = firstItems.Select(i => i.GetProperty("fields")).OrderBy(f => f.GetProperty("title").GetString()).ToList();
        Assert.Equal(["Bread", "Milk"], saved.Select(f => f.GetProperty("title").GetString()));
        Assert.Equal([5.00m, 1.19m], saved.Select(f => f.GetProperty("amount").GetDecimal()));
        Assert.Equal(2.50m, saved[0].GetProperty("unitPrice").GetDecimal());
        Assert.All(saved, f => Assert.False(f.TryGetProperty("receipt", out _)));
        var edges = (await (await admin.GetAsync($"/v1.0/items/{receipt}/relationships?type=contains%20receipt%20line&direction=outgoing", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(firstLines.Order(), edges.Select(e => e.GetProperty("targetItemId").GetGuid()).Order());
        Assert.All(edges, e => Assert.True(e.GetProperty("directed").GetBoolean()));

        // Tagged again, with a tag below "ticket": read again. The question is the same (the same image), so the answer comes
        // from the cache at once, without a batch; the lines are replaced, not added.
        var retag = await admin.SendWithEtagAsync(HttpMethod.Patch, item, (await admin.GetAsync(item, Ct)).Headers.ETag!.Tag,
            new { fields = new { tags = new[] { "Groceries" } } });
        Assert.True(retag.IsSuccessStatusCode, await retag.Content.ReadAsStringAsync(Ct));

        await Eventually.WaitForAsync(async () =>
        {
            var response = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?workflowId={workflows["Read receipts"]}&itemId={receipt}", Ct);
            var runs = (await response.ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
            return runs.Count == (approvalId is null ? 3 : 2) && runs.All(r => r.GetProperty("status").GetString() == "completed") ? true : (bool?)null;
        }, TimeSpan.FromSeconds(30));
        Assert.Single(batchApi.Submitted);
        var replaced = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{lists["Receipt lines"]}/items", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(2, replaced.Count);
        Assert.Empty(replaced.Select(i => i.GetProperty("id").GetGuid()).Intersect(firstLines));

        // Another tenant sees none of it.
        await factory.CreateTenantAsync($"wf-receipts-b-{selection}");
        var foreign = await ApiClient.CreateAsync(factory, $"wf-receipts-b-{selection}");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(item, Ct)).StatusCode);
    }
}
