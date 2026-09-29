using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
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
/// window and fills its fields and lines through the sample extension's action.
/// </summary>
public sealed class ReceiptsPackageTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Reading = """
        {"store": "LIDL", "date": "2026-09-28", "currency": "eur", "total": 6.19,
         "lines": [{"description": "Milk", "quantity": 1, "unitPrice": 1.19, "amount": 1.19},
                   {"description": "Bread", "quantity": 2, "unitPrice": "2,50", "amount": 5.00}]}
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

    [Fact]
    public async Task The_receipts_package_reads_tagged_receipts_in_the_batch_window()
    {
        var tenant = await factory.CreateTenantAsync("wf-batch-api-receipts");
        var admin = await ApiClient.CreateAsync(factory, "wf-batch-api-receipts");
        var batchApi = FakeBatchClient.For(tenant.Identifier)!;
        batchApi.Answer = line => line.Input.Contains("LIDL", StringComparison.Ordinal) ? Reading : "{}";

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
            .ToDictionary(w => w.GetProperty("name").GetString()!, w => w.GetProperty("id").GetGuid());
        Assert.Equal(["AI batch", "Read new receipts", "Read tagged receipts"], workflows.Keys.Order(StringComparer.Ordinal));

        // A receipt is uploaded, processed, then tagged "ticket": its reading waits for the batch.
        var receipts = $"/v1.0/workspaces/{ws}/lists/{lists["Receipts"]}";
        var upload = await admin.PostAsync($"{receipts}/documents", new MultipartFormDataContent { { new ByteArrayContent(ReceiptPdf()), "file", "lidl.pdf" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var receipt = (await upload.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var versions = (await (await admin.GetAsync($"{receipts}/items/{receipt}/file/versions", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray();
            return versions.All(v => v.GetProperty("processingStatus").GetString() == "succeeded") ? true : null;
        }, TimeSpan.FromSeconds(60));
        var item = $"{receipts}/items/{receipt}";
        var tag = await admin.SendWithEtagAsync(HttpMethod.Patch, item, (await admin.GetAsync(item, Ct)).Headers.ETag!.Tag, new { fields = new { tags = new[] { "ticket" } } });
        Assert.True(tag.IsSuccessStatusCode, await tag.Content.ReadAsStringAsync(Ct));

        async Task<JsonElement> RunAsync(params string[] statuses)
        {
            JsonElement run = default;
            await Eventually.WaitForAsync(async () =>
            {
                var response = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?workflowId={workflows["Read tagged receipts"]}&itemId={receipt}", Ct);
                run = (await response.ReadJsonAsync()).GetProperty("value").EnumerateArray().FirstOrDefault();
                return run.ValueKind == JsonValueKind.Object && statuses.Contains(run.GetProperty("status").GetString()) ? true : (bool?)null;
            }, TimeSpan.FromSeconds(30));
            return run;
        }

        await RunAsync("waiting");
        Assert.Empty(batchApi.Submitted);

        // The batch window: the schedule comes due, the batch is sent, and polling collects its answers.
        async Task BatchWindowAsync(int batches)
        {
            var batch = workflows["AI batch"];
            await InTenantAsync(tenant, (services, _) => services.GetRequiredService<WorkflowScheduleJob>().RunAsync(Ct));
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
                var response = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?workflowId={workflows["Read tagged receipts"]}&itemId={receipt}", Ct);
                var status = (await response.ReadJsonAsync()).GetProperty("value").EnumerateArray().First().GetProperty("status").GetString();
                return status is "completed" or "failed" ? true : (bool?)null;
            }, TimeSpan.FromSeconds(60));
        }

        await BatchWindowAsync(1);
        var line = Assert.Single(Assert.Single(batchApi.Submitted).Lines);
        Assert.Contains("LIDL", line.Input, StringComparison.Ordinal);
        Assert.NotNull(line.Schema?["properties"]?["lines"]);
        var image = Assert.Single(line.Images!);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.True(image.Content.Length > 1000);
        var done = await RunAsync("completed", "failed");
        Assert.True(done.GetProperty("status").GetString() == "completed", done.ToString());

        // The receipt's fields and one item per line, linked to the receipt.
        var fields = (await (await admin.GetAsync(item, Ct)).ReadJsonAsync()).GetProperty("fields");
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
        Assert.All(saved, f => Assert.Equal(receipt.ToString(), f.GetProperty("receipt").GetString()));

        // Tagged again, with a tag below "ticket": read again in the next batch (its values changed, so the question did too),
        // and the lines are replaced, not added.
        foreach (var tags in new[] { Array.Empty<string>(), ["Groceries"] })
        {
            var retag = await admin.SendWithEtagAsync(HttpMethod.Patch, item, (await admin.GetAsync(item, Ct)).Headers.ETag!.Tag, new { fields = new { tags } });
            Assert.True(retag.IsSuccessStatusCode, await retag.Content.ReadAsStringAsync(Ct));
        }

        await Eventually.WaitForAsync(async () =>
        {
            var response = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?workflowId={workflows["Read tagged receipts"]}&itemId={receipt}&status=waiting", Ct);
            return (await response.ReadJsonAsync()).GetProperty("value").GetArrayLength() == 1 ? true : (bool?)null;
        }, TimeSpan.FromSeconds(30));
        await BatchWindowAsync(2);
        Assert.Equal("completed", (await RunAsync("completed", "failed")).GetProperty("status").GetString());
        var replaced = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{lists["Receipt lines"]}/items", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(2, replaced.Count);
        Assert.Empty(replaced.Select(i => i.GetProperty("id").GetGuid()).Intersect(firstLines));

        // Another tenant sees none of it.
        await factory.CreateTenantAsync("wf-receipts-b");
        var foreign = await ApiClient.CreateAsync(factory, "wf-receipts-b");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(item, Ct)).StatusCode);
    }
}
