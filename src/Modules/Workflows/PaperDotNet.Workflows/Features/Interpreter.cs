using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Runs a workflow's flow (ADR-0036) node by node and saves the run after each node. A <see cref="ResumeRun"/> message
/// starts or continues it; a long run continues in a new message after <see cref="MaxNodesPerExecution"/> nodes, so it
/// does not hold a queue. Actions get a stable execution id (saved before they run), so a repeat after a crash is safe.
/// Ported from the .NET 10 module without waits (approvals, delays, retries) and leases (one server, ADR-0039).
/// </summary>
public sealed partial class WorkflowInterpreter(
    WorkflowsDbContext db,
    IOutbox outbox,
    IServiceProvider services,
    IEnumerable<IWorkflowActivity> activities,
    WorkflowItems items,
    ItemConditions conditions,
    TokenExpander tokens,
    ScriptRunner scripts,
    IWorkflowRecipients recipients,
    TimeProvider time,
    ILogger<WorkflowInterpreter> logger)
{
    private const int MaxNodesPerExecution = 200;
    private const int MaxNodesPerRun = 5000;
    private const int MaxLogEntries = 100;
    private const int MaxStateLength = 256 * 1024;

    private readonly Dictionary<string, IWorkflowActivity> _actions = activities.ToDictionary(a => a.Key, StringComparer.Ordinal);

    public async Task RunAsync(Guid tenantId, Guid runId, CancellationToken ct)
    {
        if (await FindRunAsync(db, tenantId, runId, ct) is not { Status: RunStatus.Running or RunStatus.Waiting } run
            || await WorkflowVersions.FindAsync(db, tenantId, run.WorkflowId, run.WorkflowVersion, ct) is not { } version)
        {
            return;
        }

        var flow = WorkflowJson.Deserialize(version.Definition).Flow!;
        var outputs = JsonNode.Parse(run.Outputs) as JsonObject ?? [];
        var variables = JsonNode.Parse(run.Variables) as JsonObject ?? [];
        var log = JsonNode.Parse(run.Log) as JsonArray ?? [];
        var data = run.Data is { } json ? JsonNode.Parse(json) as JsonObject : null;
        var actor = new ChangeActor(run.TenantId, null, run.Depth + 1);
        var reader = new ChangeActor(run.TenantId, null);

        void Log(string message)
        {
            log.Add(item: new JsonObject { ["at"] = time.GetUtcNow().ToString("O"), ["message"] = Truncate(message) });
            while (log.Count > MaxLogEntries)
            {
                log.RemoveAt(0);
            }
        }

        Task SaveAsync(IReadOnlyCollection<object>? messages = null)
        {
            run.Log = log.ToJsonString();
            run.Outputs = outputs.ToJsonString();
            run.Variables = variables.ToJsonString();
            return messages is { Count: > 0 } ? outbox.SaveChangesAsync(db, [], messages, ct) : db.SaveChangesAsync(ct);
        }

        async Task FailAsync(string error, string? node)
        {
            Log($"Failed: {error}");
            run.Status = RunStatus.Failed;
            run.Error = Truncate(error);
            run.FailedNode = node;
            run.CompletedAt = time.GetUtcNow();
            await SaveAsync();
            LogRunFailed(run.Id, error);
        }

        async Task<bool> ContinueAsync(string node, string outcome)
        {
            var next = flow.Nodes.GetValueOrDefault(node)?.Next;
            var target = next?.GetValueOrDefault(outcome) ?? (outcome == "error" ? null : next?.GetValueOrDefault("done"));
            if (target is null)
            {
                run.Status = RunStatus.Completed;
                run.CompletedAt = time.GetUtcNow();
                Log("Completed");
                await SaveAsync();
                return false;
            }

            run.Node = target;
            run.StepExecutionId = null;
            await SaveAsync();
            return true;
        }

        // Stops the run on a bookmark (saved with the run, and with messages, e.g. the one that resumes a completed wait).
        Task WaitAsync(WorkflowBookmark bookmark, IReadOnlyCollection<object>? messages = null)
        {
            run.Status = RunStatus.Waiting;
            run.WaitingOn = bookmark.Id;
            return SaveAsync(messages);
        }

        WorkflowBookmark NewBookmark(string node, string kind, string key, DateTimeOffset? resumeAt)
        {
            var now = time.GetUtcNow();
            var bookmark = new WorkflowBookmark
            {
                Id = Ids.New(),
                TenantId = run.TenantId,
                RunId = run.Id,
                Node = node,
                Kind = kind,
                Key = key,
                ResumeAtUnixMs = resumeAt?.ToUnixTimeMilliseconds(),
                CreatedAt = now,
                CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            };
            db.Bookmarks.Add(bookmark);
            return bookmark;
        }

        // A failed node continues on its error port if connected; else the run fails there.
        async Task<bool> FailedAsync(string id, FlowNode node, string error)
        {
            if (node.Next?.GetValueOrDefault("error") is not null)
            {
                outputs[id] = new JsonObject { ["error"] = error };
                Log($"{id}: {error}");
                return await ContinueAsync(id, "error");
            }

            await FailAsync($"{id} ({node.Activity}): {error}", id);
            return false;
        }

        TokenScope? scope = null;
        async Task<TokenScope> ScopeAsync()
        {
            if (scope is null)
            {
                ScopeItem? item = null;
                string? listName = null;
                if (run.ListId is { } listId && await items.FindListAsync(reader, run.WorkspaceId, listId, ct) is { } list)
                {
                    listName = list.Name;
                    if (run.ItemId is { } itemId && await items.GetAsync(reader, run.WorkspaceId, listId, itemId, ct) is { } current)
                    {
                        item = new ScopeItem(current.Id, listId, current.Fields, current.CreatedAt, current.UpdatedAt);
                    }
                }

                scope = new TokenScope(item, listName, outputs, variables, data);
            }

            return scope;
        }

        async Task<bool> ScriptAsync(string id, FlowNode node)
        {
            if (outputs[id] is not JsonObject { } state || state["plan"] is not JsonArray)
            {
                var current = await ScopeAsync();
                var outcome = scripts.Run(ScriptRunner.Code(node.Inputs ?? [])!, run.TenantId, run.WorkspaceId, run.StepExecutionId!.Value, current.Item, current.ListName, outputs, variables, data, ct);
                foreach (var line in outcome.Log.Take(20))
                {
                    Log($"{id}: {line}");
                }

                if (outcome.Error is { } failure)
                {
                    return await FailedAsync(id, node, failure);
                }

                variables = outcome.Variables;
                state = new JsonObject { ["plan"] = outcome.Plan, ["applied"] = 0, ["result"] = outcome.Result };
                outputs[id] = state;
                if (outputs.ToJsonString().Length > MaxStateLength || variables.ToJsonString().Length > MaxStateLength)
                {
                    await FailAsync($"{id}: the script's result, variables or planned writes are too large.", id);
                    return false;
                }

                // The plan is saved before it is applied: after a crash the writes are applied, the script does not run again.
                await SaveAsync();
            }

            var plan = (JsonArray)state["plan"]!;
            var created = new JsonArray();
            int updated = 0, deleted = 0;
            for (var index = 0; index < plan.Count; index++)
            {
                var write = (JsonObject)plan[index]!;
                var op = write["op"]!.GetValue<string>();
                var listId = Guid.Parse(write["listId"]!.GetValue<string>());
                var target = Guid.Parse(write["id"]!.GetValue<string>());
                if (index >= state["applied"]!.GetValue<int>())
                {
                    var result = op switch
                    {
                        "create" => await items.CreateAsync(actor, run.WorkspaceId, listId, target, (JsonObject)write["fields"]!.DeepClone(), ct),
                        "update" => await items.UpdateAsync(actor, run.WorkspaceId, listId, target, (JsonObject)write["fields"]!.DeepClone(), ct),
                        _ => await items.DeleteAsync(actor, run.WorkspaceId, listId, target, ct),
                    };
                    if (!result.Succeeded && !(op == "delete" && result.Status == ListItemStatus.NotFound))
                    {
                        return await FailedAsync(id, node, $"write {index + 1} ({op} in {write["list"]}): {result.Describe()}");
                    }

                    state["applied"] = index + 1;
                }

                switch (op)
                {
                    case "create":
                        created.Add(item: JsonValue.Create(target.ToString()));
                        break;
                    case "update":
                        updated++;
                        break;
                    default:
                        deleted++;
                        break;
                }
            }

            outputs[id] = new JsonObject { ["result"] = state["result"]?.DeepClone(), ["created"] = created, ["updated"] = updated, ["deleted"] = deleted };
            Log($"{id}: script done ({plan.Count} write(s))");
            return await ContinueAsync(id, "done");
        }

        if (run.Node is null)
        {
            run.Node = flow.Start;
            Log($"Started ({run.Trigger})");
            await SaveAsync();
        }

        if (run.Status == RunStatus.Waiting)
        {
            var bookmark = run.WaitingOn is { } waitingOn ? await WaitQueries.BookmarkByIdAsync(db, run.TenantId, waitingOn, ct) : null;
            if (bookmark is null)
            {
                await FailAsync("The wait of the run no longer exists.", run.Node);
                return;
            }

            if (bookmark.CompletedAtUnixMs is null)
            {
                return; // Not over yet (e.g. a duplicate message).
            }

            run.Status = RunStatus.Running;
            run.WaitingOn = null;
            if (bookmark.RunAgain || bookmark.Kind == BookmarkKinds.Retry)
            {
                // The node runs again (same execution id); a run-again activity looks up what it waited for itself.
                Log($"{bookmark.Node}: running again");
                await SaveAsync();
            }
            else
            {
                var payload = bookmark.Payload is { } text ? JsonNode.Parse(text) as JsonObject ?? [] : [];
                outputs[bookmark.Node] = payload;
                var outcome = payload["outcome"] is JsonValue value && value.TryGetValue<string>(out var named) ? named : "done";
                if (bookmark.Kind != BookmarkKinds.Delay)
                {
                    Log($"{bookmark.Node}: {outcome}");
                }

                if (!await ContinueAsync(bookmark.Node, outcome))
                {
                    return;
                }
            }
        }

        for (var executed = 0; ; executed++)
        {
            if (run.NodesRun >= MaxNodesPerRun)
            {
                await FailAsync($"The run went through more than {MaxNodesPerRun} nodes.", run.Node);
                return;
            }

            if (executed >= MaxNodesPerExecution)
            {
                // Continue in a new message: other runs get their turn.
                await SaveAsync([new ResumeRun(run.TenantId, run.Id)]);
                return;
            }

            var id = run.Node!;
            if (!flow.Nodes.TryGetValue(id, out var node))
            {
                await FailAsync($"The node '{id}' does not exist.", id);
                return;
            }

            run.NodesRun++;
            var inputs = node.Inputs ?? [];

            // The execution id is saved before the node runs, so a repeat after a crash gets the same one.
            if (run.StepExecutionId is null)
            {
                run.StepExecutionId = Ids.New();
                await SaveAsync();
            }

            bool running;
            switch (node.Activity)
            {
                case FlowActivities.End:
                    running = await ContinueAsync(id, "end");
                    break;

                case FlowActivities.Fail:
                    await FailAsync(DefinitionValidator.Text(inputs, "message") is { } message ? tokens.Expand(message, await ScopeAsync()) : $"{id}: failed.", id);
                    return;

                case FlowActivities.SetVariable:
                    var name = DefinitionValidator.Text(inputs, "name")!;
                    variables[name] = inputs["value"] is JsonValue text && text.TryGetValue<string>(out var template)
                        ? tokens.Value(template, await ScopeAsync())
                        : inputs["value"]?.DeepClone();
                    outputs[id] = new JsonObject { ["value"] = variables[name]?.DeepClone() };
                    running = await ContinueAsync(id, "done");
                    break;

                case FlowActivities.If:
                    bool holds;
                    if (DefinitionValidator.Text(inputs, "filter") is { } filter)
                    {
                        if (run.ListId is not { } listId || run.ItemId is not { } itemId)
                        {
                            running = await FailedAsync(id, node, "A filter needs the run's item.");
                            break;
                        }

                        var (matches, error) = await conditions.MatchesAsync(run.TenantId, run.WorkspaceId, listId, itemId, tokens.Expand(filter, await ScopeAsync()), ct);
                        if (error is not null)
                        {
                            running = await FailedAsync(id, node, error);
                            break;
                        }

                        holds = matches;
                    }
                    else
                    {
                        var current = await ScopeAsync();
                        holds = Comparison.Holds(
                            tokens.Expand(DefinitionValidator.Text(inputs, "left") ?? "", current),
                            DefinitionValidator.Text(inputs, "op") ?? "eq",
                            tokens.Expand(inputs["right"] is JsonValue right && right.TryGetValue<string>(out var r) ? r : "", current));
                    }

                    outputs[id] = new JsonObject { ["result"] = holds };
                    running = await ContinueAsync(id, holds ? "true" : "false");
                    break;

                case FlowActivities.Script:
                    running = await ScriptAsync(id, node);
                    break;

                case FlowActivities.Delay:
                    var wait = TimeSpan.FromHours(ActivityInputs.Number(inputs, "hours") ?? 0) + TimeSpan.FromMinutes(ActivityInputs.Number(inputs, "minutes") ?? 0);
                    Log($"{id}: waiting {wait}");
                    await WaitAsync(NewBookmark(id, BookmarkKinds.Delay, $"{run.Id:N}:{run.StepExecutionId:N}", time.GetUtcNow() + wait));
                    return;

                case FlowActivities.Approval:
                    if (await ApprovalAsync(id, node) is not { } approval)
                    {
                        running = await FailedAsync(id, node, "No assignee could be found.");
                        break;
                    }

                    // The request, the wait and the notification are saved together: a decision always finds the run
                    // waiting for it, and the assignees are always told.
                    var key = RunService.ApprovalKey(approval.Id);
                    var waitFor = await WaitQueries.BookmarkAsync(db, run.TenantId, BookmarkKinds.Approval, key, ct)
                        ?? NewBookmark(id, BookmarkKinds.Approval, key, null);
                    Log($"{id}: waiting for approval");
                    await WaitAsync(waitFor, [new NotifyApproval(run.TenantId, approval.Id, false)]);
                    return;

                default:
                    running = await ActionAsync(id, node);
                    break;
            }

            if (!running)
            {
                return;
            }

            // Values of the item may have changed.
            scope = null;
        }

        // The pending request of the node (reused when it runs again), or a new one (saved with the wait).
        async Task<ApprovalRequest?> ApprovalAsync(string id, FlowNode node)
        {
            if (await WaitQueries.PendingOfNodeAsync(db, run.TenantId, run.Id, id, ct) is { } existing)
            {
                return existing;
            }

            var inputs = node.Inputs ?? [];
            var context = Context(node, null);
            var assignees = (await recipients.ResolveAsync(ActivityInputs.Texts(inputs, "assignees") ?? [], context, ct)).Users;
            if (assignees.Count == 0)
            {
                return null;
            }

            var escalateTo = (await recipients.ResolveAsync(ActivityInputs.Texts(inputs, "escalateTo") ?? [], context, ct)).Users;
            var title = tokens.Expand(ActivityInputs.Text(inputs, "title") ?? $"Approve {{title}} ({id})", await ScopeAsync());
            DateTimeOffset? due = ActivityInputs.Number(inputs, "dueInHours") is { } hours ? time.GetUtcNow().AddHours(hours) : null;
            var approval = new ApprovalRequest
            {
                Id = Ids.New(),
                TenantId = run.TenantId,
                RunId = run.Id,
                Node = id,
                WorkspaceId = run.WorkspaceId,
                ListId = run.ListId,
                ItemId = run.ItemId,
                Title = title.Length > 1000 ? title[..1000] : title,
                Assignees = UserIds.ToJson(assignees),
                EscalateTo = UserIds.ToJson(escalateTo.Except(assignees)),
                DueAt = due,
                DueAtUnixMs = due?.ToUnixTimeMilliseconds(),
                Status = ApprovalStatus.Pending,
            };
            db.Approvals.Add(approval);
            return approval;
        }

        WorkflowActivityContext Context(FlowNode node, WorkflowResumedWait? resumed) => new()
        {
            TenantId = run.TenantId,
            WorkspaceId = run.WorkspaceId,
            RunId = run.Id,
            ListId = run.ListId,
            ItemId = run.ItemId,
            Inputs = node.Inputs ?? [],
            ExecutionId = run.StepExecutionId!.Value,
            Actor = actor,
            StartedBy = run.StartedBy,
            Services = services,
            Resumed = resumed,
            ExpandAsync = async (template, _) => tokens.Expand(template, await ScopeAsync()),
            ResolveAsync = async (template, _) => tokens.Value(template, await ScopeAsync()),
        };

        async Task<bool> ActionAsync(string id, FlowNode node)
        {
            if (!_actions.TryGetValue(node.Activity, out var action))
            {
                await FailAsync($"{id}: the activity '{node.Activity}' is not available.", id);
                return false;
            }

            // A run-again wait of this node that ended is handed back to the activity until the node moves on.
            var resumed = await WaitQueries.ResumedAsync(db, run.TenantId, run.Id, id, ct);
            var context = Context(node, resumed is null ? null : new WorkflowResumedWait(
                resumed.Kind, resumed.Key, resumed.Data is { } data ? JsonNode.Parse(data) as JsonObject : null,
                resumed.Payload is { } payload ? JsonNode.Parse(payload) as JsonObject : null));
            WorkflowActivityResult result;
            try
            {
                result = await action.ExecuteAsync(context, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogActivityThrew(exception, id, run.Id);
                result = WorkflowActivityResult.Fail(exception.Message);
            }

            void Consumed()
            {
                if (resumed is not null)
                {
                    db.Bookmarks.Remove(resumed);
                }
            }

            if (!result.Succeeded)
            {
                Consumed();
                return await FailedAsync(id, node, result.Error ?? "failed");
            }

            if (result.Waiting is { } wait)
            {
                return await WaitForAsync(id, node, wait, resumed);
            }

            Consumed();
            outputs[id] = result.Output ?? [];
            Log($"{id}: {node.Activity} done");
            return await ContinueAsync(id, result.Outcome ?? "done");
        }

        // The activity waits for something else to complete (kind, key): a bookmark of this node. A completion that came
        // first (unclaimed) is taken over and resumes the run right away. False: the run stopped.
        async Task<bool> WaitForAsync(string id, FlowNode node, WorkflowWait wait, WorkflowBookmark? resumed)
        {
            try
            {
                WorkflowBookmarks.CheckWait(wait.Kind, wait.Key);
            }
            catch (ArgumentException exception)
            {
                return await FailedAsync(id, node, exception.Message);
            }

            var existing = await WaitQueries.BookmarkAsync(db, run.TenantId, wait.Kind, wait.Key, ct);
            if (existing is not null && existing.RunId != WorkflowBookmarks.Unclaimed && !(existing.RunId == run.Id && existing.Node == id))
            {
                return await FailedAsync(id, node, $"The wait {wait.Kind} '{wait.Key}' belongs to another run.");
            }

            if (resumed is not null && resumed != existing)
            {
                db.Bookmarks.Remove(resumed);
            }

            var waitOn = existing ?? NewBookmark(id, wait.Kind, wait.Key, wait.ResumeAt);
            if (existing is { CompletedAtUnixMs: not null } && existing.RunId == run.Id)
            {
                // The node ran again and waits for the same thing again (e.g. the next poll): wait anew.
                existing.CompletedAt = null;
                existing.CompletedAtUnixMs = null;
                existing.Payload = null;
                existing.ResumeAtUnixMs = wait.ResumeAt?.ToUnixTimeMilliseconds();
            }

            waitOn.RunId = run.Id;
            waitOn.Node = id;
            waitOn.RunAgain = wait.RunAgain;
            waitOn.Data = wait.Data?.ToJsonString();
            if (waitOn.Data?.Length > MaxStateLength)
            {
                await FailAsync($"{id}: the data of the wait is too large.", id);
                return false;
            }

            Log($"{id}: waiting ({wait.Kind})");
            await WaitAsync(waitOn, waitOn.CompletedAtUnixMs is null ? null : [new ResumeRun(run.TenantId, run.Id)]);
            return false;
        }
    }

    private static Task<WorkflowRun?> FindRunAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = runId;
        var ct = cancellationToken;
        return context.WorkflowRuns.Where(r => r.TenantId == tenant && r.Id == id).FirstOrDefaultAsync(ct);
    }

    private static string Truncate(string text) => text.Length > 2000 ? text[..2000] : text;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The workflow run {RunId} failed: {Error}")]
    private partial void LogRunFailed(Guid runId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The activity of node {Node} threw in run {RunId}.")]
    private partial void LogActivityThrew(Exception exception, string node, Guid runId);
}
