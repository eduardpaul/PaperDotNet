using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>The alternative pipeline for the <see cref="Index"/> role: chunks get a sentence of context from the chat model.</summary>
    public const string IndexEnriched = "search.indexEnriched";

    public const string DefaultInstructions =
        "In one sentence, say which document this passage comes from and what it is about. Answer with the sentence only.";

    private static readonly JsonObject ChunkParameters = JsonNode.Parse("""
        {
          "chunker": { "type": "string", "enum": ["window", "pages", "whole"], "default": "window" },
          "maxChars": { "type": "integer", "minimum": 100, "maximum": 20000, "default": 1200 },
          "overlap": { "type": "integer", "minimum": 0, "maximum": 1000, "default": 150 }
        }
        """)!.AsObject();

    /// <summary>
    /// The default <see cref="Index"/> role: chunk, publish, embed. Copy it per list to change the pipeline (insert OCR, AI or
    /// script steps, or stage chunks of your own with <c>search.stage</c>); the copy fills the role and replaces it there.
    /// </summary>
    public static readonly BuiltInWorkflow IndexWorkflow = new(Index, "Index for search",
        "Indexes this list's items and file text, then embeds the chunks. Run on demand or automatically after changes.", IndexDefinition(enrich: false))
    {
        Scope = BuiltInScope.List,
        EnabledByDefault = true,
        AllowManualLaunch = true,
        System = true,
        Parameters = new JsonObject { ["type"] = "object", ["properties"] = ChunkParameters.DeepClone() },
    };

    /// <summary>An alternative workflow for the <see cref="Index"/> role (needs a chat model): turning it on in a list replaces the default there.</summary>
    public static readonly BuiltInWorkflow IndexEnrichedWorkflow = new(IndexEnriched, "Index for search with AI context",
        "Like Index for search, but the chat model adds a sentence of context to each chunk before it is published and embedded, so passages are found by what they are about. Replaces Index for search in the list.",
        IndexDefinition(enrich: true))
    {
        Role = Index,
        Scope = BuiltInScope.List,
        AllowManualLaunch = true,
        System = true,
        Requires = BuiltInRequirements.Ai,
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject([.. ChunkParameters.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())),
                KeyValuePair.Create<string, JsonNode?>("instructions", new JsonObject { ["type"] = "string", ["default"] = DefaultInstructions, ["description"] = "What the chat model writes for each chunk." })]),
        },
    };

    /// <summary>The indexing flow: chunk → (enrich →) publish → embed; a step with nothing to do ends at <c>skipped</c>.</summary>
    private static JsonObject IndexDefinition(bool enrich)
    {
        var definition = JsonNode.Parse("""
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
            """)!.AsObject();
        if (enrich)
        {
            var nodes = definition["flow"]!["nodes"]!.AsObject();
            nodes["chunk"]!["next"]!["done"] = "enrich";
            nodes["enrich"] = new JsonObject
            {
                ["activity"] = "search.enrich",
                ["inputs"] = new JsonObject { ["instructions"] = "{param:instructions}" },
                ["next"] = new JsonObject { ["done"] = "publish", ["skipped"] = "end" },
            };
        }

        return definition;
    }

    public static readonly BuiltInWorkflow RebuildWorkflow = new("search.rebuild", "Rebuild search",
        "Rebuilds included lists through item workflows: on request every item, on its schedule only items whose index is stale.",
        JsonNode.Parse("""
            { "scope": "workspace", "triggers": [
                { "type": "search.rebuildRequested" },
                { "type": "schedule", "cron": "*/15 * * * *", "concurrency": "skip" }
              ], "flow": { "start": "rebuild", "nodes": { "rebuild": { "activity": "search.rebuild" } } } }
            """)!.AsObject())
    { EnabledByDefault = true, System = true };

    public static readonly BuiltInWorkflow RemoveWorkflow = ItemChangeWorkflows.Create("search.remove", "Remove deleted items from search",
        "Removes the search records of deleted items; independent of automatic indexing.", "search.remove", [WorkflowTriggers.ItemDeleted], locked: true);

    public static readonly BuiltInWorkflow ContainerWorkflow = ItemChangeWorkflows.Create("search.containers", "Maintain library search",
        "Removes excluded or deleted libraries and requests indexing when they are included again.", "search.container", [SearchTriggers.ContainerChanged], locked: true);

    public static readonly BuiltInWorkflow ScopesWorkflow = ItemChangeWorkflows.Create("search.scopes", "Update search permissions",
        "Gives indexed items their new permission scopes, without indexing their text again.", "search.scopes", [SearchTriggers.ScopesChanged], locked: true);

    /// <summary>
    /// The pipeline that stages a generation (its workflow and version, from the engine-owned execution context): stored
    /// with the publication, so changing the role's workflow, its version or its parameters marks publications stale.
    /// </summary>
    public static string? Pipeline(WorkflowActivityContext context) =>
        context.ExecutionContext?["workflowId"]?.GetValue<string>() is { } id && Guid.TryParse(id, out var workflowId)
            ? Pipeline(workflowId, context.ExecutionContext["workflowVersion"]?.GetValue<int>() ?? 0)
            : null;

    public static string Pipeline(Guid workflowId, int version) => $"{workflowId:N}:{version}";
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

