using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>Trigger types of workflows in the core.</summary>
public static class WorkflowTriggers
{
    /// <summary>Started by a person (<c>POST /v1.0/workflows/{id}/runs</c>), optionally on an item.</summary>
    public const string Manual = "manual";
    public const string ItemAdded = "itemAdded";
    public const string ItemUpdated = "itemUpdated";
    public const string ItemDeleted = "itemDeleted";

    /// <summary>An item came back from the recycle bin.</summary>
    public const string ItemRestored = "itemRestored";

    /// <summary>Prefix of workflow events: <c>wf.{key}.{event}</c> (ADR-0038).</summary>
    public const string WorkflowEventPrefix = "wf.";

    public static readonly string[] All = [Manual, ItemAdded, ItemUpdated, ItemDeleted, ItemRestored];

    /// <summary>Whether a trigger type is known: one of <see cref="All"/> or a workflow event.</summary>
    public static bool IsKnown(string type) => All.Contains(type, StringComparer.Ordinal) || WorkflowEvents.IsEventTrigger(type);
}

/// <summary>Events of workflows (ADR-0038): <c>wf.{key}.{event}</c>.</summary>
public static partial class WorkflowEvents
{
    /// <summary>Raised when a run ends as completed (data: <c>runId</c>, <c>status</c>).</summary>
    public const string Completed = "completed";

    /// <summary>Raised when a run ends as failed (data: <c>runId</c>, <c>status</c>, <c>error</c>).</summary>
    public const string Failed = "failed";

    /// <summary>Whether a trigger type is a workflow event: <c>wf.{key}.{event}</c> (the key may have dots, the event not).</summary>
    public static bool IsEventTrigger(string type) => EventTrigger().IsMatch(type);

    /// <summary>Why an event name raised by <c>event.raise</c> is not valid, or null.</summary>
    public static string? Check(string? name) => name switch
    {
        null or "" => "event is required.",
        Completed or Failed => $"{name} is raised when a run ends; choose another name.",
        _ when !EventName().IsMatch(name) => "event is a name of letters, digits, dashes and underscores (e.g. noText).",
        _ => null,
    };

    [GeneratedRegex(@"^wf\.[a-z0-9][a-z0-9._-]*\.[A-Za-z][A-Za-z0-9_-]{0,49}$")]
    private static partial Regex EventTrigger();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,49}$")]
    private static partial Regex EventName();
}

/// <summary>Keys of workflows (ADR-0038).</summary>
public static partial class WorkflowKeys
{
    public const int MaxLength = 100;

    /// <summary>A key made from a name: lower case letters, digits and dashes, e.g. <c>check-big-bills</c>.</summary>
    public static string FromName(string name)
    {
        var key = NotKey().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        key = key.Length > MaxLength ? key[..MaxLength].TrimEnd('-') : key;
        return key.Length == 0 ? "workflow" : key;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotKey();
}

/// <summary>
/// When a workflow runs: <c>type</c>, and for item triggers the <c>list</c> (by name, any list without it) and, for
/// updates, <c>changedFields</c> (one of them must change).
/// </summary>
public sealed record WorkflowTrigger(string Type, string? List = null, IReadOnlyList<string>? ChangedFields = null);

/// <summary>Runs of one workflow on the same item (ADR-0037).</summary>
public static class RunConcurrency
{
    /// <summary>Both run (the default).</summary>
    public const string Parallel = "parallel";

    /// <summary>The new run does not start.</summary>
    public const string Skip = "skip";

    /// <summary>The running one is cancelled, the new one starts.</summary>
    public const string Replace = "replace";

