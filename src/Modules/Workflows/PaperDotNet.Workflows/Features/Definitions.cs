using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

    public static readonly string[] All = [Manual, ItemAdded, ItemUpdated, ItemDeleted];
}

/// <summary>
/// When a workflow runs: <c>type</c>, and for item triggers the <c>list</c> (by name, any list without it) and, for
/// updates, <c>changedFields</c> (one of them must change).
/// </summary>
public sealed record WorkflowTrigger(string Type, string? List = null, IReadOnlyList<string>? ChangedFields = null);

/// <summary>
/// A workflow version: its trigger (or several in <c>triggers</c>), an OData <c>condition</c> on the item checked when an
/// item trigger fires, the initial <c>variables</c>, and the <c>flow</c>. The core runs the flow form of ADR-0036; the
/// <c>steps</c> form, waits (approval, delay, retries) and schedules come later.
/// </summary>
public sealed record WorkflowSpec(
    WorkflowTrigger? Trigger = null,
    IReadOnlyList<WorkflowTrigger>? Triggers = null,
    string? Condition = null,
    JsonObject? Variables = null,
    FlowDefinition? Flow = null)
{
    public const int MaxTriggers = 10;

    [JsonIgnore]
    public IReadOnlyList<WorkflowTrigger> AllTriggers => Triggers ?? (Trigger is null ? [] : [Trigger]);
}

/// <summary>The flow: the <c>start</c> node and the nodes by id.</summary>
public sealed record FlowDefinition(string Start, IReadOnlyDictionary<string, FlowNode> Nodes)
{
    public const int MaxNodes = 200;
}

/// <summary>A node: an <c>activity</c>, its <c>inputs</c> (texts may hold tokens) and the next node per outcome port.</summary>
public sealed record FlowNode(string Activity, JsonObject? Inputs = null, IReadOnlyDictionary<string, string>? Next = null);

/// <summary>Activities the engine runs itself; actions (<c>item.create</c>, …) come from the catalog (<see cref="IWorkflowActivity"/>).</summary>
public static class FlowActivities
{
    /// <summary>
    /// Continues with <c>true</c> or <c>false</c>: <c>filter</c> (OData on the run's item) or <c>left</c>, <c>op</c>
    /// (<see cref="Comparison.Operators"/>) and <c>right</c> (texts with tokens).
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

    public static readonly string[] All = [If, SetVariable, Script, End, Fail, Delay, Approval];

    /// <summary>The outcome ports of an activity; actions have <c>done</c> and <c>error</c> (and their own outcomes).</summary>
    public static IReadOnlySet<string> Ports(string activity, IWorkflowActivity? action) => activity switch
    {
        If => new HashSet<string>(["true", "false", "error"], StringComparer.Ordinal),
        SetVariable => new HashSet<string>(["done"], StringComparer.Ordinal),
        Script => new HashSet<string>(["done", "error"], StringComparer.Ordinal),
        End or Fail => new HashSet<string>([], StringComparer.Ordinal),
        Delay => new HashSet<string>(["done"], StringComparer.Ordinal),
        Approval => new HashSet<string>(["approved", "rejected", "done"], StringComparer.Ordinal),
        _ => new HashSet<string>(["done", "error", .. action?.Outcomes ?? []], StringComparer.Ordinal),
    };
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

/// <summary>Checks a definition when it is saved.</summary>
internal static class DefinitionValidator
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
            if (!WorkflowTriggers.All.Contains(trigger.Type, StringComparer.Ordinal))
            {
                errors.Add($"Unknown trigger type '{trigger.Type}'. Use one of: {string.Join(", ", WorkflowTriggers.All)}.");
            }

            if (trigger.ChangedFields is { Count: > 0 } && trigger.Type != WorkflowTriggers.ItemUpdated)
            {
                errors.Add("changedFields applies to itemUpdated triggers only.");
            }
        }

        if (spec.Flow is not { } flow || flow.Nodes is not { Count: > 0 })
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

            errors.AddRange((node.Activity switch
            {
                FlowActivities.If => inputs["filter"] is not null || Text(inputs, "left") is not null && Comparison.Operators.Contains(Text(inputs, "op"))
                    ? []
                    : (IEnumerable<string>)[$"if needs filter, or left and op ({string.Join(", ", Comparison.Operators)}) and right."],
                FlowActivities.SetVariable => Text(inputs, "name") is null ? ["setVariable needs name."] : [],
                FlowActivities.Script => ScriptRunner.Check(ScriptRunner.Code(inputs)) is { } problem ? [problem] : [],
                FlowActivities.End or FlowActivities.Fail => [],
                FlowActivities.Delay => ActivityInputs.Number(inputs, "hours") is null && ActivityInputs.Number(inputs, "minutes") is null
                    ? ["delay needs hours or minutes (numbers)."]
                    : [],
                FlowActivities.Approval => ActivityInputs.Texts(inputs, "assignees") is { Count: > 0 } ? [] : ["approval needs assignees."],
                _ => action!.Validate(inputs),
            }).Select(e => $"{id}: {e}"));
        }

        return errors;
    }

    public static string? Text(JsonObject inputs, string name) =>
        inputs[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