/// <summary>
/// What every pipeline of the <c>search.index</c> role shares: reading the item (skipped when its list is excluded or it is
/// gone) and staging a generation of chunks under the run, outside the published index. Publication (<c>search.publish</c>)
/// checks it again, so a custom pipeline can change chunks and cost, never permissions or inclusion.
/// </summary>
internal sealed class SearchStaging(SearchInput input, ISearchStore store, SearchDbContext db)
{
    /// <summary>The item's current document and policy version, or the result that ends the step (misused or skipped).</summary>
    public async Task<(SearchDocumentData? Document, uint PolicyVersion, WorkflowActivityResult? Result)> ReadAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.Item is not { } item || context.RunId is null)
        {
            return (null, 0, WorkflowActivityResult.Fail("Indexing requires an item workflow run."));
        }

        var policy = await db.ContainerPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == item.ListId, ct);
        if (policy is { Included: false })
        {
            return (null, 0, WorkflowActivityResult.Ok(SearchWorkflows.Skipped, new JsonObject { ["reason"] = "excluded" }));
        }

        var document = await input.ReadAsync(item.ItemId, ct);
        return document is null || document.WorkspaceId != item.WorkspaceId || document.ContainerId != item.ListId
            ? (null, 0, WorkflowActivityResult.Ok(SearchWorkflows.Skipped, new JsonObject { ["reason"] = "gone" }))
            : (document, policy?.Version ?? 0, null);
    }

    /// <summary>Stages the chunks as the run's generation and makes it the item's next publication.</summary>
    public async Task<WorkflowActivityResult> StageAsync(
        WorkflowActivityContext context, SearchDocumentData document, uint policyVersion, List<PassageText> chunks, bool truncated, CancellationToken ct)
    {
        var item = context.Item!;
        var generation = context.RunId!.Value;
        var revision = SearchInput.Revision(document);
        var stamp = (await input.StampsAsync([item.ItemId], ct)).GetValueOrDefault(item.ItemId);
        await store.StageAsync(new SearchGeneration(generation, document with { Pages = [], Chunks = chunks }, revision, policyVersion)
        {
            Stamp = stamp,
            Settings = SearchWorkflows.Pipeline(context),
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
}

internal sealed class SearchChunkActivity(SearchStaging staging) : IWorkflowActivity
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
        if (Validate(context.Inputs).FirstOrDefault() is { } error) { return WorkflowActivityResult.Fail(error); }
        var (document, policyVersion, ended) = await staging.ReadAsync(context, ct);
        if (ended is not null) { return ended; }
        var chunker = ActivityInputs.Text(context.Inputs, "chunker") ?? "window";
        var max = (int)(ActivityInputs.Number(context.Inputs, "maxChars") ?? Passages.MaxChars);
        var overlap = (int)(ActivityInputs.Number(context.Inputs, "overlap") ?? Passages.Overlap);
        var (chunks, truncated) = Chunk(document!, chunker, max, overlap);
        return await staging.StageAsync(context, document!, policyVersion, chunks, truncated, ct);
    }

    internal static (List<PassageText> Chunks, bool Truncated) Chunk(SearchDocumentData document, string chunker, int max, int overlap)
    {
        var texts = new List<PassageText> { new(null, $"{document.Keywords}\n{document.Body}") { FromBody = true } };
        texts.AddRange(document.Pages.Select((text, i) => new PassageText(i + 1, text)));
        if (texts.All(t => string.IsNullOrWhiteSpace(t.Text))) { texts = [new(null, document.Title) { FromBody = true }]; }
        if (chunker == "whole")
        {
            var whole = Passages.Normalize(string.Join('\n', texts.Select(t => t.Text)));
            return ([new PassageText(document.Pages.Count > 0 ? 1 : null, whole.Length > MaxWholeChars ? whole[..MaxWholeChars] : whole) { FromBody = document.Pages.Count == 0 }],
                whole.Length > MaxWholeChars);
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

                chunks.Add(new PassageText(text.Page, part) { FromBody = text.FromBody });
            }
        }

        return (chunks, false);
    }
}