    public static readonly string[] All = [Parallel, Skip, Replace];
}

/// <summary>
/// A workflow version: its trigger (or several in <c>triggers</c>), an OData <c>condition</c> on the item checked when an
/// item trigger fires, the initial <c>variables</c>, and its body: a <c>flow</c> of nodes (ADR-0036) or <c>steps</c> (a
/// sequence with conditions, compiled into a flow). <c>concurrency</c> says what happens when a run on an item starts
/// while another run of the workflow on that item is going (<see cref="RunConcurrency"/>).
/// </summary>
public sealed record WorkflowSpec(
    WorkflowTrigger? Trigger = null,
    IReadOnlyList<WorkflowTrigger>? Triggers = null,
    string? Condition = null,
    JsonObject? Variables = null,
    FlowDefinition? Flow = null,
    IReadOnlyList<WorkflowStep>? Steps = null,
    string? Concurrency = null)
{
    public const int MaxTriggers = 10;

    [JsonIgnore]
    public IReadOnlyList<WorkflowTrigger> AllTriggers => Triggers ?? (Trigger is null ? [] : [Trigger]);

    /// <summary>The flow a run executes: the definition's flow, or its steps compiled into one.</summary>
    [JsonIgnore]
    public FlowDefinition RunFlow => Flow ?? StepCompiler.Compile(Steps ?? []);
}

/// <summary>The flow: the <c>start</c> node and the nodes by id.</summary>
public sealed record FlowDefinition(string Start, IReadOnlyDictionary<string, FlowNode> Nodes)
{
    public const int MaxNodes = 200;
    public const int MaxNodeId = 200;
}

/// <summary>
/// A node: an <c>activity</c>, its <c>inputs</c> (texts may hold tokens), the next node per outcome port, and an optional
/// <c>retry</c> policy for failures.
/// </summary>
public sealed record FlowNode(string Activity, JsonObject? Inputs = null, IReadOnlyDictionary<string, string>? Next = null, RetryPolicy? Retry = null);

/// <summary>How often a failing node is tried again (<c>attempts</c>, at most 10) and how long to wait in between (<c>delayMinutes</c>, default 1).</summary>
public sealed record RetryPolicy(int Attempts, double? DelayMinutes = null);

/// <summary>
/// A step of the <c>steps</c> form: <c>action</c> (<c>action</c>, <c>inputs</c>), <c>approval</c> (<c>name</c>,
/// <c>assignees</c>, <c>title</c>, <c>dueInHours</c>, <c>escalateTo</c>), <c>delay</c> (<c>hours</c>) or <c>condition</c>
/// (<c>step</c> + <c>is</c>: an approval's outcome, or <c>filter</c>: OData on the item; <c>then</c> and <c>else</c>).
/// </summary>
public sealed record WorkflowStep(
    string Type,
    string? Name = null,
    string? Action = null,
    JsonObject? Inputs = null,
    IReadOnlyList<string>? Assignees = null,
    string? Title = null,
    double? DueInHours = null,
    IReadOnlyList<string>? EscalateTo = null,
    double? Hours = null,
    string? Step = null,
    string? Is = null,
    string? Filter = null,
    IReadOnlyList<WorkflowStep>? Then = null,
    IReadOnlyList<WorkflowStep>? Else = null);

/// <summary>Kinds of steps of the <c>steps</c> form.</summary>
public static class StepTypes
{
    public const string Action = "action";
    public const string Approval = "approval";
    public const string Delay = "delay";
    public const string Condition = "condition";

    public static readonly string[] All = [Action, Approval, Delay, Condition];
}

/// <summary>Activities the engine runs itself; actions (<c>item.create</c>, …) come from the catalog (<see cref="IWorkflowActivity"/>).</summary>
public static class FlowActivities
{
    /// <summary>
    /// Continues with <c>true</c> or <c>false</c>: <c>filter</c> (OData on the run's item), <c>step</c> + <c>is</c> (an
    /// approval node's outcome) or <c>left</c>, <c>op</c> (<see cref="Comparison.Operators"/>) and <c>right</c> (texts with tokens).
    /// </summary>
    public const string If = "if";

