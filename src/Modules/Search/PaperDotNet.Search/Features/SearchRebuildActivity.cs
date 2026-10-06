using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>Bounded, resumable enumeration and coordination; indexing itself runs in item workflows.</summary>
internal sealed class SearchRebuildActivity(
    IListItemStore items, SearchDbContext db, IWorkflowTriggers triggers, IWorkflowDirectory workflows, TimeProvider time, SearchInput input, EmbeddingModel embeddings) : IWorkflowActivity
{
    public string Key => "search.rebuild";
    public string Description => "Requests indexing in bounded pages, waits for child runs and reports their failures.";

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        if (context.Resumed?.TimedOut == true) { return WorkflowActivityResult.Fail("The child indexing request did not finish within 24 hours."); }
        var state = context.Resumed?.Data?.DeepClone().AsObject() ?? new JsonObject
        {
            ["list"] = 0, ["indexed"] = 0, ["cursor"] = null,
            ["lists"] = new JsonArray([.. (await items.AsSystem().GetListsAsync(context.WorkspaceId, null, ct)).Select(l => JsonValue.Create(l.Id.ToString()))]),
        };
        if (state["requests"] is JsonArray pending)
        {
            foreach (var request in pending)
            {
                var runs = await workflows.GetEventRunsAsync(Guid.Parse(request!.GetValue<string>()), ct);
                if (runs.Count == 0 || runs.Any(r => r.Status is "running" or "waiting")) { return Wait(context, state, request!.GetValue<string>()); }
                if (runs.FirstOrDefault(r => r.Status is "failed" or "cancelled") is { } failed)
                { return WorkflowActivityResult.Fail($"Item indexing did not complete in run {failed.Id}: {failed.Error}"); }
            }
            state["indexed"] = state["indexed"]!.GetValue<int>() + pending.Count;
            state.Remove("requests");
        }
        var lists = state["lists"]!.AsArray();
        var scheduled = context.ExecutionContext?["trigger"]?.GetValue<string>() == WorkflowTriggers.Schedule;
        var listNumber = state["list"]!.GetValue<int>();
        while (listNumber < lists.Count)
        {
            var id = Guid.Parse(lists[listNumber]!.GetValue<string>());
            if (await db.ContainerPolicies.AsNoTracking().AnyAsync(p => p.Id == id && !p.Included, ct)
                || (scheduled && !await workflows.IsBuiltInEnabledAsync(context.WorkspaceId, SearchWorkflows.Index, id, ct))
                || await items.AsSystem().GetListAsync(context.WorkspaceId, id, ct) is null)
            {
                state["list"] = ++listNumber;
                state["cursor"] = null;
                continue;
            }
            var (page, error) = await items.AsSystem().QueryPageAsync(context.WorkspaceId, id,
                new ListItemQuery(Top: 100, SkipToken: state["cursor"]?.GetValue<string>()), ct);
            if (error is not null) { return WorkflowActivityResult.Fail(error); }
            var requests = new JsonArray();
            foreach (var item in page!.Items)
            {
                if (scheduled)
                {
                    var publication = await db.Publications.AsNoTracking().FirstOrDefaultAsync(p => p.Id == item.Id, ct);
                    var source = await input.ReadAsync(item.Id, ct);
                    if (source is null || (publication?.Revision == SearchInput.Revision(source)
                        && (!embeddings.Enabled || publication.EmbeddingModel == embeddings.ModelKey))) { continue; }
                }
                var requestId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.ExecutionKey}:{item.Id:N}"))[..16]);
                await triggers.RaiseAsync(SearchWorkflows.ReindexItem, item.WorkspaceId, new WorkflowItem(item.WorkspaceId, item.ListId, item.Id), null, requestId, ct);
                requests.Add(requestId.ToString());
            }
            state["cursor"] = page.NextCursor;
            if (page.NextCursor is null) { state["list"] = ++listNumber; }
            if (requests.Count > 0)
            {
                state["requests"] = requests;
                return Wait(context, state, requests[0]!.GetValue<string>());
            }
        }
        return WorkflowActivityResult.Ok(new JsonObject { ["indexed"] = state["indexed"]!.DeepClone(), ["lists"] = lists.Count });
    }

    private WorkflowActivityResult Wait(WorkflowActivityContext context, JsonObject state, string requestId) =>
        WorkflowActivityResult.WaitAndRunAgain("search.indexRequest", requestId, time.GetUtcNow() + TimeSpan.FromHours(24), state);
}