/// <summary>
/// Stages chunks a pipeline made itself (a script, an AI or OCR step) instead of a chunker: <c>chunks</c> is a list of
/// texts or of <c>{ "text", "page" }</c>, usually a single token such as <c>{step:split.json.chunks}</c>.
/// </summary>
internal sealed class SearchStageActivity(SearchStaging staging) : IWorkflowActivity
{
    public string Key => "search.stage";
    public string Description => "Stages the given chunks (texts or { text, page }) of the item for publication; skipped when the list is excluded or the item is gone.";
    public IReadOnlyList<string> Outcomes => [SearchWorkflows.Skipped];
    public JsonObject? InputSchema => JsonNode.Parse("""
        { "type": "object", "required": ["chunks"], "properties": {
          "chunks": { "type": "array", "description": "Texts, or objects with text and page (1 = first page); at most 400, each up to 20,000 characters." }
        } }
        """)!.AsObject();
    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("chunks", ActivitySchemas.Number("Chunks staged.")),
        ("truncated", ActivitySchemas.Boolean("Whether chunks were cut to the limits.")));

    public IEnumerable<string> Validate(JsonObject inputs)
    {
        if (inputs["chunks"] is not (JsonArray or JsonValue)) { yield return "chunks is required: a list, or a token that gives one."; }
    }

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        var given = context.Inputs["chunks"] is JsonValue token && token.TryGetValue<string>(out var template)
            ? await context.ResolveAsync(template, ct)
            : context.Inputs["chunks"];
        if (given is not JsonArray list)
        {
            return WorkflowActivityResult.Fail("chunks must be a list of texts or of { text, page }.");
        }

        var (document, policyVersion, ended) = await staging.ReadAsync(context, ct);
        if (ended is not null) { return ended; }
        var chunks = new List<PassageText>();
        var truncated = list.Count > Passages.MaxPerDocument;
        foreach (var entry in list.Take(Passages.MaxPerDocument))
        {
            var (text, page) = entry switch
            {
                JsonValue value when value.TryGetValue<string>(out var plain) => (plain, (int?)null),
                JsonObject chunk => (chunk["text"]?.GetValue<string>(), chunk["page"] is JsonValue p && p.TryGetValue<int>(out var number) ? number : (int?)null),
                _ => (null, null),
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = Passages.Normalize(text);
            truncated |= normalized.Length > SearchChunkActivity.MaxWholeChars;
            chunks.Add(new PassageText(page, normalized.Length > SearchChunkActivity.MaxWholeChars ? normalized[..SearchChunkActivity.MaxWholeChars] : normalized));
        }

        return chunks.Count == 0
            ? WorkflowActivityResult.Fail("chunks has no text.")
            : await staging.StageAsync(context, document!, policyVersion, chunks, truncated, ct);
    }
}

