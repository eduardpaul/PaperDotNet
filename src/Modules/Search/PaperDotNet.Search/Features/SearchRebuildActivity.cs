using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>
/// Coordinates a rebuild through item workflows (<see cref="WorkflowRequests"/>): on request every item of every included
/// list, on its schedule only items whose publication is stale (compared by cheap stamps, never by reading text), and only
/// in lists with automatic indexing. Each page of requests is waited for durably; a failed item is counted and the rebuild
/// goes on. Scanning yields after a bounded number of items, so a large tenant never holds one execution.
/// </summary>
internal sealed class SearchRebuildActivity(
    IListItemStore items, SearchDbContext db, IWorkflowTriggers triggers, IWorkflowDirectory workflows, TimeProvider time, SearchInput input,
    EmbeddingModel embeddings, ISearchStore store, IWorkflowBookmarks bookmarks) : IWorkflowActivity
{
    private const int PageSize = 100;

    /// <summary>Items one execution looks at before it yields.</summary>
    private const int ScanPerExecution = 2000;

    /// <summary>How long one item's indexing may take before it counts as failed.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromHours(6);

    public string Key => "search.rebuild";
    public string Description => "Requests indexing in bounded pages, waits for the item runs and reports the ones that failed.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        var scheduled = context.ExecutionContext?["trigger"]?.GetValue<string>() == WorkflowTriggers.Schedule;
        var requests = WorkflowRequests.Resume(context, () => new JsonObject
        {
            ["list"] = 0,
            ["cursor"] = null,
            ["lists"] = new JsonArray(),
        });
        var cursor = requests.Cursor;
        if (requests.Requested == 0 && cursor["list"]!.GetValue<int>() == 0 && cursor["lists"]!.AsArray().Count == 0)
        {
            cursor["lists"] = new JsonArray([.. (await items.AsSystem().GetListsAsync(context.WorkspaceId, null, ct)).Select(l => JsonValue.Create(l.Id.ToString()))]);
        }

        if (await requests.SettleAsync(SearchWorkflows.IndexRequestKind, workflows, time.GetUtcNow() + RequestTimeout, ct) is { } waiting)
        {
            return waiting;
        }

        var lists = cursor["lists"]!.AsArray();
        var scanned = 0;
        while (cursor["list"]!.GetValue<int>() < lists.Count)
        {
            if (scanned >= ScanPerExecution)
            {
                return await requests.YieldAsync(context.ExecutionKey, bookmarks, ct);
            }

            var listId = Guid.Parse(lists[cursor["list"]!.GetValue<int>()]!.GetValue<string>());
            if (await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == listId && !p.Included, ct)
                || (scheduled && !await workflows.IsBuiltInEnabledAsync(context.WorkspaceId, SearchWorkflows.Index, listId, ct))
                || await items.AsSystem().GetListAsync(context.WorkspaceId, listId, ct) is null)
            {
                NextList(cursor);
                continue;
            }

            var (page, error) = await items.AsSystem().QueryPageAsync(context.WorkspaceId, listId,
                new ListItemQuery(Top: PageSize, SkipToken: cursor["cursor"]?.GetValue<string>()), ct);
            if (error is not null) { return WorkflowActivityResult.Fail(error); }
            scanned += page!.Items.Count;
            var wanted = scheduled ? await StaleAsync(context.WorkspaceId, listId, page.Items.Select(i => i.Id).ToList(), ct) : page.Items.Select(i => i.Id).ToHashSet();
            foreach (var item in page.Items.Where(i => wanted.Contains(i.Id)))
            {
                var requestId = WorkflowRequests.RequestId(context.ExecutionKey, item.Id);
                await triggers.RaiseAsync(SearchWorkflows.ReindexItem, item.WorkspaceId, new WorkflowItem(item.WorkspaceId, item.ListId, item.Id), null, requestId, ct);
                requests.Add(requestId);
            }

            cursor["cursor"] = page.NextCursor;
            if (page.NextCursor is null)
            {
                NextList(cursor);
            }

            if (requests.Pending > 0
                && await requests.SettleAsync(SearchWorkflows.IndexRequestKind, workflows, time.GetUtcNow() + RequestTimeout, ct) is { } wait)
            {
                return wait;
            }
        }

        var summary = requests.Summary();
        summary["lists"] = lists.Count;
        // An explicit rebuild that left items unindexed is a failure people should see; the schedule tries again.
        return !scheduled && requests.Failed > 0
            ? WorkflowActivityResult.Fail($"{requests.Failed} of {requests.Requested} items could not be indexed; see the output of their runs. {summary["failures"]!.ToJsonString()}")
            : WorkflowActivityResult.Ok(summary);
    }

    private static void NextList(JsonObject cursor)
    {
        cursor["list"] = cursor["list"]!.GetValue<int>() + 1;
        cursor["cursor"] = null;
    }

    /// <summary>
    /// Items of a page whose publication is missing or stale: another source stamp (item change, file version), other chunk
    /// settings, or vectors of another model. Batched lookups only.
    /// </summary>
    private async Task<HashSet<Guid>> StaleAsync(Guid workspaceId, Guid listId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var embedded = embeddings.Enabled && store.Capabilities.HasFlag(SearchStoreCapabilities.Vector);
        var settings = SearchWorkflows.Settings(await workflows.GetBuiltInParametersAsync(workspaceId, SearchWorkflows.Index, listId, ct));
        var stamps = await input.StampsAsync(ids, ct);
        var idArray = ids.ToArray();
        var publications = await db.Publications.AsNoTracking().Where(p => EF.Parameter(idArray).Contains(p.Id))
            .Select(p => new { p.Id, p.Revision, p.SourceStamp, p.Settings, p.EmbeddingModel }).ToDictionaryAsync(p => p.Id, ct);
        return [.. ids.Where(id => stamps.ContainsKey(id) && (!publications.TryGetValue(id, out var publication)
            || publication.Revision is null || publication.SourceStamp != stamps[id] || publication.Settings != settings
            || (embedded && publication.EmbeddingModel != embeddings.ModelKey)))];
    }
}