    /// <summary>Sets the variable <c>name</c> to <c>value</c> (a text with tokens, or any JSON value).</summary>
    public const string SetVariable = "setVariable";

    /// <summary>
    /// Runs JavaScript (<c>code</c>, ADR-0037) that reads the tenant's lists and plans writes to them (<c>items</c>); the
    /// plan is saved with the run, then applied. Output: <c>result</c>, <c>created</c>, <c>updated</c>, <c>deleted</c>.
    /// </summary>
    public const string Script = "script";

    /// <summary>Ends the run as completed.</summary>
    public const string End = "end";

    /// <summary>Ends the run as failed with <c>message</c>.</summary>
    public const string Fail = "fail";

    /// <summary>Waits <c>hours</c> and/or <c>minutes</c>, then continues with <c>done</c>.</summary>
    public const string Delay = "delay";

    /// <summary>
    /// Asks <c>assignees</c> (people inputs) to approve: <c>title</c> (tokens), optional <c>dueInHours</c> and
    /// <c>escalateTo</c> (added when overdue). Continues with <c>approved</c> or <c>rejected</c>; the output holds the
    /// <c>outcome</c>, <c>decidedBy</c> and <c>comment</c>.
    /// </summary>
    public const string Approval = "approval";

    /// <summary>
    /// Runs the nodes on its <c>item</c> port for each element of <c>items</c> (an array, or one token such as
    /// <c>{step:read.json.lines}</c>) or of the items a <c>query</c> (<c>list</c>, <c>filter</c>) finds; the element is the
    /// variable <c>as</c> (default <c>item</c>). The body leads back to the node; after the last element it continues with <c>done</c>.
    /// </summary>
    public const string ForEach = "forEach";

    /// <summary>Raises the workflow event <c>wf.{key}.{event}</c> with <c>data</c> (tokens; one token keeps its type).</summary>
    public const string Raise = "event.raise";

    public const int MaxForEachItems = 500;

    public static readonly string[] All = [If, SetVariable, Script, End, Fail, Delay, Approval, ForEach, Raise];

    /// <summary>The outcome ports of an activity; actions have <c>done</c> and <c>error</c> (and their own outcomes).</summary>
    public static IReadOnlySet<string> Ports(string activity, IWorkflowActivity? action) => activity switch
    {
        If => new HashSet<string>(["true", "false", "error"], StringComparer.Ordinal),
        SetVariable or Delay or Raise => new HashSet<string>(["done"], StringComparer.Ordinal),
        Script => new HashSet<string>(["done", "error"], StringComparer.Ordinal),
        End or Fail => new HashSet<string>([], StringComparer.Ordinal),
        Approval => new HashSet<string>(["approved", "rejected", "done"], StringComparer.Ordinal),
        ForEach => new HashSet<string>(["item", "done", "error"], StringComparer.Ordinal),
        _ => new HashSet<string>(["done", "error", .. action?.Outcomes ?? []], StringComparer.Ordinal),
    };

    /// <summary>
    /// The nodes a <c>forEach</c> runs for each element: those reached from its <c>item</c> port without passing through it
    /// (loops inside it start again for each of its elements).
    /// </summary>
    public static HashSet<string> LoopBody(FlowDefinition flow, string forEach)
    {
        var body = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (flow.Nodes.GetValueOrDefault(forEach)?.Next?.GetValueOrDefault("item") is { } first)
        {
            pending.Push(first);
        }

        while (pending.TryPop(out var id))
        {
            if (id == forEach || !body.Add(id) || !flow.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            foreach (var next in node.Next?.Values ?? [])
            {
                pending.Push(next);
            }
        }

        return body;
    }
}

/// <summary>Comparisons of <c>if</c> nodes; numbers compare as numbers.</summary>
public static class Comparison
{
    public static readonly string[] Operators = ["eq", "ne", "gt", "ge", "lt", "le", "contains", "empty", "notEmpty"];