/// <summary>
/// Asks the chat model for text to add to each staged chunk (by default a sentence of context), so passages are found by
/// what they are about. Answers are cached by instructions, title and chunk text: an unchanged chunk is never asked again,
/// and its vector is reused.
/// </summary>
internal sealed class SearchEnrichActivity(ISearchStore store, SearchDbContext db, IServiceProvider services, TimeProvider time) : IWorkflowActivity
{
    /// <summary>Longest added text per chunk.</summary>
    private const int MaxContextChars = 1000;

    public string Key => "search.enrich";
    public string Description => "Adds text from the chat model (e.g. a sentence of context) to each staged chunk before it is published and embedded.";
    public IReadOnlyList<string> Outcomes => [SearchWorkflows.Skipped];
    public JsonObject? InputSchema => JsonNode.Parse("""
        { "type": "object", "properties": {
          "instructions": { "type": "string", "description": "What the chat model writes for each chunk (default: one sentence of context)." }
        } }
        """)!.AsObject();
    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("enriched", ActivitySchemas.Number("Chunks the model was asked about.")),
        ("cached", ActivitySchemas.Number("Chunks whose text was known already.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.RunId is not { } id) { return WorkflowActivityResult.Fail("Enrichment requires a workflow run."); }
        if (services.GetService<IChatClient>() is not { } chat) { return WorkflowActivityResult.Fail("search.enrich needs a chat model (AI:Chat)."); }
        if (await store.GetStageAsync(id, ct) is not { } stage)
        {
            return WorkflowActivityResult.Ok(SearchWorkflows.Skipped, new JsonObject { ["reason"] = "superseded" });
        }

        var instructions = ActivityInputs.Text(context.Inputs, "instructions") is { Length: > 0 } given
            ? await context.ExpandAsync(given, ct)
            : SearchWorkflows.DefaultInstructions;
        var title = stage.Document.Title;
        var keys = stage.Document.Chunks.Select(c => CacheKey(instructions, title, c.Text)).ToList();
        var distinct = keys.Distinct().ToArray();
        var known = await db.Enrichments.AsNoTracking().Where(e => EF.Parameter(distinct).Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Text, ct);
        var asked = 0;
        var enriched = new List<PassageText>();
        foreach (var (chunk, key) in stage.Document.Chunks.Zip(keys))
        {
            if (!known.TryGetValue(key, out var added))
            {
                ChatResponse answer;
                try
                {
                    answer = await chat.GetResponseAsync(
                        [new ChatMessage(ChatRole.System, instructions), new ChatMessage(ChatRole.User, $"Document: {title}\n\nPassage:\n{chunk.Text}")],
                        cancellationToken: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return WorkflowActivityResult.Fail($"The chat model failed: {ex.Message}");
                }

                added = Passages.Normalize(answer.Text ?? string.Empty);
                added = added.Length > MaxContextChars ? added[..MaxContextChars] : added;
                known[key] = added;
                db.Enrichments.Add(new SearchEnrichment { Id = key, Text = added, CreatedAt = time.GetUtcNow() });
                asked++;
            }

            enriched.Add(string.IsNullOrWhiteSpace(added) ? chunk : chunk with { Text = $"{added}\n{chunk.Text}" });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another run cached the same answers meanwhile: theirs count.
            db.ChangeTracker.Clear();
        }

        await store.StageAsync(stage with { Document = stage.Document with { Chunks = enriched } }, ct);
        return WorkflowActivityResult.Ok(new JsonObject { ["enriched"] = asked, ["cached"] = enriched.Count - asked });
    }

    private static Guid CacheKey(string instructions, string title, string text) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{instructions}\n{title}\n{text}"))[..16]);
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
