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
    IListItemStore items,
    ItemConditions conditions,
    TokenExpander tokens,
    ScriptRunner scripts,
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
        if (await FindRunAsync(db, tenantId, runId, ct) is not { Status: RunStatus.Running } run
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
                if (run.ListId is { } listId && await items.FindListAsync(run.TenantId, listId, ct) is { } list)
                {
                    listName = list.Name;
                    if (run.ItemId is { } itemId && await items.GetAsync(run.TenantId, listId, itemId, ct) is { } current)
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
                var outcome = scripts.Run(ScriptRunner.Code(node.Inputs ?? [])!, run.TenantId, run.StepExecutionId!.Value, current.Item, current.ListName, outputs, variables, data, ct);
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
                        "create" => await items.CreateAsync(actor, listId, target, (JsonObject)write["fields"]!.DeepClone(), ct),
                        "update" => await items.UpdateAsync(actor, listId, target, (JsonObject)write["fields"]!.DeepClone(), null, ct),
                        _ => await items.DeleteAsync(actor, listId, target, null, ct),
                    };
                    if (!result.Succeeded && !(op == "delete" && result.Status == ItemWriteStatus.NotFound))
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

                        var (matches, error) = await conditions.MatchesAsync(run.TenantId, listId, itemId, tokens.Expand(filter, await ScopeAsync()), ct);
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

        async Task<bool> ActionAsync(string id, FlowNode node)
        {
            if (!_actions.TryGetValue(node.Activity, out var action))
            {
                await FailAsync($"{id}: the activity '{node.Activity}' is not available.", id);
                return false;
            }

            var context = new WorkflowActivityContext
            {
                TenantId = run.TenantId,
                RunId = run.Id,
                ListId = run.ListId,
                ItemId = run.ItemId,
                Inputs = node.Inputs ?? [],
                ExecutionId = run.StepExecutionId!.Value,
                Actor = actor,
                Services = services,
                ExpandAsync = async (template, _) => tokens.Expand(template, await ScopeAsync()),
                ResolveAsync = async (template, _) => tokens.Value(template, await ScopeAsync()),
            };

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

            if (!result.Succeeded)
            {
                return await FailedAsync(id, node, result.Error ?? "failed");
            }

            outputs[id] = result.Output ?? [];
            Log($"{id}: {node.Activity} done");
            return await ContinueAsync(id, result.Outcome ?? "done");
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
