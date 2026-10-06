using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Search.Features;

internal static class SearchWorkflows
{
    public const string Index = "search.index";
    public const string Requested = "search.requested";
    public const string ReindexItem = "search.reindexItem";
    public const string RebuildRequested = "search.rebuildRequested";
    public const string ContainerChanged = "search.containerChanged";

    public static readonly BuiltInWorkflow IndexWorkflow = new(Index, "Index for search",
        "Indexes this list's items and file text, then embeds the chunks. Run on demand or automatically after changes.",
        JsonNode.Parse("""
            {
              "scope": "list",
              "concurrency": "replace",
              "triggers": [
                { "type": "itemAdded", "list": "{param:list}" },
                { "type": "itemUpdated", "list": "{param:list}" },
                { "type": "itemRestored", "list": "{param:list}" },
                { "type": "search.requested", "list": "{param:list}" },
                { "type": "search.reindexItem", "list": "{param:list}" },
                { "type": "wf.documents.text.hasText", "list": "{param:list}" },
                { "type": "wf.documents.text.noText", "list": "{param:list}" },
                { "type": "manual", "list": "{param:list}" }
              ],
              "flow": {
                "start": "chunk",
                "nodes": {
                  "chunk": { "activity": "search.chunk", "inputs": { "chunker": "{param:chunker}", "maxChars": "{param:maxChars}", "overlap": "{param:overlap}" }, "next": { "done": "publish" } },
                  "publish": { "activity": "search.publish", "next": { "done": "embed" } },
                  "embed": { "activity": "search.embed" }
                }
              }
            }
            """)!.AsObject())
    {
        Scope = BuiltInScope.List,
        EnabledByDefault = true,
        AllowManualLaunch = true,
        Parameters = JsonNode.Parse("""
            { "type": "object", "properties": {
              "chunker": { "type": "string", "enum": ["window", "pages", "whole"], "default": "window" },
              "maxChars": { "type": "integer", "minimum": 100, "maximum": 20000, "default": 1200 },
              "overlap": { "type": "integer", "minimum": 0, "maximum": 1000, "default": 150 }
            } }
            """)!.AsObject(),
    };

    public static readonly BuiltInWorkflow RebuildWorkflow = new("search.rebuild", "Rebuild search",
        "Rebuilds included lists through item workflows, with durable progress and visible failures.",
        JsonNode.Parse("""
            { "scope": "workspace", "triggers": [
                { "type": "search.rebuildRequested" }, { "type": "manual" },
                { "type": "schedule", "cron": "*/15 * * * *" }
              ], "flow": { "start": "rebuild", "nodes": { "rebuild": { "activity": "search.rebuild" } } } }
            """)!.AsObject()) { EnabledByDefault = true, AllowManualLaunch = true };

    public static readonly BuiltInWorkflow RemoveWorkflow = ItemChangeWorkflows.Create("search.remove", "Remove deleted items from search",
        "Removes the search records of deleted items; independent of automatic indexing.", "search.remove", WorkflowTriggers.ItemDeleted);

    public static readonly BuiltInWorkflow ContainerWorkflow = ItemChangeWorkflows.Create("search.containers", "Maintain library search",
        "Removes excluded or deleted libraries and requests indexing when they are included again.", "search.container", ContainerChanged);
}

/// <summary>Reads source-owned metadata and text, without depending on a search backend or search inclusion.</summary>
internal sealed class SearchInput(IEnumerable<ISearchItemSource> sources, IEnumerable<IItemTextSource> textSources)
{
    public async Task<SearchDocumentData?> ReadAsync(Guid id, CancellationToken ct)
    {
        SearchDocumentData? document = null;
        foreach (var source in sources)
        {
            document = await source.GetDocumentAsync(id, ct);
            if (document is not null) { break; }
        }
        if (document is null) { return null; }
        foreach (var source in textSources)
        {
            if ((await source.GetTextAsync([id], ct)).TryGetValue(id, out var text))
            {
                document = document with { Pages = text.Pages, Language = text.Language ?? document.Language, ContentRevision = text.Revision, ContentReady = text.Ready };
            }
        }
        return document;
    }

    public static string Revision(SearchDocumentData document) => Passages.Hash(JsonSerializer.Serialize(document));
}

