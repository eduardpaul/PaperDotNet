using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Features.StorageOptimization;
using PaperDotNet.Documents.Features.PhotoToDocument;
using PaperDotNet.Documents.Features;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Tenancy.Contracts;
using UglyToad.PdfPig;

namespace PaperDotNet.IntegrationTests;

public sealed class PhotoCompositionTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private async Task<(TenantSummary Tenant, HttpClient Client, Guid Workspace, Guid Library, string Path, Guid[] Items)> SetupAsync(string suffix, PaperDotNetApiFactory? host = null)
    {
        host ??= factory;
        var tenant = await host.CreateTenantAsync("composition-" + suffix);
        var client = await ApiClient.CreateAsync(host, tenant.Identifier);
        var ws = await client.CreateWorkspaceAsync("Photos");
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Photos", templateKey = "documents" }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var library = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        var path = $"/v1.0/workspaces/{ws}/lists/{library}";
        var items = new List<Guid>();
        for (var i = 0; i < 3; i++) items.Add(await UploadAsync(client, path, $"page-{i}.png", StorageOptimizationTests.ReceiptImage($"RECEIPT PAGE {i + 1} TOTAL {4711 + i}")));
        return (tenant, client, ws, library, path, items.ToArray());
    }
    private static async Task<Guid> UploadAsync(HttpClient client, string path, string name, byte[] content)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", name } };
        var response = await client.PostAsync($"{path}/documents", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("itemId").GetGuid();
    }
    private static async Task<(Guid Run, Guid Approval)> LaunchAsync(HttpClient client, Guid ws, Guid list, string path, Guid[] ids, Guid primary, HttpClient? reviewClient = null)
    {
        var launch = await client.PostAsJsonAsync($"{path}/workflows/builtIns/paperdotnet.storageoptimization.photoToDocument/runs", new { listId = list, itemIds = ids, primaryItemId = primary }, Ct);
        Assert.True(launch.IsSuccessStatusCode, await launch.Content.ReadAsStringAsync(Ct));
        var run = Assert.Single((await launch.ReadJsonAsync()).EnumerateArray()).GetProperty("id").GetGuid();
        var settled = await Eventually.WaitForAsync(async () =>
        {
            var response = await client.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs/{run}", Ct);
            var result = await response.ReadJsonAsync();
            return result.GetProperty("status").GetString() is "waiting" or "failed" ? result : (JsonElement?)null;
        }, TimeSpan.FromSeconds(60));
        Assert.True(settled.GetProperty("status").GetString() == "waiting", settled.ToString());
        var approvals = await (reviewClient ?? client).GetAsync("/v1.0/me/approvals", Ct);
        var approval = Assert.Single((await approvals.ReadJsonAsync()).GetProperty("value").EnumerateArray(), a => a.GetProperty("runId").GetGuid() == run).GetProperty("id").GetGuid();
        return (run, approval);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task Photos_are_unchanged_until_review_then_pdf_is_promoted_or_discarded(string outcome)
    {
        var (tenant, client, ws, list, path, ids) = await SetupAsync(outcome);
        var order = new[] { ids[2], ids[0], ids[1] };
        var (run, approval) = await LaunchAsync(client, ws, list, path, order, ids[1]);
        foreach (var id in ids) Assert.Equal("image/png", (await client.GetAsync($"{path}/items/{id}/file", Ct)).Content.Headers.ContentType?.MediaType);
        var review = await client.GetAsync($"/v1.0/me/approvals/{approval}/review", Ct);
        var descriptor = await review.ReadJsonAsync();
        Assert.Equal("pdfComposition", descriptor.GetProperty("renderer").GetString());
        Assert.True(descriptor.GetProperty("canDecide").GetBoolean());
        var candidatePath = $"/v1.0/me/approvals/{approval}/review/content/candidate";
        var candidate = await client.GetByteArrayAsync(candidatePath, Ct);
        using (var pdf = PdfDocument.Open(candidate))
        {
            Assert.Equal(3, pdf.NumberOfPages);
            Assert.Contains("4713", pdf.GetPage(1).Text, StringComparison.Ordinal);
            Assert.Contains("4711", pdf.GetPage(2).Text, StringComparison.Ordinal);
            Assert.Contains("4712", pdf.GetPage(3).Text, StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1.0/me/approvals/{approval}/review/content/page-2", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1.0/me/approvals/{approval}/review/content/source-3", Ct)).StatusCode);
        var decision = await client.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome }, Ct);
        Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync(Ct));
        await SelectionWorkflowTests.WaitAsync(client, ws, run, "completed");
        var file = await client.GetAsync($"{path}/items/{ids[1]}/file", Ct);
        Assert.Equal(outcome == "approved" ? "application/pdf" : "image/png", file.Content.Headers.ContentType?.MediaType);
        var versions = await client.GetAsync($"{path}/items/{ids[1]}/file/versions", Ct);
        Assert.Equal(outcome == "approved" ? 2 : 1, (await versions.ReadJsonAsync()).GetProperty("value").GetArrayLength());
        foreach (var id in ids.Where(i => i != ids[1]))
            Assert.Equal(outcome == "approved" ? HttpStatusCode.NotFound : HttpStatusCode.OK, (await client.GetAsync($"{path}/items/{id}", Ct)).StatusCode);
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var photoDb = scope.ServiceProvider.GetRequiredService<PhotoConversionDbContext>();
        var record = await photoDb.Conversions.SingleAsync(c => c.RunId == run, Ct);
        await using var actorScope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier, record.StartedBy);
        var store = actorScope.ServiceProvider.GetRequiredService<PhotoConversionStore>();
        Assert.Equal(outcome == "approved" ? "accepted" : "rejected", record.State);
        if (outcome == "approved")
        {
            Assert.True(await store.PromoteAsync(record.Id, Ct)); // replay after the committed replacement
            Assert.Equal(2, await db.FileVersions.CountAsync(v => v.ItemId == ids[1], Ct));
            Assert.Equal(candidate, await file.Content.ReadAsByteArrayAsync(Ct));
            var items = actorScope.ServiceProvider.GetRequiredService<IListItemStore>();
            var restored = await items.RestoreAsync(ws, list, ids[0], null, Ct);
            Assert.True(restored.Succeeded);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/items/{ids[0]}/file", Ct)).StatusCode);
        }
        else Assert.False(await store.PromoteAsync(record.Id, Ct));
    }

    [Fact]
    public async Task Documents_provides_photo_launch_and_review_without_a_separate_extension()
    {
        var (_, client, ws, list, path, ids) = await SetupAsync("documents-photos");
        var extensions = await (await client.GetAsync("/v1.0/extensions", Ct)).ReadJsonAsync();
        Assert.DoesNotContain(extensions.EnumerateArray(), e => e.GetProperty("id").GetString() == "paperdotnet.storageoptimization");
        var catalog = await (await client.GetAsync($"{path}/workflows/builtIns", Ct)).ReadJsonAsync();
        Assert.Contains(catalog.EnumerateArray(), w => w.GetProperty("key").GetString() == "paperdotnet.storageoptimization.photoToDocument");
        var (run, approval) = await LaunchAsync(client, ws, list, path, ids, ids[0]);
        var review = await (await client.GetAsync($"/v1.0/me/approvals/{approval}/review", Ct)).ReadJsonAsync();
        Assert.True(review.GetProperty("canDecide").GetBoolean());
        var duplicate = await client.PostAsJsonAsync($"{path}/workflows/builtIns/paperdotnet.storageoptimization.photoToDocument/runs", new { itemIds = ids }, Ct);
        Assert.Empty((await duplicate.ReadJsonAsync()).EnumerateArray());
        await SelectionWorkflowTests.WaitAsync(client, ws, run, "waiting");
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("metadata")]
    public async Task Changing_any_source_disables_decisions_and_never_replaces_the_photos(string change)
    {
        var (_, client, ws, list, path, ids) = await SetupAsync("stale-" + change);
        var (_, approval) = await LaunchAsync(client, ws, list, path, ids, ids[0]);
        if (change == "upload")
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(StorageOptimizationTests.ReceiptImage("NEW PHOTO 9999")), "file", "changed.png" } };
            Assert.True((await client.PutAsync($"{path}/items/{ids[2]}/file", form, Ct)).IsSuccessStatusCode);
        }
        else
        {
            var current = await client.GetAsync($"{path}/items/{ids[2]}", Ct);
            using var update = new HttpRequestMessage(HttpMethod.Patch, $"{path}/items/{ids[2]}") { Content = JsonContent.Create(new { fields = new { title = "Changed title" } }) };
            update.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.ToString());
            var changed = await client.SendAsync(update, Ct);
            Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync(Ct));
        }
        var review = await client.GetAsync($"/v1.0/me/approvals/{approval}/review", Ct);
        Assert.False((await review.ReadJsonAsync()).GetProperty("canDecide").GetBoolean());
        Assert.False((await client.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome = "approved" }, Ct)).IsSuccessStatusCode);
        foreach (var id in ids) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/items/{id}", Ct)).StatusCode);
        Assert.Equal("image/png", (await client.GetAsync($"{path}/items/{ids[0]}/file", Ct)).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Conflicting_reviewed_item_rolls_back_entire_replacement_and_recycling()
    {
        var (tenant, client, ws, list, path, ids) = await SetupAsync("rollback");
        var (run, _) = await LaunchAsync(client, ws, list, path, ids, ids[0]);
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var photoDb = scope.ServiceProvider.GetRequiredService<PhotoConversionDbContext>();
        var candidate = await photoDb.Conversions.SingleAsync(c => c.RunId == run, Ct);
        await using var actorScope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier, candidate.StartedBy);
        var failing = new FailingRecycle(actorScope.ServiceProvider.GetRequiredService<IItemBatchRecycle>());
        var store = ActivatorUtilities.CreateInstance<PhotoConversionStore>(actorScope.ServiceProvider, failing);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PromoteAsync(candidate.Id, Ct));
        Assert.Contains("injected", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, (await (await client.GetAsync($"{path}/items", Ct)).ReadJsonAsync()).GetProperty("value").GetArrayLength());
        Assert.Equal(1, await db.FileVersions.CountAsync(v => v.ItemId == ids[0], Ct));
    }

    [Fact]
    public async Task Unsupported_photos_fail_without_approval_or_changes()
    {
        var (_, client, ws, list, path, ids) = await SetupAsync("unsupported");
        var pdfId = await UploadAsync(client, path, "already.pdf", DocumentPdf());
        var launch = await client.PostAsJsonAsync($"{path}/workflows/builtIns/paperdotnet.storageoptimization.photoToDocument/runs", new { listId = list, itemIds = new[] { ids[0], pdfId } }, Ct);
        var run = (await launch.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await SelectionWorkflowTests.WaitAsync(client, ws, run, "failed");
        var approvals = await client.GetAsync("/v1.0/me/approvals", Ct);
        Assert.DoesNotContain((await approvals.ReadJsonAsync()).GetProperty("value").EnumerateArray(), a => a.GetProperty("runId").GetGuid() == run);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/items/{ids[0]}", Ct)).StatusCode);
    }
    [Fact]
    public async Task A_blank_photo_can_produce_a_larger_pdf_and_keeps_its_page()
    {
        var (_, client, ws, list, path, ids) = await SetupAsync("blank");
        using var bitmap = new SkiaSharp.SKBitmap(100, 100);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var blank = await UploadAsync(client, path, "blank.png", png.ToArray());
        var (_, approval) = await LaunchAsync(client, ws, list, path, [blank, ids[0]], blank);
        var content = await client.GetByteArrayAsync($"/v1.0/me/approvals/{approval}/review/content/candidate", Ct);
        Assert.True(content.Length > png.Size);
        using var pdf = PdfDocument.Open(content);
        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Empty(pdf.GetPage(1).Text.Trim());
        Assert.Contains("4711", pdf.GetPage(2).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_review_protects_bytes_and_cancellation_releases_the_candidate()
    {
        var (tenant, client, ws, list, path, ids) = await SetupAsync("cleanup");
        var (run, _) = await LaunchAsync(client, ws, list, path, ids, ids[0]);
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var photoDb = scope.ServiceProvider.GetRequiredService<PhotoConversionDbContext>();
        var record = await photoDb.Conversions.SingleAsync(c => c.RunId == run, Ct);
        var staged = await db.StagedFiles.SingleAsync(c => c.Id == record.Id, Ct);
        var storedId = staged.StoredFileId;
        staged.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await db.StoredFiles.Where(f => f.Id == storedId).ExecuteUpdateAsync(u => u.SetProperty(f => f.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await db.SaveChangesAsync(Ct);
        var job = ActivatorUtilities.CreateInstance<StoredFileCleanupJob>(scope.ServiceProvider);
        await job.RunAsync(Ct);
        Assert.True(await db.StoredFiles.AnyAsync(f => f.Id == storedId, Ct));
        var cancelled = await client.PostAsync($"/v1.0/workspaces/{ws}/workflows/runs/{run}/cancel", null, Ct);
        Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync(Ct));
        await job.RunAsync(Ct);
        Assert.False(await db.StoredFiles.AnyAsync(f => f.Id == storedId, Ct));
        Assert.Equal("abandoned", (await photoDb.Conversions.AsNoTracking().SingleAsync(c => c.Id == record.Id, Ct)).State);
        foreach (var id in ids) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/items/{id}/file", Ct)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ocr_failure_keeps_originals_and_retry_uses_the_original_snapshot(bool changeSource)
    {
        await using var host = new PaperDotNetApiFactory { OcrPath = "nonexistent-photo-composition-ocr" };
        await host.InitializeAsync();
        var (tenant, client, ws, list, path, ids) = await SetupAsync("ocr-retry-" + changeSource.ToString().ToLowerInvariant(), host);
        var launch = await client.PostAsJsonAsync($"{path}/workflows/builtIns/paperdotnet.storageoptimization.photoToDocument/runs", new { itemIds = ids }, Ct);
        var run = (await launch.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        var failure = await SelectionWorkflowTests.WaitAsync(client, ws, run, "failed");
        Assert.Contains("not installed", failure.GetProperty("error").GetString()!, StringComparison.Ordinal);
        await using var scope = host.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var photoDb = scope.ServiceProvider.GetRequiredService<PhotoConversionDbContext>();
        var record = await photoDb.Conversions.SingleAsync(c => c.RunId == run, Ct);
        var snapshots = record.SourcesJson;
        foreach (var id in ids) Assert.Equal("image/png", (await client.GetAsync($"{path}/items/{id}/file", Ct)).Content.Headers.ContentType?.MediaType);
        if (changeSource)
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(StorageOptimizationTests.ReceiptImage("UPDATED 9999")), "file", "updated.png" } };
            Assert.True((await client.PutAsync($"{path}/items/{ids[2]}/file", form, Ct)).IsSuccessStatusCode);
        }
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PaperDotNet.Ocr.OcrOptions>>().Value.TesseractPath = "tesseract";
        var retry = await client.PostAsync($"/v1.0/workspaces/{ws}/workflows/runs/{run}/retry", null, Ct);
        Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync(Ct));
        await SelectionWorkflowTests.WaitAsync(client, ws, run, changeSource ? "failed" : "waiting");
        Assert.Equal(snapshots, await photoDb.Conversions.AsNoTracking().Where(s => s.Id == record.Id).Select(s => s.SourcesJson).SingleAsync(Ct));
        var approvals = await client.GetAsync("/v1.0/me/approvals", Ct);
        Assert.Equal(changeSource ? 0 : 1, (await approvals.ReadJsonAsync()).GetProperty("value").EnumerateArray().Count(a => a.GetProperty("runId").GetGuid() == run));
    }

    [Theory]
    [InlineData("launcher")]
    [InlineData("reviewer")]
    public async Task All_sources_require_access_at_launch_review_and_commit(string revoked)
    {
        var (tenant, admin, ws, list, path, ids) = await SetupAsync("access-" + revoked);
        var user = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "writer", password = "writer-password-123" }, Ct);
        var writerId = (await user.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = writerId, role = "visitor" }, Ct)).IsSuccessStatusCode);
        var writer = await ApiClient.CreateAsync(factory, tenant.Identifier, "writer", "writer-password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync($"{path}/workflows/builtIns/paperdotnet.storageoptimization.photoToDocument/runs", new { itemIds = ids }, Ct)).StatusCode);
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = writerId, role = "member" }, Ct)).IsSuccessStatusCode);
        HttpClient reviewer = admin;
        if (revoked == "reviewer")
        {
            var checker = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "checker", password = "checker-password-123" }, Ct);
            var checkerId = (await checker.ReadJsonAsync()).GetProperty("id").GetGuid();
            Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = checkerId, role = "visitor" }, Ct)).IsSuccessStatusCode);
            Assert.True((await admin.PutAsJsonAsync($"{path}/workflows/builtIns/paperdotnet.storageoptimization.photoToDocument", new { enabled = false, parameters = new { approvers = new[] { "checker" } } }, Ct)).IsSuccessStatusCode);
            reviewer = await ApiClient.CreateAsync(factory, tenant.Identifier, "checker", "checker-password-123");
        }
        var (_, approval) = await LaunchAsync(writer, ws, list, path, ids, ids[0], reviewer);
        Assert.True((await (await reviewer.GetAsync($"/v1.0/me/approvals/{approval}/review", Ct)).ReadJsonAsync()).GetProperty("canDecide").GetBoolean());
        if (revoked == "launcher")
            Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = writerId, role = "visitor" }, Ct)).IsSuccessStatusCode);
        else
        {
            Assert.True((await admin.PostAsJsonAsync($"{path}/items/{ids[2]}/permissions/breakInheritance", new { copyGrants = false }, Ct)).IsSuccessStatusCode);
            Assert.True((await admin.PutAsJsonAsync($"{path}/items/{ids[2]}/permissions/grants", new { grants = new[] { new { principalType = "user", principalId = writerId, level = "contribute" } } }, Ct)).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync($"/v1.0/me/approvals/{approval}/review/content/candidate", Ct)).StatusCode);
        }
        Assert.False((await reviewer.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome = "approved" }, Ct)).IsSuccessStatusCode);
        foreach (var id in ids) Assert.Equal("image/png", (await admin.GetAsync($"{path}/items/{id}/file", Ct)).Content.Headers.ContentType?.MediaType);
    }

    private sealed class FailingRecycle(IItemBatchRecycle inner) : IItemBatchRecycle
    {
        public async Task RecycleAsync(IReadOnlyList<ItemRecycleTarget> targets, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await inner.RecycleAsync([targets.First(t => t.Recycle)], transaction, cancellationToken);
            throw new InvalidOperationException("injected failure after recycling the first source");
        }
        public Task AnnounceAsync(IReadOnlyList<ItemRecycleTarget> targets, CancellationToken cancellationToken) => inner.AnnounceAsync(targets, cancellationToken);
    }

    private static byte[] DocumentPdf()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        return builder.Build();
    }
}