    public static bool Holds(string left, string op, string right)
    {
        var numbers = double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
            & double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var r);
        var order = numbers ? l.CompareTo(r) : string.Compare(left, right, StringComparison.Ordinal);
        return op switch
        {
            "eq" => numbers ? order == 0 : string.Equals(left, right, StringComparison.Ordinal),
            "ne" => numbers ? order != 0 : !string.Equals(left, right, StringComparison.Ordinal),
            "gt" => order > 0,
            "ge" => order >= 0,
            "lt" => order < 0,
            "le" => order <= 0,
            "contains" => left.Contains(right, StringComparison.OrdinalIgnoreCase),
            "empty" => string.IsNullOrWhiteSpace(left),
            "notEmpty" => !string.IsNullOrWhiteSpace(left),
            _ => false,
        };
    }
}

/// <summary>Definition JSON: source-generated, without nulls (ADR-0039).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkflowSpec))]
internal sealed partial class WorkflowJson : JsonSerializerContext
{
    public static string Serialize(WorkflowSpec spec) => JsonSerializer.Serialize(spec, Default.WorkflowSpec);

    public static WorkflowSpec Deserialize(string json) => JsonSerializer.Deserialize(json, Default.WorkflowSpec)!;

    /// <summary>A definition from the API; null with the reason when it is not one.</summary>
    public static (WorkflowSpec? Spec, string? Error) Read(JsonObject definition)
    {
        try
        {
            return (definition.Deserialize(Default.WorkflowSpec), null);
        }
        catch (JsonException exception)
        {
            return (null, exception.Message);
        }
    }
}

/// <summary>
/// Compiles steps into a flow: each step becomes a node named like the step (or <c>step 1</c>, <c>Name.then.1</c>, …),
/// connected by <c>done</c>; a condition becomes an <c>if</c> node whose <c>true</c> and <c>false</c> ports lead into its
/// branches, which continue with the step after the condition.
/// </summary>
internal static class StepCompiler
{
    public const int MaxSteps = 100;
    public const int MaxDepth = 5;

    public static FlowDefinition Compile(IReadOnlyList<WorkflowStep> steps)
    {
        var nodes = new Dictionary<string, FlowNode>(StringComparer.Ordinal);
        var named = new HashSet<string>(Names(steps), StringComparer.Ordinal);

        string Id(WorkflowStep step, string generated)
        {
            if (step.Name is { } name)
            {
                return name;
            }

            var id = generated;
            for (var i = 2; named.Contains(id) || nodes.ContainsKey(id); i++)
            {
                id = $"{generated} ({i})";
            }

            return id;
        }

        // Emits the steps backwards, so each node knows the node after it; returns the first node's id.
        string? Emit(IReadOnlyList<WorkflowStep> steps, string path, string? after)
        {
            var next = after;
            for (var index = steps.Count - 1; index >= 0; index--)
            {
                var step = steps[index];
                var id = Id(step, $"{path}{index + 1}");
                var done = next is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["done"] = next };
                nodes[id] = step.Type switch
                {
                    StepTypes.Action => new FlowNode(step.Action ?? "", step.Inputs?.DeepClone().AsObject(), done),
                    StepTypes.Approval => new FlowNode(FlowActivities.Approval, Object(
                        ("assignees", Strings(step.Assignees)), ("title", step.Title), ("dueInHours", step.DueInHours), ("escalateTo", Strings(step.EscalateTo))), done),
                    StepTypes.Delay => new FlowNode(FlowActivities.Delay, Object(("hours", step.Hours)), done),
                    _ => ConditionNode(step, id, next),
                };
                next = id;
            }

            return next;
        }

