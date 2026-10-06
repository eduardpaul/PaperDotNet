using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>
/// Bounded fan-out for coordinator activities (e.g. a search rebuild): page through a set, raise one request per element
/// with a trigger that reports completions (<see cref="WorkflowTriggerDefinition.CompletionKind"/>), wait for them through
/// durable waits, and continue. Failures of single elements are collected, not fatal, so one broken item never blocks the
/// rest. All state lives in the wait's data (<see cref="WorkflowActivityResult.WaitAndRunAgain"/>): an activity resumes
/// with <see cref="Resume"/>, does a bounded amount of work, and returns <see cref="WaitFor"/> or <see cref="YieldAsync"/>.
/// </summary>
public sealed class WorkflowRequests
{
    /// <summary>Wait kind of <see cref="YieldAsync"/>: a pause that lets other runs go first, then continues.</summary>
    public const string YieldKind = "workflows.yield";

    /// <summary>Failures kept in the state and the summary (the count covers all of them).</summary>
    public const int MaxFailuresKept = 20;

    private readonly JsonObject state;

    private WorkflowRequests(JsonObject state) => this.state = state;

    /// <summary>The coordinator's own position (lists, cursors), saved with the requests.</summary>
    public JsonObject Cursor => state["cursor"]!.AsObject();

    /// <summary>Requests not yet settled.</summary>
    public int Pending => state["pending"]!.AsArray().Count;

    /// <summary>Requests raised so far.</summary>
    public int Requested => state["requested"]!.GetValue<int>();

    /// <summary>Requests that failed (their runs failed, or they did not finish in time).</summary>
    public int Failed => state["failed"]!.GetValue<int>();

    /// <summary>The state saved before, or a new one with <paramref name="cursor"/> as the coordinator's position.</summary>
    public static WorkflowRequests Resume(WorkflowActivityContext context, Func<JsonObject> cursor) =>
        new(context.Resumed?.Data?["cursor"] is JsonObject ? context.Resumed.Data.DeepClone().AsObject() : new JsonObject
        {
            ["cursor"] = cursor(),
            ["pending"] = new JsonArray(),
            ["requested"] = 0,
            ["completed"] = 0,
            ["failed"] = 0,
            ["failures"] = new JsonArray(),
            ["yields"] = 0,
        });

    /// <summary>
    /// The id of the request for <paramref name="element"/> in this execution: the same on a retry, so raising it again
    /// starts nothing twice (runs are unique per workflow and event).
    /// </summary>
    public static Guid RequestId(string executionKey, Guid element) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{executionKey}:{element:N}"))[..16]);

    /// <summary>Records a raised request to wait for.</summary>
    public void Add(Guid requestId)
    {
        state["pending"]!.AsArray().Add(requestId.ToString());
        state["requested"] = Requested + 1;
    }

    /// <summary>Counts a raised request nobody waits for (fire and forget, still bounded and repeat-safe).</summary>
    public void Raised() => state["requested"] = Requested + 1;

    /// <summary>
    /// Settles the requests that finished (from their completion waits). Returns the wait for the first one still going,
    /// or null when none is left. A request that times out (<paramref name="timeout"/>) counts as failed. Cancelled runs
    /// (replaced by a newer run of the same workflow on the item) count as done.
    /// </summary>
    public async Task<WorkflowActivityResult?> SettleAsync(
        string kind, IWorkflowDirectory workflows, DateTimeOffset timeout, CancellationToken cancellationToken)
    {
        var pending = state["pending"]!.AsArray();
        while (pending.Count > 0)
        {
            var key = pending[0]!.GetValue<string>();
            var completion = await workflows.GetCompletionAsync(kind, key, cancellationToken);
            if (completion is null)
            {
                return WaitFor(kind, key, timeout);
            }

            var status = completion["status"]?.GetValue<string>();
            if (status is null)
            {
                Fail(key, null, "The request did not finish in time.");
            }
            else if (status == "failed")
            {
                foreach (var run in (completion["runs"] as JsonArray ?? []).OfType<JsonObject>().Where(r => r["status"]?.GetValue<string>() == "failed"))
                {
                    Fail(key, run["id"]?.GetValue<string>(), run["error"]?.GetValue<string>());
                }
            }
            else
            {
                state["completed"] = state["completed"]!.GetValue<int>() + 1;
            }

            pending.RemoveAt(0);
        }

        return null;
    }

    /// <summary>Waits for one request; the activity runs again when it completes (or at <paramref name="timeout"/>).</summary>
    public WorkflowActivityResult WaitFor(string kind, string requestKey, DateTimeOffset timeout) =>
        WorkflowActivityResult.WaitAndRunAgain(kind, requestKey, timeout, state);

    /// <summary>
    /// Pauses after a bounded amount of work and continues right after, behind the messages already queued: a long
    /// coordination never holds a server or keeps other runs waiting. The wait is completed before it starts, so the
    /// engine resumes the run at once.
    /// </summary>
    public async Task<WorkflowActivityResult> YieldAsync(string executionKey, IWorkflowBookmarks bookmarks, CancellationToken cancellationToken)
    {
        var yields = state["yields"]!.GetValue<int>();
        state["yields"] = yields + 1;
        var key = $"{executionKey}:{yields}";
        await bookmarks.CompleteAsync(YieldKind, key, new JsonObject { ["outcome"] = "yield" }, cancellationToken);
        return WorkflowActivityResult.WaitAndRunAgain(YieldKind, key, null, state);
    }

    /// <summary>Counts and the first failures, for the activity's output.</summary>
    public JsonObject Summary() => new()
    {
        ["requested"] = Requested,
        ["completed"] = state["completed"]!.GetValue<int>(),
        ["failed"] = Failed,
        ["failures"] = state["failures"]!.DeepClone(),
    };

    private void Fail(string request, string? runId, string? error)
    {
        state["failed"] = Failed + 1;
        var failures = state["failures"]!.AsArray();
        if (failures.Count < MaxFailuresKept)
        {
            failures.Add(new JsonObject { ["request"] = request, ["runId"] = runId, ["error"] = error });
        }
    }
}
