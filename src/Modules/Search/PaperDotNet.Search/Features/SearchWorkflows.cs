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

    /// <summary>A rebuild asks for an item's indexing and waits for it (also when automatic indexing is off).</summary>
    public const string ReindexItem = "search.reindexItem";

    /// <summary>A list included again asks for its items' indexing (also when automatic indexing is off); nobody waits.</summary>
    public const string IncludeItem = "search.includeItem";

    public const string RebuildRequested = "search.rebuildRequested";

    /// <summary>Wait kinds of the completions of <see cref="ReindexItem"/> and <see cref="RebuildRequested"/>.</summary>
    public const string IndexRequestKind = "search.indexRequest";
    public const string RebuildRequestKind = "search.rebuildRequest";

    /// <summary>The outcome of a step that had nothing to do (the list is excluded, the item is gone or changed meanwhile).</summary>
    public const string Skipped = "skipped";

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
                { "type": "search.includeItem", "list": "{param:list}" },
                { "type": "wf.documents.text.hasText", "list": "{param:list}" },
                { "type": "wf.documents.text.noText", "list": "{param:list}" },
                { "type": "manual", "list": "{param:list}" }
              ],
              "flow": {
                "start": "chunk",
                "nodes": {
                  "chunk": { "activity": "search.chunk", "inputs": { "chunker": "{param:chunker}", "maxChars": "{param:maxChars}", "overlap": "{param:overlap}" }, "next": { "done": "publish", "skipped": "end" } },
                  "publish": { "activity": "search.publish", "next": { "done": "embed", "skipped": "end" } },
                  "embed": { "activity": "search.embed" },
                  "end": { "activity": "end" }
                }
              }
            }
            """)!.AsObject())
    {
        Scope = BuiltInScope.List,
        EnabledByDefault = true,
        AllowManualLaunch = true,
        System = true,
        Parameters = JsonNode.Parse("""
            { "type": "object", "properties": {
              "chunker": { "type": "string", "enum": ["window", "pages", "whole"], "default": "window" },
              "maxChars": { "type": "integer", "minimum": 100, "maximum": 20000, "default": 1200 },
              "overlap": { "type": "integer", "minimum": 0, "maximum": 1000, "default": 150 }
            } }
            """)!.AsObject(),
    };

    public static readonly BuiltInWorkflow RebuildWorkflow = new("search.rebuild", "Rebuild search",
        "Rebuilds included lists through item workflows: on request every item, on its schedule only items whose index is stale.",
        JsonNode.Parse("""
            { "scope": "workspace", "triggers": [
                { "type": "search.rebuildRequested" }, { "type": "manual" },
                { "type": "schedule", "cron": "*/15 * * * *", "concurrency": "skip" }
              ], "flow": { "start": "rebuild", "nodes": { "rebuild": { "activity": "search.rebuild" } } } }
            """)!.AsObject())
    { EnabledByDefault = true, AllowManualLaunch = true, System = true };

    public static readonly BuiltInWorkflow RemoveWorkflow = ItemChangeWorkflows.Create("search.remove", "Remove deleted items from search",
        "Removes the search records of deleted items; independent of automatic indexing.", "search.remove", [WorkflowTriggers.ItemDeleted]);

    public static readonly BuiltInWorkflow ContainerWorkflow = ItemChangeWorkflows.Create("search.containers", "Maintain library search",
        "Removes excluded or deleted libraries and requests indexing when they are included again.", "search.container", [SearchTriggers.ContainerChanged]);

    public static readonly BuiltInWorkflow ScopesWorkflow = ItemChangeWorkflows.Create("search.scopes", "Update search permissions",
        "Gives indexed items their new permission scopes, without indexing their text again.", "search.scopes", [SearchTriggers.ScopesChanged]);

    /// <summary>The chunk settings of the index workflow's parameters (defaults filled in), as stored with a publication.</summary>
    public static string Settings(JsonObject? parameters) => Settings(
        parameters?["chunker"]?.GetValue<string>() ?? "window",
        parameters?["maxChars"]?.GetValue<int>() ?? Passages.MaxChars,
        parameters?["overlap"]?.GetValue<int>() ?? Passages.Overlap);

    public static string Settings(string chunker, int maxChars, int overlap) => $"{chunker}:{maxChars}:{overlap}";
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

    /// <summary>
    /// Cheap stamps (item stamp plus text revision) of many items, without building documents or reading text: what a
    /// sweep compares with the stamps stored at publication.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> StampsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var stamps = new Dictionary<Guid, string>();
        foreach (var source in sources)
        {
            foreach (var (id, stamp) in await source.GetStampsAsync(ids, ct))
            {
                stamps.TryAdd(id, stamp);
            }
        }

        foreach (var source in textSources)
        {
            foreach (var (id, (revision, ready)) in await source.GetRevisionsAsync(ids, ct))
            {
                if (stamps.TryGetValue(id, out var stamp))
                {
                    stamps[id] = $"{stamp}|{revision}:{(ready ? 1 : 0)}";
                }
            }
        }

        return stamps;
    }

    /// <summary>A hash of what the document says. The permission scope is left out: scope changes do not index again.</summary>
    public static string Revision(SearchDocumentData document) => Passages.Hash(JsonSerializer.Serialize(document with { ScopeId = Guid.Empty }));
}

internal sealed class SearchChunkActivity(SearchInput input, ISearchStore store, SearchDbContext db) : IWorkflowActivity
{
    /// <summary>Longest chunk of the <c>whole</c> chunker (embedding models take a few thousand tokens).</summary>
    public const int MaxWholeChars = 20_000;

    public string Key => "search.chunk";
    public string Description => "Stages current item metadata and text as chunks without changing the published index; skipped when the list is excluded or the item is gone.";
    public IReadOnlyList<string> Outcomes => [SearchWorkflows.Skipped];
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
        if (Validate(context.Inputs).FirstOrDefault() is { } error) { return WorkflowActivityResult.Fail(error); }
        var policy = await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == item.ListId, ct);
        if (policy is { Included: false })
        {
            return WorkflowActivityResult.Ok(SearchWorkflows.Skipped, new JsonObject { ["reason"] = "excluded" });
        }

        var document = await input.ReadAsync(item.ItemId, ct);
        if (document is null || document.WorkspaceId != item.WorkspaceId || document.ContainerId != item.ListId)
        {
            return WorkflowActivityResult.Ok(SearchWorkflows.Skipped, new JsonObject { ["reason"] = "gone" });
        }

        var chunker = ActivityInputs.Text(context.Inputs, "chunker") ?? "window";
        var max = (int)(ActivityInputs.Number(context.Inputs, "maxChars") ?? Passages.MaxChars);
        var overlap = (int)(ActivityInputs.Number(context.Inputs, "overlap") ?? Passages.Overlap);
        var (chunks, truncated) = Chunk(document, chunker, max, overlap);
        var revision = SearchInput.Revision(document);
        var stamp = (await input.StampsAsync([item.ItemId], ct)).GetValueOrDefault(item.ItemId);
        await store.StageAsync(new SearchGeneration(generation, document with { Pages = [], Chunks = chunks }, revision, policy?.Version ?? 0)
        {
            Stamp = stamp,
            Settings = SearchWorkflows.Settings(chunker, max, overlap),
            Truncated = truncated,
        }, ct);
        var previousGeneration = await ClaimAsync(item, generation, ct);
        if (previousGeneration != Guid.Empty && previousGeneration != generation)
        {
            await store.DeleteStageAsync(previousGeneration, ct);
        }

        return WorkflowActivityResult.Ok(new JsonObject { ["generation"] = generation.ToString(), ["revision"] = revision, ["chunks"] = chunks.Count, ["truncated"] = truncated });
    }

    /// <summary>Makes the generation the item's next publication; returns the one it replaces.</summary>
    private async Task<Guid> ClaimAsync(WorkflowItem item, Guid generation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var publication = await db.Publications.FirstOrDefaultAsync(p => p.Id == item.ItemId, ct);
            if (publication is null)
            {
                publication = new SearchPublication { Id = item.ItemId, WorkspaceId = item.WorkspaceId, ContainerId = item.ListId };
                db.Publications.Add(publication);
            }

            var previous = publication.GenerationId;
            publication.GenerationId = generation;
            publication.WorkspaceId = item.WorkspaceId;
            publication.ContainerId = item.ListId;
            try
            {
                await db.SaveChangesAsync(ct);
                return previous;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Another run created the item's publication at the same moment: claim that one.
                db.ChangeTracker.Clear();
            }
        }
    }

    internal static (List<PassageText> Chunks, bool Truncated) Chunk(SearchDocumentData document, string chunker, int max, int overlap)
    {
        var texts = new List<PassageText> { new(null, $"{document.Keywords}\n{document.Body}") };
        texts.AddRange(document.Pages.Select((text, i) => new PassageText(i + 1, text)));
        if (texts.All(t => string.IsNullOrWhiteSpace(t.Text))) { texts = [new(null, document.Title)]; }
        if (chunker == "whole")
        {
            var whole = Passages.Normalize(string.Join('\n', texts.Select(t => t.Text)));
            return ([new PassageText(document.Pages.Count > 0 ? 1 : null, whole.Length > MaxWholeChars ? whole[..MaxWholeChars] : whole)], whole.Length > MaxWholeChars);
        }

        var chunks = new List<PassageText>();
        foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
        {
            var normalized = Passages.Normalize(text.Text);
            // A page longer than a chunk may be is split into windows, so no chunk exceeds what the embedding model takes.
            IEnumerable<string> parts = chunker == "pages" && normalized.Length <= max ? [normalized] : Passages.Windows(normalized, max, overlap);
            foreach (var part in parts)
            {
                if (chunks.Count == Passages.MaxPerDocument)
                {
                    return (chunks, true);
                }

                chunks.Add(new PassageText(text.Page, part));
            }
        }

        return (chunks, false);
    }
}

internal sealed class SearchPublishActivity(SearchInput input, ISearchStore store, SearchDbContext db, TimeProvider time) : IWorkflowActivity
{
    public string Key => "search.publish";
    public string Description => "Publishes a staged generation if the item and library policy still match; skipped when a newer change superseded it.";
    public IReadOnlyList<string> Outcomes => [SearchWorkflows.Skipped];

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.RunId is not { } id) { return WorkflowActivityResult.Fail("Publication requires a workflow run."); }
        var stage = await store.GetStageAsync(id, ct);
        if (stage is null)
        {
            // The store commit may have succeeded before the engine saved this node's output; else a newer run replaced it.
            var published = await db.Publications.AsNoTracking().FirstOrDefaultAsync(p => p.RunId == id && p.Revision != null, ct);
            return published is null ? Skip("superseded")
                : WorkflowActivityResult.Ok(new JsonObject { ["indexed"] = true, ["revision"] = published.Revision, ["chunks"] = published.Chunks });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Updating the current generation locks this item's publication on both database providers.
        var claimed = await db.Publications.Where(p => p.Id == stage.Document.Id && p.GenerationId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.RunId, context.RunId), ct);
        var policy = await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == stage.Document.ContainerId, ct);
        var current = claimed == 0 || policy is { Included: false } ? null : await input.ReadAsync(stage.Document.Id, ct);
        var reason = claimed == 0 ? "superseded"
            : policy is { Included: false } ? "excluded"
            : (policy?.Version ?? 0) != stage.PolicyVersion || current is null || SearchInput.Revision(current) != stage.Revision ? "superseded"
            : null;
        if (reason is not null)
        {
            await transaction.RollbackAsync(ct);
            await store.DeleteStageAsync(id, ct);
            return Skip(reason);
        }

        // The scope as it is now: a permission change handled meanwhile is not undone by an older stage.
        await store.UpsertAsync([stage.Document with { ScopeId = current!.ScopeId }], ct);
        await db.Publications.Where(p => p.Id == stage.Document.Id && p.GenerationId == id).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Revision, stage.Revision).SetProperty(p => p.PublishedAt, time.GetUtcNow())
            .SetProperty(p => p.Chunks, stage.Document.Chunks.Count).SetProperty(p => p.Truncated, stage.Truncated)
            .SetProperty(p => p.SourceStamp, stage.Stamp).SetProperty(p => p.Settings, stage.Settings)
            .SetProperty(p => p.EmbeddingModel, (string?)null), ct);
        await transaction.CommitAsync(ct);
        await store.DeleteStageAsync(id, ct);
        return WorkflowActivityResult.Ok(new JsonObject { ["indexed"] = true, ["revision"] = stage.Revision, ["chunks"] = stage.Document.Chunks.Count });
    }

    private static WorkflowActivityResult Skip(string reason) => WorkflowActivityResult.Ok(SearchWorkflows.Skipped, new JsonObject { ["reason"] = reason });
}

internal sealed class SearchEmbedActivity(ISearchStore store, EmbeddingModel embeddings, IOptions<SearchOptions> options, SearchDbContext db) : IWorkflowActivity
{
    public string Key => "search.embed";
    public string Description => "Embeds this item's published chunks; provider failures remain visible and retryable in the run.";
    public IReadOnlyList<string> Outcomes => [SearchWorkflows.Skipped];

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.Item is not { } item) { return WorkflowActivityResult.Fail("Embedding requires an item."); }
        if (await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == item.ListId && !p.Included, ct))
        { return WorkflowActivityResult.Ok(SearchWorkflows.Skipped, new JsonObject { ["reason"] = "excluded" }); }
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
            if (count > Passages.MaxPerDocument * 2)
            {
                // The store keeps returning passages it does not accept vectors for: stop instead of calling the provider forever.
                return WorkflowActivityResult.Fail("The search store did not keep the embeddings of this item.");
            }
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

/// <summary>Moves indexed items to their current permission scopes; their text, chunks and vectors stay.</summary>
internal sealed class SearchScopesActivity(IEnumerable<ISearchItemSource> sources, ISearchStore store) : IWorkflowActivity
{
    private const int Batch = 500;

    public string Key => "search.scopes";
    public string Description => "Gives indexed items their current permission scopes (data: itemIds), without indexing them again.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.ExecutionContext?["trigger"]?.GetValue<string>() != SearchTriggers.ScopesChanged)
        { return WorkflowActivityResult.Fail($"search.scopes only runs on {SearchTriggers.ScopesChanged}."); }
        var ids = (context.Data?["itemIds"] as JsonArray ?? []).Select(v => Guid.TryParse(v?.GetValue<string>(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToList();
        var updated = 0;
        foreach (var chunk in ids.Chunk(Batch))
        {
            var scopes = new Dictionary<Guid, Guid>();
            foreach (var source in sources)
            {
                foreach (var (id, scope) in await source.GetScopesAsync(chunk, ct))
                {
                    scopes.TryAdd(id, scope);
                }
            }

            await store.SetScopesAsync(scopes, ct);
            updated += scopes.Count;
        }

        return WorkflowActivityResult.Ok(new JsonObject { ["updated"] = updated });
    }
}

internal sealed class SearchContainerActivity(ISearchStore store, SearchDbContext db, IListItemStore items, IWorkflowTriggers triggers, IWorkflowBookmarks bookmarks) : IWorkflowActivity
{
    private const int PageSize = 200;
    private const int PagesPerExecution = 5;

    public string Key => "search.container";
    public string Description => "Applies library inclusion changes: removes an excluded or deleted list, or requests indexing of an included one in bounded pages.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        // The data must come from the product's own trigger: a workflow's raised data could name another workspace's list.
        if (context.ExecutionContext?["trigger"]?.GetValue<string>() != SearchTriggers.ContainerChanged)
        { return WorkflowActivityResult.Fail($"search.container only runs on {SearchTriggers.ContainerChanged}."); }
        if (!Guid.TryParse(context.Data?["containerId"]?.GetValue<string>(), out var id)) { return WorkflowActivityResult.Fail("containerId is required."); }
        var list = await items.AsSystem().GetListAsync(context.WorkspaceId, id, ct);
        if (list is null || await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == id && !p.Included, ct))
        {
            await store.DeleteContainerAsync(id, ct);
            await db.Publications.Where(p => p.ContainerId == id).ExecuteDeleteAsync(ct);
            return WorkflowActivityResult.Ok(new JsonObject { ["removed"] = true });
        }

        // Included (again) or its schema changed: every item is indexed, also where automatic indexing is off. Request ids
        // come from the execution, so a retry requests nothing twice.
        var requests = WorkflowRequests.Resume(context, () => new JsonObject { ["cursor"] = null });
        for (var pages = 0; pages < PagesPerExecution; pages++)
        {
            var (page, error) = await items.AsSystem().QueryPageAsync(list.WorkspaceId, id,
                new ListItemQuery(Top: PageSize, SkipToken: requests.Cursor["cursor"]?.GetValue<string>()), ct);
            if (error is not null) { return WorkflowActivityResult.Fail(error); }
            foreach (var item in page!.Items)
            {
                await triggers.RaiseAsync(SearchWorkflows.IncludeItem, item.WorkspaceId, new WorkflowItem(item.WorkspaceId, item.ListId, item.Id), null,
                    WorkflowRequests.RequestId(context.ExecutionKey, item.Id), ct);
                requests.Raised();
            }

            requests.Cursor["cursor"] = page.NextCursor;
            if (page.NextCursor is null)
            {
                return WorkflowActivityResult.Ok(new JsonObject { ["requested"] = requests.Requested });
            }
        }

        return await requests.YieldAsync(context.ExecutionKey, bookmarks, ct);
    }
}
