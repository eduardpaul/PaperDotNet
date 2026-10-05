using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Data;
using PaperDotNet.Workflows.Contracts;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

public sealed class StagedDocumentContractTests(PaperDotNetApiFactory factory)
{
    [Fact]
    public async Task Temporary_documents_publish_without_a_selection_or_enabled_extension()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await factory.CreateTenantAsync("composition-contract");
        using var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        var ws = await client.CreateWorkspaceAsync("Compositions");
        var library = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Sources", templateKey = "documents" }, ct);
        var list = (await library.ReadJsonAsync()).GetProperty("id").GetGuid();
        using var upload = new MultipartFormDataContent { { new ByteArrayContent(StorageOptimizationTests.ReceiptImage()), "file", "source.png" } };
        var uploaded = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", upload, ct);
        var itemId = (await uploaded.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var scopes = factory.Services.GetRequiredService<ITenantScopeFactory>();
        Guid actor;
        await using (var scope = scopes.CreateScope(tenant.Id, tenant.Identifier))
            actor = (await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().FileVersions.SingleAsync(v => v.ItemId == itemId, ct)).CreatedBy!.Value;
        await using var actorScope = scopes.CreateScope(tenant.Id, tenant.Identifier, actor);
        var files = actorScope.ServiceProvider.GetRequiredService<IDocumentFileStore>();
        var file = await files.GetCurrentAsync(itemId, ct);
        var store = actorScope.ServiceProvider.GetRequiredService<IStagedDocumentStore>();
        var publisher = actorScope.ServiceProvider.GetRequiredService<IDocumentPublisher>();
        var texts = new[] { "", "", "" };
        var builder = new PdfDocumentBuilder();
        foreach (var _ in texts) builder.AddPage(PageSize.A4);
        using var pdf = new MemoryStream(builder.Build());
        var candidate = await store.CreateAsync(Guid.CreateVersion7(), "contract-test", "result.pdf", "eng", ct);
        candidate = await store.StageAsync(candidate.Id, pdf, texts, ct);
        Assert.Equal(3, candidate.PageCount);
        var transactions = actorScope.ServiceProvider.GetRequiredService<ListsDbContext>();
        // Rollback after saving a file version, then retry using the very same service scope.
        await using (var transaction = await transactions.Database.BeginTransactionAsync(ct))
        {
            await publisher.PublishAsync(candidate.Id, file!, "processing", transaction.GetDbTransaction(), ct);
            await transaction.RollbackAsync(ct);
        }
        Assert.Equal(file!.Id, (await files.GetCurrentAsync(itemId, ct))!.Id);
        await using (var transaction = await transactions.Database.BeginTransactionAsync(ct))
        {
            await publisher.PublishAsync(candidate.Id, file!, "processing", transaction.GetDbTransaction(), ct);
            await transaction.CommitAsync(ct);
        }
        await publisher.AnnounceAsync(candidate.Id, ct);
        var composed = await files.GetCurrentAsync(itemId, ct);
        Assert.NotNull(composed);
        Assert.Equal("processing", composed.Source);
        var db = actorScope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        Assert.Equal(3, (await db.FileVersions.SingleAsync(v => v.Id == candidate.Id, ct)).PageCount);
        Assert.Equal(2, await db.FileVersions.CountAsync(v => v.ItemId == itemId, ct));
        Assert.Equal(texts, await db.Pages.OrderBy(p => p.PageNumber).Select(p => p.Text).ToArrayAsync(ct));
        Assert.Equal("text", (await ReadTextAsync(actorScope.ServiceProvider, ws, list, itemId, ct)).Outcome);
        var unprepared = await store.CreateAsync(Guid.CreateVersion7(), "contract-test", "unprocessed.pdf", "eng", ct);
        pdf.Position = 0;
        unprepared = await store.StageAsync(unprepared.Id, pdf, null, ct);
        Assert.Equal(3, unprepared.PageCount); // Output pages do not depend on having recognized text.
        await using (var transaction = await transactions.Database.BeginTransactionAsync(ct))
        {
            await publisher.PublishAsync(unprepared.Id, composed, "processing", transaction.GetDbTransaction(), ct);
            await transaction.CommitAsync(ct);
        }
        Assert.Equal("noText", (await ReadTextAsync(actorScope.ServiceProvider, ws, list, itemId, ct)).Outcome);
        Assert.Equal("noText", (await ReadTextAsync(actorScope.ServiceProvider, ws, list, itemId, ct)).Outcome);
    }