internal sealed class SearchChunkActivity(SearchInput input, ISearchStore store, SearchDbContext db) : IWorkflowActivity
{
    public string Key => "search.chunk";
    public string Description => "Stages current item metadata and text as chunks without changing the published index.";
    public JsonObject? InputSchema => SearchWorkflows.IndexWorkflow.Parameters;
    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("chunks", ActivitySchemas.Number("Chunks staged.")),
        ("truncated", ActivitySchemas.Boolean("Whether indexing reached its chunk limit.")));

    public IEnumerable<string> Validate(JsonObject inputs)
    {
        var chunker = ActivityInputs.Text(inputs, "chunker");
        if (chunker is not null && !chunker.Contains('{', StringComparison.Ordinal) && chunker is not ("window" or "pages" or "whole"))
        { yield return "chunker must be window, pages or whole."; }
        var max = ActivityInputs.Number(inputs, "maxChars") ?? Passages.MaxChars;
        var overlap = ActivityInputs.Number(inputs, "overlap") ?? Passages.Overlap;
        if (max < 100 || max > 20000 || overlap < 0 || overlap >= max) { yield return "maxChars must be 100–20000 and overlap must be smaller than maxChars."; }
    }

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.Item is not { } item || context.RunId is not { } generation) { return WorkflowActivityResult.Fail("Indexing requires an item workflow run."); }
        if (await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == item.ListId, ct) is { Included: false })
        { return WorkflowActivityResult.Fail("This library is excluded from search."); }
        var document = await input.ReadAsync(item.ItemId, ct);
        if (document is null || document.WorkspaceId != item.WorkspaceId || document.ContainerId != item.ListId)
        { return WorkflowActivityResult.Fail("The item no longer exists at this location."); }

        var chunker = ActivityInputs.Text(context.Inputs, "chunker") ?? "window";
        var max = (int)(ActivityInputs.Number(context.Inputs, "maxChars") ?? Passages.MaxChars);
        var overlap = (int)(ActivityInputs.Number(context.Inputs, "overlap") ?? Passages.Overlap);
        if (Validate(context.Inputs).FirstOrDefault() is { } error) { return WorkflowActivityResult.Fail(error); }
        var chunks = Chunk(document, chunker, max, overlap).Take(Passages.MaxPerDocument + 1).ToList();
        var truncated = chunks.Count > Passages.MaxPerDocument;
        if (truncated) { chunks.RemoveAt(chunks.Count - 1); }
        var revision = SearchInput.Revision(document);
        var policy = await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == item.ListId, ct);
        await store.StageAsync(new SearchGeneration(generation, document with { Pages = [], Chunks = chunks }, revision, policy?.Version ?? 0), ct);
        var publication = await db.Publications.FirstOrDefaultAsync(p => p.Id == item.ItemId, ct);
        if (publication is null)
        {
            publication = new SearchPublication { Id = item.ItemId, WorkspaceId = item.WorkspaceId, ContainerId = item.ListId };
            db.Publications.Add(publication);
        }
        var previousGeneration = publication.GenerationId;
        publication.GenerationId = generation;
        publication.Truncated = truncated;
        await db.SaveChangesAsync(ct);
        if (previousGeneration != Guid.Empty && previousGeneration != generation)
        {
            await store.DeleteStageAsync(previousGeneration, ct);
        }
        return WorkflowActivityResult.Ok(new JsonObject { ["generation"] = generation.ToString(), ["revision"] = revision, ["chunks"] = chunks.Count, ["truncated"] = truncated });
    }

    private static IEnumerable<PassageText> Chunk(SearchDocumentData document, string chunker, int max, int overlap)
    {
        var texts = new List<PassageText> { new(null, $"{document.Keywords}\n{document.Body}") };
        texts.AddRange(document.Pages.Select((text, i) => new PassageText(i + 1, text)));
        if (texts.All(t => string.IsNullOrWhiteSpace(t.Text))) { texts = [new(null, document.Title)]; }
        if (chunker == "whole") { yield return new PassageText(document.Pages.Count > 0 ? 1 : null, string.Join('\n', texts.Select(t => t.Text))); yield break; }
        foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
        {
            if (chunker == "pages") { yield return text; }
            else
            {
                foreach (var window in Passages.Windows(Passages.Normalize(text.Text), max, overlap)) { yield return new PassageText(text.Page, window); }
            }
        }
    }
}

internal sealed class SearchPublishActivity(SearchInput input, ISearchStore store, SearchDbContext db, TimeProvider time) : IWorkflowActivity
{
    public string Key => "search.publish";
    public string Description => "Publishes a staged generation if the item and library policy still match.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.RunId is not { } id) { return WorkflowActivityResult.Fail("Publication requires a workflow run."); }
        var stage = await store.GetStageAsync(id, ct);
        if (stage is null)
        {
            // The store commit may have succeeded before the engine saved this node's output.
            var published = await db.Publications.AsNoTracking().FirstOrDefaultAsync(p => p.RunId == id && p.Revision != null, ct);
            return published is null ? WorkflowActivityResult.Fail("There is no staged generation. Run search.chunk first.")
                : WorkflowActivityResult.Ok(new JsonObject { ["indexed"] = true, ["revision"] = published.Revision, ["chunks"] = published.Chunks });
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Updating the current generation locks this item's publication on both database providers.
        var claimed = await db.Publications.Where(p => p.Id == stage.Document.Id && p.GenerationId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.RunId, context.RunId), ct);
        var policy = await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == stage.Document.ContainerId, ct);
        var current = await input.ReadAsync(stage.Document.Id, ct);
        if (claimed == 0 || policy is { Included: false } || (policy?.Version ?? 0) != stage.PolicyVersion
            || current is null || SearchInput.Revision(current) != stage.Revision)
        {
            await transaction.RollbackAsync(ct);
            await store.DeleteStageAsync(id, ct);
            return WorkflowActivityResult.Fail("The item or search policy changed. Index the current item again.");
        }
        await store.UpsertAsync([stage.Document], ct);
        await db.Publications.Where(p => p.Id == stage.Document.Id && p.GenerationId == id).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Revision, stage.Revision).SetProperty(p => p.PublishedAt, time.GetUtcNow())
            .SetProperty(p => p.Chunks, stage.Document.Chunks.Count).SetProperty(p => p.EmbeddingModel, (string?)null), ct);
        await transaction.CommitAsync(ct);
        await store.DeleteStageAsync(id, ct);
        return WorkflowActivityResult.Ok(new JsonObject { ["indexed"] = true, ["revision"] = stage.Revision, ["chunks"] = stage.Document.Chunks.Count });
    }
}