        FlowNode ConditionNode(WorkflowStep step, string id, string? after)
        {
            var then = Emit(step.Then ?? [], $"{id}.then.", after);
            var otherwise = Emit(step.Else ?? [], $"{id}.else.", after);
            var ports = new Dictionary<string, string>(StringComparer.Ordinal);
            if (then is not null)
            {
                ports["true"] = then;
            }

            if (otherwise is not null)
            {
                ports["false"] = otherwise;
            }

            var inputs = step.Filter is { } filter ? Object(("filter", filter)) : Object(("step", step.Step), ("is", step.Is));
            return new FlowNode(FlowActivities.If, inputs, ports.Count == 0 ? null : ports);
        }

        var start = Emit(steps, "step ", null);
        return new FlowDefinition(start ?? "", nodes);
    }

    /// <summary>Checks what the compiled flow cannot tell: step types, names, nesting and approval references.</summary>
    public static List<string> Validate(IReadOnlyList<WorkflowStep> steps)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var approvals = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        void Check(IReadOnlyList<WorkflowStep> steps, string path, int depth)
        {
            if (depth > MaxDepth)
            {
                errors.Add($"{path}: conditions can be nested at most {MaxDepth} levels.");
                return;
            }

            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var at = $"{path}[{index}]";
                if (++count > MaxSteps)
                {
                    errors.Add($"A workflow has at most {MaxSteps} steps.");
                    return;
                }

                if (step.Name is { } name && (!names.Add(name) || name.Length > FlowDefinition.MaxNodeId))
                {
                    errors.Add(name.Length > FlowDefinition.MaxNodeId ? $"{at}: step names have at most {FlowDefinition.MaxNodeId} characters." : $"{at}: the step name '{name}' is used twice.");
                }

                switch (step.Type)
                {
                    case StepTypes.Action:
                        if (string.IsNullOrWhiteSpace(step.Action))
                        {
                            errors.Add($"{at}: action is required.");
                        }

                        break;
                    case StepTypes.Approval:
                        if (step.Name is null)
                        {
                            errors.Add($"{at}: approval steps need a name (conditions refer to their outcome).");
                        }
                        else
                        {
                            approvals.Add(step.Name);
                        }

                        break;
                    case StepTypes.Delay:
                        if (step.Hours is not > 0)
                        {
                            errors.Add($"{at}: hours must be positive.");
                        }

                        break;
                    case StepTypes.Condition:
                        if ((step.Step is null) == (step.Filter is null))
                        {
                            errors.Add($"{at}: use either step (with is) or filter.");
                        }
                        else if (step.Step is { } target && !approvals.Contains(target))
                        {
                            errors.Add($"{at}: step '{target}' is not an earlier approval step.");
                        }

                        Check(step.Then ?? [], $"{at}.then", depth + 1);
                        Check(step.Else ?? [], $"{at}.else", depth + 1);
                        break;
                    default:
                        errors.Add($"{at}: unknown step type '{step.Type}' (use {string.Join(", ", StepTypes.All)}).");
                        break;
                }
            }
        }

        if (steps.Count == 0)
        {
            errors.Add("At least one step is required.");
        }

        Check(steps, "steps", 0);
        return errors;
    }

    private static IEnumerable<string> Names(IReadOnlyList<WorkflowStep> steps) =>
        steps.SelectMany(s => (s.Name is { } name ? [name] : Array.Empty<string>()).Concat(Names(s.Then ?? [])).Concat(Names(s.Else ?? [])));

    private static JsonArray? Strings(IReadOnlyList<string>? values) => values is null ? null : [.. values.Select(v => (JsonNode)JsonValue.Create(v))];

    private static JsonObject Object(params (string Name, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            switch (value)
            {
                case JsonNode node:
                    result[name] = node;
                    break;
                case string text:
                    result[name] = text;
                    break;
                case double number:
                    result[name] = number;
                    break;
            }
        }

        return result;
    }
}