    [Fact]
    public async Task Temporary_image_output_is_immutable_private_and_released_without_changing_live_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = await factory.CreateTenantAsync("temporary-image");
        using var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        var ws = await client.CreateWorkspaceAsync("Temporary output");
        var listResponse = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Images", templateKey = "documents" }, ct);
        var list = (await listResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        var original = StorageOptimizationTests.ReceiptImage("ORIGINAL 4711");
        using var upload = new MultipartFormDataContent { { new ByteArrayContent(original), "file", "source.png" } };
        var response = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", upload, ct);
        var itemId = (await response.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var scopes = factory.Services.GetRequiredService<ITenantScopeFactory>();
        Guid actor;
        await using (var scope = scopes.CreateScope(tenant.Id, tenant.Identifier))
            actor = (await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().FileVersions.SingleAsync(v => v.ItemId == itemId, ct)).CreatedBy!.Value;
        await using var owner = scopes.CreateScope(tenant.Id, tenant.Identifier, actor);
        var staging = owner.ServiceProvider.GetRequiredService<IStagedDocumentStore>();
        var id = Guid.CreateVersion7();
        await staging.CreateAsync(id, "any-processing-extension", "output.png", null, ct);
        var output = StorageOptimizationTests.ReceiptImage("OUTPUT 9999");
        using (var bytes = new MemoryStream(output))
        {
            var staged = await staging.StageAsync(id, bytes, null, ct);
            Assert.Equal("image/png", staged.MediaType);
            Assert.Equal(output.Length, staged.Size);
        }
        using (var replay = new MemoryStream(original)) await staging.StageAsync(id, replay, null, ct);
        await using (var content = await staging.OpenAsync(id, ct))
        {
            Assert.NotNull(content);
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes, ct);
            Assert.Equal(output, bytes.ToArray());
        }
        await using (var stranger = scopes.CreateScope(tenant.Id, tenant.Identifier, Guid.CreateVersion7()))
        {
            var other = stranger.ServiceProvider.GetRequiredService<IStagedDocumentStore>();
            Assert.Null(await other.GetAsync(id, ct));
            Assert.Null(await other.OpenAsync(id, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.CreateAsync(id, "any-processing-extension", "output.png", null, ct));
        }
        await staging.ReleaseAsync(id, ct);
        Assert.Null(await staging.OpenAsync(id, ct));
        Assert.Equal(original, await client.GetByteArrayAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{itemId}/file", ct));
        Assert.Equal(1, await owner.ServiceProvider.GetRequiredService<DocumentsDbContext>().FileVersions.CountAsync(v => v.ItemId == itemId, ct));
        var unprocessed = Guid.CreateVersion7();
        await staging.CreateAsync(unprocessed, "any-processing-extension", "output.png", null, ct);
        using (var bytes = new MemoryStream(output)) await staging.StageAsync(unprocessed, bytes, null, ct);
        var files = owner.ServiceProvider.GetRequiredService<IDocumentFileStore>();
        var expected = await files.GetCurrentAsync(itemId, ct);
        var publisher = owner.ServiceProvider.GetRequiredService<IDocumentPublisher>();
        await using (var transaction = await owner.ServiceProvider.GetRequiredService<ListsDbContext>().Database.BeginTransactionAsync(ct))
        {
            await publisher.PublishAsync(unprocessed, expected!, "processing", transaction.GetDbTransaction(), ct);
            await transaction.CommitAsync(ct);
        }
        // Staging an image without text must not tell later workflows to skip recognition.
        Assert.Equal("noText", (await ReadTextAsync(owner.ServiceProvider, ws, list, itemId, ct)).Outcome);
    }

    private static Task<WorkflowActivityResult> ReadTextAsync(IServiceProvider services, Guid workspace, Guid list, Guid item, CancellationToken ct) =>
        services.GetServices<IWorkflowActivity>().Single(a => a.Key == "document.readText").ExecuteAsync(new WorkflowActivityContext
        {
            WorkspaceId = workspace, Item = new(workspace, list, item), Inputs = [], Services = services,
            Source = "contract-test", ExecutionKey = "contract-test", ExecutionId = Guid.CreateVersion7(),
            ExpandAsync = (value, _) => Task.FromResult(value), ResolveAsync = (_, _) => Task.FromResult<JsonNode?>(null),
        }, ct);
}