internal sealed class SearchEmbedActivity(ISearchStore store, EmbeddingModel embeddings, IOptions<SearchOptions> options, SearchDbContext db) : IWorkflowActivity
{
    public string Key => "search.embed";
    public string Description => "Embeds this item's published chunks; provider failures remain visible and retryable in the run.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.Item is not { } item) { return WorkflowActivityResult.Fail("Embedding requires an item."); }
        if (await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == item.ListId && !p.Included, ct))
        { return WorkflowActivityResult.Fail("This library is excluded from search."); }
        if (!embeddings.Enabled || !store.Capabilities.HasFlag(SearchStoreCapabilities.Vector))
        { return WorkflowActivityResult.Ok(new JsonObject { ["enabled"] = false }); }
        var model = embeddings.ModelKey;
        var count = 0;
        while (true)
        {
            var batch = await store.GetPassagesToEmbedAsync(item.ItemId, model, Math.Clamp(options.Value.EmbeddingBatchSize, 1, 2048), ct);
            if (batch.Count == 0) { break; }
            Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>> generated;
            try { generated = await embeddings.Generator!.GenerateAsync(batch.Select(p => p.Input), cancellationToken: ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { return WorkflowActivityResult.Fail($"Embedding provider failed: {ex.Message}"); }
            if (generated.Count != batch.Count) { return WorkflowActivityResult.Fail("The embedding provider returned an incomplete batch."); }
            await store.SetEmbeddingsAsync(model, batch.Zip(generated).Select(p => new PassageEmbedding(p.First.Id, p.Second.Vector)).ToList(), ct);
            count += batch.Count;
        }
        await db.Publications.Where(p => p.Id == item.ItemId && p.RunId == context.RunId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.EmbeddingModel, model), ct);
        return WorkflowActivityResult.Ok(new JsonObject { ["enabled"] = true, ["model"] = model, ["embedded"] = count });
    }
}

internal sealed class SearchRemoveActivity(ISearchStore store, SearchDbContext db, IListItemStore items) : IWorkflowActivity
{
    public string Key => "search.remove";
    public string Description => "Removes deleted items from search without allowing a delayed deletion to remove a restored item.";
    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.Item is not { } item) { return WorkflowActivityResult.Fail("Removal requires an item."); }
        if (await items.AsSystem().GetByIdAsync(item.ItemId, ct) is null)
        {
            await store.DeleteAsync([item.ItemId], ct);
            await db.Publications.Where(p => p.Id == item.ItemId).ExecuteDeleteAsync(ct);
        }
        return WorkflowActivityResult.Ok();
    }
}

internal sealed class SearchContainerActivity(ISearchStore store, SearchDbContext db, IListItemStore items, IWorkflowTriggers triggers) : IWorkflowActivity
{
    public string Key => "search.container";
    public string Description => "Applies library inclusion changes, with bounded indexing requests and idempotent removal.";
    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Data?["containerId"]?.GetValue<string>(), out var id)) { return WorkflowActivityResult.Fail("containerId is required."); }
        var list = await items.AsSystem().GetListAsync(context.WorkspaceId, id, ct);
        if (list is null || await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == id && !p.Included, ct))
        {
            await store.DeleteContainerAsync(id, ct);
            await db.Publications.Where(p => p.ContainerId == id).ExecuteDeleteAsync(ct);
        }
        else
        {
            string? cursor = null;
            do
            {
                var (page, error) = await items.AsSystem().QueryPageAsync(list.WorkspaceId, id, new ListItemQuery(Top: 200, SkipToken: cursor), ct);
                if (error is not null) { return WorkflowActivityResult.Fail(error); }
                foreach (var item in page!.Items)
                {
                    await triggers.RaiseAsync(SearchWorkflows.ReindexItem, item.WorkspaceId, new WorkflowItem(item.WorkspaceId, item.ListId, item.Id), null, ct);
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        return WorkflowActivityResult.Ok();
    }
}
