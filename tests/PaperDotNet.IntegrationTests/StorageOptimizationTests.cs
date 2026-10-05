using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Features;
using PaperDotNet.StorageOptimization;
using PaperDotNet.Tenancy.Contracts;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

#pragma warning disable CA1416 // PDFium supports Linux, Windows and macOS, where these tests run.

namespace PaperDotNet.IntegrationTests;

public sealed class StorageOptimizationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Extension = StorageOptimizationExtension.Id;

    internal static byte[] ReceiptImage(string text = "RECEIPT TOTAL 123.45")
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < 10 && text.Length > 0; i++)
        {
            page.AddText(text, 30, new PdfPoint(30, 740 - i * 60), font);
        }

        using var output = new MemoryStream();
        PDFtoImage.Conversion.SavePng(output, builder.Build(), 0, password: null, new PDFtoImage.RenderOptions { Dpi = 150 });
        var encoded = output.ToArray();
        var bytes = new byte[encoded.Length + 100_000];
        encoded.CopyTo(bytes, 0);
        return bytes;
    }

    private async Task<(TenantSummary Tenant, HttpClient Client, Guid Workspace, Guid Library, string Path)> SetupAsync(string identifier, PaperDotNetApiFactory? host = null)
    {
        host ??= factory;
        var tenant = await host.CreateTenantAsync(identifier);
        var client = await ApiClient.CreateAsync(host, identifier);
        Assert.True((await client.PostAsync($"/v1.0/extensions/{Extension}/enable", null, Ct)).IsSuccessStatusCode);
        var ws = await client.CreateWorkspaceAsync("Receipts");
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Photos", templateKey = "documents" }, Ct);
        var list = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        var path = $"/v1.0/workspaces/{ws}/lists/{list}";
        var enabled = await client.PutAsJsonAsync($"{path}/workflows/builtIns/{Extension}.optimize", new { enabled = true }, Ct);
        Assert.True(enabled.IsSuccessStatusCode, await enabled.Content.ReadAsStringAsync(Ct));
        return (tenant, client, ws, list, path);
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string path, int? byteSize = null)
    {
        var content = ReceiptImage();
        if (byteSize is { } size)
        {
            Array.Resize(ref content, size);
        }

        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", "receipt.png" } };
        var response = await client.PostAsync($"{path}/documents", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("itemId").GetGuid();
    }

    private static async Task<JsonElement> ApprovalAsync(HttpClient client, Guid itemId) =>
        await Eventually.WaitForAsync(async () =>
        {
            var response = await client.GetAsync("/v1.0/me/approvals", Ct);
            var approval = (await response.ReadJsonAsync()).GetProperty("value").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("itemId").GetGuid() == itemId);
            return approval.ValueKind == JsonValueKind.Object ? approval : (JsonElement?)null;
        }, TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Manual_launch_works_in_any_library_without_enabling_automatic_runs()
    {
        const string identifier = "storage-opt-manual";
        await factory.CreateTenantAsync(identifier);
        using var admin = await ApiClient.CreateAsync(factory, identifier);
        Assert.True((await admin.PostAsync($"/v1.0/extensions/{Extension}/enable", null, Ct)).IsSuccessStatusCode);
        var ws = await admin.CreateWorkspaceAsync("Manual files");
        var userResponse = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "writer", password = "writer-password-1" }, Ct);
        var writerId = (await userResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = writerId, role = "member" }, Ct)).IsSuccessStatusCode);
        using var writer = await ApiClient.CreateAsync(factory, identifier, "writer", "writer-password-1");
        var visitorResponse = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "reader", password = "reader-password-1" }, Ct);
        var visitorId = (await visitorResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = visitorId, role = "visitor" }, Ct)).IsSuccessStatusCode);
        using var visitor = await ApiClient.CreateAsync(factory, identifier, "reader", "reader-password-1");
        foreach (var name in new[] { "Never enabled", "Turned off", "Automatic" })
        {
            var created = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey = "documents" }, Ct);
            var listId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
            var path = $"/v1.0/workspaces/{ws}/lists/{listId}";
            var builtIns = $"{path}/workflows/builtIns";
            Guid? existingId = null;
            if (name != "Never enabled")
            {
                var settings = await admin.PutAsJsonAsync($"{builtIns}/{Extension}.optimize", new { enabled = true, parameters = new { targetHeight = 14 } }, Ct);
                Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
                existingId = (await settings.ReadJsonAsync()).GetProperty("workflowId").GetGuid();
                if (name == "Turned off")
                {
                    var current = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/{existingId}", Ct);
                    using var off = new HttpRequestMessage(HttpMethod.Put, $"{builtIns}/{Extension}.optimize") { Content = JsonContent.Create(new { enabled = false }) };
                    off.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.ToString());
                    Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(off, Ct)).StatusCode);
                }
            }
            var catalog = (await (await writer.GetAsync(builtIns, Ct)).ReadJsonAsync()).EnumerateArray().Single(b => b.GetProperty("key").GetString() == Extension + ".optimize");
            Assert.True(catalog.GetProperty("allowManualLaunch").GetBoolean());
            Assert.Equal(name == "Automatic", catalog.GetProperty("enabled").GetBoolean());
            var item = await UploadAsync(writer, path);
            var launchUrl = $"{builtIns}/{Extension}.optimize/runs";
            Assert.Equal(HttpStatusCode.Forbidden, (await visitor.PostAsJsonAsync(launchUrl, new { itemIds = new[] { item } }, Ct)).StatusCode);
            var invalid = await writer.PostAsJsonAsync(launchUrl, new { itemIds = new[] { item, Guid.CreateVersion7() } }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
            var launched = await writer.PostAsJsonAsync(launchUrl, new { itemIds = new[] { item } }, Ct);
            Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
            var run = (await launched.ReadJsonAsync())[0];
            var workflowId = run.GetProperty("workflowId").GetGuid();
            if (existingId is { } expected)
            {
                Assert.Equal(expected, workflowId);
            }
            var workflow = await (await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/{workflowId}", Ct)).ReadJsonAsync();
            Assert.Equal(name == "Automatic", workflow.GetProperty("enabled").GetBoolean());
            Assert.Equal(name == "Never enabled" ? 12 : 14, workflow.GetProperty("flow").GetProperty("nodes").GetProperty("prepare").GetProperty("inputs").GetProperty("targetHeight").GetInt32());
            var approval = await ApprovalAsync(admin, item);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/v1.0/me/approvals/{approval.GetProperty("id").GetGuid()}/decision", new { outcome = "rejected" }, Ct)).StatusCode);
            // Existing name/id APIs also honor the manual policy for off built-ins.
            var again = await writer.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows/{workflowId}/runs", new { listId, itemIds = new[] { item } }, Ct);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await writer.PostAsJsonAsync($"{builtIns}/documents.ocr/runs", new { itemIds = new[] { item } }, Ct)).StatusCode);
        }
        var home = await (await writer.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync();
        var inbox = home.GetProperty("inboxListId").GetGuid();
        var homeWs = home.GetProperty("workspaceId").GetGuid();
        var inboxPath = $"/v1.0/workspaces/{homeWs}/lists/{inbox}";
        var inboxItem = await UploadAsync(writer, inboxPath);
        var inboxLaunch = $"{inboxPath}/workflows/builtIns/{Extension}.optimize/runs";
        var inboxRunResponse = await writer.PostAsJsonAsync(inboxLaunch, new { itemIds = new[] { inboxItem } }, Ct);
        Assert.Equal(HttpStatusCode.OK, inboxRunResponse.StatusCode);
        var inboxWorkflowId = (await inboxRunResponse.ReadJsonAsync())[0].GetProperty("workflowId").GetGuid();
        await factory.CreateTenantAsync("storage-opt-manual-foreign");
        using var foreign = await ApiClient.CreateAsync(factory, "storage-opt-manual-foreign");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync(inboxLaunch, new { itemIds = new[] { inboxItem } }, Ct)).StatusCode);
        Assert.True((await admin.PostAsync($"/v1.0/extensions/{Extension}/disable", null, Ct)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await writer.PostAsJsonAsync(inboxLaunch, new { itemIds = new[] { inboxItem } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await writer.PostAsJsonAsync($"/v1.0/workspaces/{homeWs}/workflows/{inboxWorkflowId}/runs", new { listId = inbox, itemIds = new[] { inboxItem } }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("approved", "image/webp")]
    [InlineData("rejected", "image/png")]
    public async Task Image_review_collects_form_values_before_resuming(string outcome, string mediaType)
    {
        var (_, client, ws, _, path) = await SetupAsync($"storage-opt-form-{outcome}");
        var builtIns = (await (await client.GetAsync($"{path}/workflows/builtIns", Ct)).ReadJsonAsync()).EnumerateArray();
        var builtInId = builtIns.Single(b => b.GetProperty("key").GetString() == Extension + ".optimize").GetProperty("workflowId").GetGuid();
        var current = await client.GetAsync($"/v1.0/workspaces/{ws}/workflows/{builtInId}", Ct);
        var definition = JsonNode.Parse(await current.Content.ReadAsStringAsync(Ct))!.AsObject();
        var disabled = await client.SendWithEtagAsync(HttpMethod.Put, $"{path}/workflows/builtIns/{Extension}.optimize", current.Headers.ETag!.Tag, new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var flow = definition["flow"]!.DeepClone().AsObject();
        flow["nodes"]!["review"]!["inputs"]!["inputSchema"] = JsonNode.Parse("""
            { "type": "object", "properties": {
              "reason": { "type": "string", "minLength": 3 },
              "checked": { "type": "boolean", "default": true }
            }, "required": ["reason"] }
            """);
        var created = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows", new
        {
            name = "Review with information",
            trigger = new { type = "document.added", list = "Photos", data = new { source = "upload" } },
            flow,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await UploadAsync(client, path);
        var approval = await ApprovalAsync(client, item);
        Assert.Equal(StorageOptimizationExtension.ReviewType, approval.GetProperty("review").GetProperty("type").GetString());
        Assert.True(approval.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("reason", out _));
        var id = approval.GetProperty("id").GetGuid();
        var decision = $"/v1.0/me/approvals/{id}/decision";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(decision, new { outcome }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(decision, new { outcome, inputs = new { reason = "no" } }, Ct)).StatusCode);
        Assert.Equal(id, (await ApprovalAsync(client, item)).GetProperty("id").GetGuid());
        Assert.True((await (await client.GetAsync($"/v1.0/me/approvals/{id}/review", Ct)).ReadJsonAsync()).GetProperty("canDecide").GetBoolean());
        var accepted = await client.PostAsJsonAsync(decision, new { outcome, inputs = new { reason = "Legible" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var submitted = (await accepted.ReadJsonAsync()).GetProperty("inputs");
        Assert.Equal("Legible", submitted.GetProperty("reason").GetString());
        Assert.True(submitted.GetProperty("checked").GetBoolean());
        var runId = approval.GetProperty("runId").GetGuid();
        var run = await Eventually.WaitForAsync(async () =>
        {
            var value = await (await client.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs/{runId}", Ct)).ReadJsonAsync();
            return value.GetProperty("status").GetString() == "completed" ? value : (JsonElement?)null;
        }, TimeSpan.FromSeconds(30));
        Assert.Equal("Legible", run.GetProperty("outputs").GetProperty("review").GetProperty("input").GetProperty("reason").GetString());
        Assert.Equal(outcome, run.GetProperty("outputs").GetProperty("review").GetProperty("outcome").GetString());
        Assert.Equal(mediaType, (await client.GetAsync($"{path}/items/{item}/file", Ct)).Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Tesseract_switch_still_prepares_word_based_review()
    {
        await using var host = new PaperDotNetApiFactory { OptimizationTextDetector = "tesseract" };
        await host.InitializeAsync();
        var (_, client, _, _, path) = await SetupAsync("storage-opt-tesseract", host);
        var item = await UploadAsync(client, path);
        var approval = await ApprovalAsync(client, item);
        var review = await client.GetAsync($"/v1.0/me/approvals/{approval.GetProperty("id").GetGuid()}/review", Ct);
        var data = (await review.ReadJsonAsync()).GetProperty("data");
        Assert.Equal("tesseract", data.GetProperty("detector").GetString());
        Assert.Equal("word", data.GetProperty("strategy").GetString());
    }

    [Fact]
    public async Task Approval_replaces_once_and_cleanup_preserves_shared_source_content()
    {
        var (tenant, client, ws, _, path) = await SetupAsync("storage-opt-approve");
        var item = await UploadAsync(client, path);
        var shared = await UploadAsync(client, path);
        var approval = await ApprovalAsync(client, item);
        var id = approval.GetProperty("id").GetGuid();
        var review = await client.GetAsync($"/v1.0/me/approvals/{id}/review", Ct);
        var metadata = await review.ReadJsonAsync();
        Assert.True(metadata.GetProperty("canDecide").GetBoolean());
        Assert.Equal("paddleocr", metadata.GetProperty("data").GetProperty("detector").GetString());
        Assert.Equal("PP-OCRv6_small_det", metadata.GetProperty("data").GetProperty("model").GetString());
        Assert.Equal("line", metadata.GetProperty("data").GetProperty("strategy").GetString());
        var source = await client.GetByteArrayAsync($"/v1.0/me/approvals/{id}/review/content/source", Ct);
        var optimized = await client.GetByteArrayAsync($"/v1.0/me/approvals/{id}/review/content/candidate", Ct);
        Assert.True(optimized.Length < source.Length);
        Assert.Equal("image/webp", (await client.GetAsync($"/v1.0/me/approvals/{id}/review/content/candidate", Ct)).Content.Headers.ContentType!.MediaType);
        var decision = await client.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "approved", comment = "Readable" }, Ct);
        Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "approved" }, Ct)).StatusCode);
        await Eventually.WaitForAsync(async () =>
        {
            var response = await client.GetAsync($"{path}/items/{item}/file", Ct);
            return response.Content.Headers.ContentType?.MediaType == "image/webp" ? true : (bool?)null;
        }, TimeSpan.FromSeconds(30));
        Assert.Equal(optimized, await client.GetByteArrayAsync($"{path}/items/{item}/file", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/items/{item}/file/versions/1", Ct)).StatusCode);
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var accepted = await db.Candidates.AsNoTracking().SingleAsync(c => c.ItemId == item && c.State == "accepted", Ct);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDocumentFileStore>().AsSystem().PromoteAsync(accepted.Id, Ct));
        await db.StoredFiles.ExecuteUpdateAsync(u => u.SetProperty(f => f.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await scope.ServiceProvider.GetRequiredService<StoredFileCleanupJob>().RunAsync(Ct);
        Assert.Equal(source, await client.GetByteArrayAsync($"{path}/items/{shared}/file", Ct));
        Assert.Single(await db.FileVersions.Where(v => v.ItemId == item).ToListAsync(Ct));
        Assert.Equal(ws, approval.GetProperty("workspaceId").GetGuid());
    }

    [Fact]
    public async Task Approved_unshared_source_is_reclaimed_and_pending_content_is_protected()
    {
        await using var host = new PaperDotNetApiFactory { UploadLimit = 100 * 1024 * 1024 };
        await host.InitializeAsync();
        var (tenant, client, _, _, path) = await SetupAsync("storage-opt-reclaim", host);
        var item = await UploadAsync(client, path, 48 * 1024 * 1024);
        var approval = await ApprovalAsync(client, item);
        var id = approval.GetProperty("id").GetGuid();
        await using var scope = host.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var source = await db.StoredFiles.SingleAsync(f => f.MediaType == "image/png", Ct);
        var sourceKey = source.BlobKey;
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        await db.StoredFiles.ExecuteUpdateAsync(u => u.SetProperty(f => f.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await db.Candidates.ExecuteUpdateAsync(u => u.SetProperty(c => c.CreatedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await scope.ServiceProvider.GetRequiredService<StoredFileCleanupJob>().RunAsync(Ct);
        Assert.True(await blobs.ExistsAsync(sourceKey, Ct));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1.0/me/approvals/{id}/review/content/candidate", Ct)).StatusCode);
        Assert.True((await client.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "approved" }, Ct)).IsSuccessStatusCode);
        await Eventually.WaitForAsync(async () => await db.Candidates.AsNoTracking().AnyAsync(c => c.ItemId == item && c.State == "accepted", Ct) ? true : (bool?)null,
            TimeSpan.FromSeconds(30));
        await db.StoredFiles.ExecuteUpdateAsync(u => u.SetProperty(f => f.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await scope.ServiceProvider.GetRequiredService<StoredFileCleanupJob>().RunAsync(Ct);
        Assert.False(await blobs.ExistsAsync(sourceKey, Ct));
        Assert.Single(await db.StoredFiles.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Multiple_group_overrides_include_nested_members_and_protect_assignment()
    {
        var (tenant, admin, ws, _, path) = await SetupAsync("storage-opt-groups");
        async Task<Guid> PostIdAsync(string url, object value) =>
            (await (await admin.PostAsJsonAsync(url, value, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var reviewer = await PostIdAsync("/v1.0/users", new { userName = "reviewer", password = "reviewer-password-1" });
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = reviewer, role = "member" }, Ct)).IsSuccessStatusCode);
        var parent = await PostIdAsync("/v1.0/groups", new { name = "Managers" });
        var nested = await PostIdAsync("/v1.0/groups", new { name = "Reviewers" });
        var empty = await PostIdAsync("/v1.0/groups", new { name = "Empty" });
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/groups/{parent}/groups", new { groupId = nested }, Ct)).IsSuccessStatusCode);
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/groups/{nested}/members", new { userId = reviewer }, Ct)).IsSuccessStatusCode);
        var builtIn = (await (await admin.GetAsync($"{path}/workflows/builtIns", Ct)).ReadJsonAsync()).EnumerateArray()
            .Single(w => w.GetProperty("key").GetString() == Extension + ".optimize");
        var workflowId = builtIn.GetProperty("workflowId").GetGuid();
        var current = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/{workflowId}", Ct);
        var settings = await admin.SendWithEtagAsync(HttpMethod.Put, $"{path}/workflows/builtIns/{Extension}.optimize", current.Headers.ETag!.Tag,
            new { enabled = true, parameters = new { approvers = new[] { "group:Managers", "group:Empty" } } });
        Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync(Ct));
        var item = await UploadAsync(admin, path);
        using var client = await ApiClient.CreateAsync(factory, "storage-opt-groups", "reviewer", "reviewer-password-1");
        var approval = await ApprovalAsync(client, item);
        var id = approval.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1.0/me/approvals/{id}/review", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1.0/me/approvals/{id}/review", Ct)).StatusCode);
        var notifications = (await (await client.GetAsync("/v1.0/me/notifications", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray();
        Assert.Contains(notifications, n => n.GetProperty("title").GetString()!.StartsWith("Review smaller file", StringComparison.Ordinal));
        var decisions = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "rejected" }, Ct)));
        Assert.Single(decisions, d => d.IsSuccessStatusCode);
        Assert.Single(decisions, d => d.StatusCode == HttpStatusCode.Conflict);

        current = await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/{workflowId}", Ct);
        settings = await admin.SendWithEtagAsync(HttpMethod.Put, $"{path}/workflows/builtIns/{Extension}.optimize", current.Headers.ETag!.Tag,
            new { enabled = true, parameters = new { approvers = new[] { "group:Empty" } } });
        Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync(Ct));
        var unreviewed = await UploadAsync(admin, path);
        var failed = await Eventually.WaitForAsync(async () =>
        {
            var runs = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?workflowId={workflowId}&itemId={unreviewed}", Ct)).ReadJsonAsync())
                .GetProperty("value").EnumerateArray();
            var run = runs.FirstOrDefault(r => r.GetProperty("status").GetString() == "failed");
            return run.ValueKind == JsonValueKind.Object ? run : (JsonElement?)null;
        }, TimeSpan.FromSeconds(60));
        Assert.Contains("No active managers", failed.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(ReceiptImage(), await admin.GetByteArrayAsync($"{path}/items/{unreviewed}/file", Ct));
        Assert.Empty((await (await admin.GetAsync("/v1.0/me/approvals", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray());

        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        await Eventually.WaitForAsync(async () => await db.Candidates.AsNoTracking().AnyAsync(c => c.ItemId == item && c.State == "rejected", Ct) ? true : (bool?)null);
        var abandoned = await db.Candidates.AsNoTracking().SingleAsync(c => c.ItemId == unreviewed, Ct);
        var staged = await db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == abandoned.StoredFileId, Ct);
        await db.Candidates.ExecuteUpdateAsync(u => u.SetProperty(c => c.CreatedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await db.StoredFiles.ExecuteUpdateAsync(u => u.SetProperty(f => f.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await scope.ServiceProvider.GetRequiredService<StoredFileCleanupJob>().RunAsync(Ct);
        Assert.False(await scope.ServiceProvider.GetRequiredService<IBlobStore>().ExistsAsync(staged.BlobKey, Ct));
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/groups/{empty}/members", new { userId = reviewer }, Ct)).IsSuccessStatusCode);
        Assert.True((await admin.PostAsync($"/v1.0/workspaces/{ws}/workflows/runs/{failed.GetProperty("id").GetGuid()}/retry", null, Ct)).IsSuccessStatusCode);
        var retried = await ApprovalAsync(client, unreviewed);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1.0/me/approvals/{retried.GetProperty("id").GetGuid()}/review/content/candidate", Ct)).StatusCode);
        Assert.Equal(abandoned.Id, (await db.Candidates.AsNoTracking().SingleAsync(c => c.ItemId == unreviewed, Ct)).Id);
    }

    [Fact]
    public async Task Rejection_keeps_the_source_and_reclaims_the_candidate()
    {
        var (tenant, client, _, _, path) = await SetupAsync("storage-opt-reject");
        var item = await UploadAsync(client, path);
        var approval = await ApprovalAsync(client, item);
        var id = approval.GetProperty("id").GetGuid();
        Assert.True((await client.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "rejected" }, Ct)).IsSuccessStatusCode);
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        await Eventually.WaitForAsync(async () => await db.Candidates.AsNoTracking().AnyAsync(c => c.ItemId == item && c.State == "rejected", Ct) ? true : (bool?)null,
            TimeSpan.FromSeconds(30));
        Assert.Equal(ReceiptImage(), await client.GetByteArrayAsync($"{path}/items/{item}/file", Ct));
        await db.StoredFiles.ExecuteUpdateAsync(u => u.SetProperty(f => f.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct);
        await scope.ServiceProvider.GetRequiredService<StoredFileCleanupJob>().RunAsync(Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1.0/me/approvals/{id}/review/content/candidate", Ct)).StatusCode);
        Assert.Single(await db.StoredFiles.ToListAsync(Ct));
    }

    [Fact]
    public async Task Reviews_are_tenant_and_assignment_scoped_and_disabled_extensions_cannot_decide()
    {
        var (_, client, _, _, path) = await SetupAsync("storage-opt-isolation");
        var item = await UploadAsync(client, path);
        var approval = await ApprovalAsync(client, item);
        var id = approval.GetProperty("id").GetGuid();
        await factory.CreateTenantAsync("storage-opt-foreign");
        using var foreign = await ApiClient.CreateAsync(factory, "storage-opt-foreign");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/v1.0/me/approvals/{id}/review", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/v1.0/me/approvals/{id}/review/content/source", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "approved" }, Ct)).StatusCode);
        Assert.True((await client.PostAsync($"/v1.0/extensions/{Extension}/disable", null, Ct)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1.0/me/approvals/{id}/review", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "approved" }, Ct)).StatusCode);
        Assert.Equal(ReceiptImage(), await client.GetByteArrayAsync($"{path}/items/{item}/file", Ct));
    }

    [Fact]
    public async Task A_new_upload_cancels_the_old_review_and_cannot_be_overwritten()
    {
        var (_, client, _, _, path) = await SetupAsync("storage-opt-stale");
        var item = await UploadAsync(client, path);
        var approval = await ApprovalAsync(client, item);
        var id = approval.GetProperty("id").GetGuid();
        using var replacement = new MultipartFormDataContent { { new ByteArrayContent(ReceiptImage("NEW RECEIPT 999.99")), "file", "new.png" } };
        var uploaded = await client.PutAsync($"{path}/items/{item}/file", replacement, Ct);
        Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/v1.0/me/approvals/{id}/decision", new { outcome = "approved" }, Ct)).StatusCode);
        Assert.Equal(ReceiptImage("NEW RECEIPT 999.99"), await client.GetByteArrayAsync($"{path}/items/{item}/file", Ct));
    }
}