/// <summary>Checks a definition when it is saved.</summary>
internal static partial class DefinitionValidator
{
    public static List<string> Validate(WorkflowSpec spec, IReadOnlyDictionary<string, IWorkflowActivity> actions)
    {
        var errors = new List<string>();
        var triggers = spec.AllTriggers;
        if (triggers.Count is 0 or > WorkflowSpec.MaxTriggers)
        {
            errors.Add($"A workflow has 1 to {WorkflowSpec.MaxTriggers} triggers (trigger or triggers).");
        }

        foreach (var trigger in triggers)
        {
            if (!WorkflowTriggers.IsKnown(trigger.Type ?? ""))
            {
                errors.Add($"Unknown trigger type '{trigger.Type}'. Use one of: {string.Join(", ", WorkflowTriggers.All)}, or wf.{{key}}.{{event}}.");
            }

            if (trigger.ChangedFields is { Count: > 0 } && trigger.Type != WorkflowTriggers.ItemUpdated)
            {
                errors.Add("changedFields applies to itemUpdated triggers only.");
            }
        }

        if (spec.Concurrency is { } concurrency && !RunConcurrency.All.Contains(concurrency, StringComparer.Ordinal))
        {
            errors.Add($"concurrency must be one of: {string.Join(", ", RunConcurrency.All)}.");
        }

        if ((spec.Flow is null) == (spec.Steps is null))
        {
            errors.Add("Use either flow (start and nodes) or steps.");
            return errors;
        }

        if (spec.Steps is { } steps)
        {
            errors.AddRange(StepCompiler.Validate(steps));
            if (errors.Count > 0)
            {
                return errors;
            }
        }

        var flow = spec.RunFlow;
        if (flow.Nodes is not { Count: > 0 })
        {
            errors.Add("flow with start and nodes is required.");
            return errors;
        }

        if (flow.Nodes.Count > FlowDefinition.MaxNodes)
        {
            errors.Add($"A flow has at most {FlowDefinition.MaxNodes} nodes.");
        }

        if (!flow.Nodes.ContainsKey(flow.Start ?? ""))
        {
            errors.Add($"The start node '{flow.Start}' does not exist.");
        }

        foreach (var (id, node) in flow.Nodes)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > FlowDefinition.MaxNodeId)
            {
                errors.Add($"Node ids have 1 to {FlowDefinition.MaxNodeId} characters.");
            }

            var inputs = node.Inputs ?? [];
            actions.TryGetValue(node.Activity ?? "", out var action);
            if (action is null && !FlowActivities.All.Contains(node.Activity, StringComparer.Ordinal))
            {
                errors.Add($"{id}: unknown activity '{node.Activity}'.");
                continue;
            }

            var ports = FlowActivities.Ports(node.Activity!, action);
            foreach (var (port, target) in node.Next ?? new Dictionary<string, string>())
            {
                if (!ports.Contains(port))
                {
                    errors.Add($"{id}: {node.Activity} has no outcome '{port}' (it has: {string.Join(", ", ports)}).");
                }

                if (!flow.Nodes.ContainsKey(target))
                {
                    errors.Add($"{id}: the next node '{target}' does not exist.");
                }
            }

            if (node.Retry is { } retry && (retry.Attempts is < 1 or > 10 || retry.DelayMinutes is < 0 or > 1440))
            {
                errors.Add($"{id}: retry needs 1 to 10 attempts and a delay of 0 to 1440 minutes.");
            }

            errors.AddRange((node.Activity switch
            {
                FlowActivities.If => ValidateIf(flow, inputs),
                FlowActivities.SetVariable => Text(inputs, "name") is null ? ["setVariable needs name."] : [],
                FlowActivities.Script => ScriptRunner.Check(ScriptRunner.Code(inputs)) is { } problem ? [problem] : [],
                FlowActivities.End or FlowActivities.Fail => [],
                FlowActivities.Delay => ActivityInputs.Number(inputs, "hours") is null && ActivityInputs.Number(inputs, "minutes") is null
                    ? ["delay needs hours or minutes (numbers)."]
                    : [],
                FlowActivities.Approval => ActivityInputs.Texts(inputs, "assignees") is { Count: > 0 } ? [] : ["approval needs assignees."],
                FlowActivities.ForEach => ValidateForEach(node, inputs),
                FlowActivities.Raise => ValidateRaise(inputs),
                _ => action!.Validate(inputs),
            }).Select(e => $"{id}: {e}"));
        }

        if (errors.Count == 0)
        {
            errors.AddRange(Unreachable(flow));

            // A loop inside a loop needs its own variable, or it would overwrite the outer loop's element.
            static string Variable(FlowNode node) => Text(node.Inputs ?? [], "as") ?? "item";
            foreach (var (id, node) in flow.Nodes.Where(n => n.Value.Activity == FlowActivities.ForEach))
            {
                errors.AddRange(FlowActivities.LoopBody(flow, id)
                    .Where(b => flow.Nodes[b].Activity == FlowActivities.ForEach && Variable(flow.Nodes[b]) == Variable(node))
                    .Select(inner => $"{inner}: a loop inside the loop '{id}' needs its own variable (as), not '{Variable(node)}'."));
            }
        }

        return errors;
    }

    private static IEnumerable<string> ValidateIf(FlowDefinition flow, JsonObject inputs)
    {
        var kinds = new[] { inputs["filter"] is not null, Text(inputs, "step") is not null, Text(inputs, "op") is not null };
        if (kinds.Count(k => k) != 1)
        {
            yield return $"if needs one of: filter, step (with is), or left, op ({string.Join(", ", Comparison.Operators)}) and right.";
        }
        else if (Text(inputs, "step") is { } step)
        {
            if (flow.Nodes.GetValueOrDefault(step)?.Activity != FlowActivities.Approval)
            {
                yield return $"'{step}' is not an approval node.";
            }

            if (Text(inputs, "is") is not (ApprovalOutcomes.Approved or ApprovalOutcomes.Rejected))
            {
                yield return "is must be approved or rejected.";
            }
        }
        else if (Text(inputs, "op") is { } op && !Comparison.Operators.Contains(op))
        {
            yield return $"op must be one of {string.Join(", ", Comparison.Operators)}.";
        }
    }

    private static IEnumerable<string> ValidateForEach(FlowNode node, JsonObject inputs)
    {
        var query = inputs["query"] as JsonObject;
        if ((inputs["items"] is null) == (query is null))
        {
            yield return "use either items (an array, or one token such as {step:read.json.lines}) or query (list, filter).";
        }
        else if (inputs["items"] is { } list && list is not JsonArray && !(list is JsonValue value && value.GetValueKind() == JsonValueKind.String))
        {
            yield return "items must be an array or a token.";
        }
        else if (query is not null && Text(query, "list") is null)
        {
            yield return "query needs list.";
        }

        if (Text(inputs, "as") is { } variable && !VariableName().IsMatch(variable))
        {
            yield return "as must be a name (letters, digits and _).";
        }

        if (node.Next?.ContainsKey("item") != true)
        {
            yield return "forEach needs an item port (the nodes to run for each element).";
        }
    }

    private static IEnumerable<string> ValidateRaise(JsonObject inputs)
    {
        if (WorkflowEvents.Check(Text(inputs, "event")) is { } error)
        {
            yield return error;
        }

        if (inputs["data"] is not (null or JsonObject))
        {
            yield return "data must be an object.";
        }
    }

    private static IEnumerable<string> Unreachable(FlowDefinition flow)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([flow.Start]);
        while (pending.TryPop(out var id))
        {
            if (reached.Add(id) && flow.Nodes.TryGetValue(id, out var node))
            {
                foreach (var next in node.Next?.Values ?? [])
                {
                    pending.Push(next);
                }
            }
        }

        return flow.Nodes.Keys.Where(id => !reached.Contains(id)).Select(id => $"{id}: the node cannot be reached from the start.");
    }

    public static string? Text(JsonObject inputs, string name) =>
        inputs[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}
