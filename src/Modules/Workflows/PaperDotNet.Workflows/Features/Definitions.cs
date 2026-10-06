using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// When a workflow runs: <c>type</c> is <c>manual</c> (started by a person), an item event (<c>itemAdded</c>,
/// <c>itemUpdated</c>, <c>itemDeleted</c>, <c>itemRestored</c>), <c>schedule</c>, <c>date</c>, a module trigger
/// (<c>document.processed</c>, <c>approval.decided</c>, <c>task.completed</c>, <c>comment.added</c>) or an extension
/// trigger. <c>list</c> and <c>contentType</c> narrow it by name; <c>changedFields</c> (updates) needs one of them to
/// change; <c>terms</c> (term paths <c>Group/Set/Term</c>) needs the item to have one of them or a term below.
/// Omit <c>list</c> to watch any list in the workspace.
/// <c>schedule</c> runs on <c>cron</c> (5 fields) in <c>timeZone</c> (default: the organization's). <c>date</c> runs for
/// each item of <c>list</c> when its date <c>field</c> plus <c>offsetHours</c> (negative: before) is reached. <c>manual</c>
/// may describe the <c>inputs</c> a person gives when starting it (a JSON Schema object; they become run variables).
/// <c>data</c> (module and extension triggers) needs the trigger's data to have these values, e.g. <c>{ "hasText": false }</c>.
/// </summary>
public sealed record WorkflowTrigger(
    string Type,
    string? List = null,
    string? ContentType = null,
    IReadOnlyList<string>? ChangedFields = null,
    IReadOnlyList<string>? Terms = null,
    string? Cron = null,
    string? TimeZone = null,
    string? Field = null,
    double? OffsetHours = null,
    JsonObject? Inputs = null,
    JsonObject? Data = null,
    string? Concurrency = null,
    WorkflowTriggerParameters? Parameters = null)
{
    /// <summary>Whether the trigger's data has every value of <see cref="Data"/> (true without <see cref="Data"/>).</summary>
    public bool MatchesData(JsonObject? data) =>
        Data is null || Data.All(wanted => data is not null && data.TryGetPropertyValue(wanted.Key, out var value) && JsonNode.DeepEquals(value, wanted.Value));
}

/// <summary>Conditions evaluated against the triggering item's transactional snapshots.</summary>
public sealed record WorkflowTriggerParameters(JsonObject When);

/// <summary>An action with its inputs (strings may contain tokens such as <c>{title}</c>).</summary>
public sealed record ActionDefinition(string Type, JsonObject? Inputs = null);

/// <summary>
/// What a workflow does (a version of it): its trigger (<c>trigger</c>, or several in <c>triggers</c>: any of them starts
/// a run), an OData condition on the item checked when a trigger fires (optional), and its body: either <c>steps</c> (a
/// sequence with if/else) or a <c>flow</c> (nodes connected by outcome ports, ADR-0036). <c>variables</c> are the initial
/// values of the run's variables. <c>concurrency</c> says what happens when a run on an item starts while another run of
/// the workflow on that item is going (<see cref="RunConcurrency"/>).
/// </summary>
public sealed record WorkflowSpec(
    WorkflowTrigger? Trigger, string? Condition, IReadOnlyList<WorkflowStep>? Steps, FlowDefinition? Flow = null, JsonObject? Variables = null,
    string? Concurrency = null, IReadOnlyList<WorkflowTrigger>? Triggers = null, string? Scope = null, JsonObject? InputSchema = null,
    string? Provides = null)
{
    /// <summary>Most triggers of a workflow.</summary>
    public const int MaxTriggers = 10;

    /// <summary>The triggers: <c>triggers</c>, or the one <c>trigger</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorkflowTrigger> AllTriggers => Triggers ?? (Trigger is null ? [] : [Trigger]);

    /// <summary>The trigger types, for the workflow's <c>Trigger</c> column (joined with commas, each once).</summary>
    [JsonIgnore]
    public string TriggerTypes => string.Join(',', AllTriggers.Where(t => t is not null).Select(t => t.Type).Distinct(StringComparer.Ordinal));
}

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
/// A workflow's flow (ADR-0036): the <c>start</c> node and the nodes by id. A node runs an activity and continues with
/// the node its outcome port names in <c>next</c> (<c>done</c> when the outcome's own port is not connected); a node
/// without a next node ends the run.
/// </summary>
public sealed record FlowDefinition(string Start, IReadOnlyDictionary<string, FlowNode> Nodes);

/// <summary>
/// A node of a flow: an <c>activity</c> (an action such as <c>item.update</c>, or one of the flow activities
/// <c>approval</c>, <c>delay</c>, <c>if</c>, <c>setVariable</c>, <c>end</c>, <c>fail</c>), its <c>inputs</c>, the next
/// node per outcome port (<c>done</c>, <c>error</c>, <c>approved</c>, <c>rejected</c>, <c>true</c>, <c>false</c>)
/// and an optional <c>retry</c> policy for failures.
/// </summary>
public sealed record FlowNode(string Activity, JsonObject? Inputs = null, IReadOnlyDictionary<string, string>? Next = null, RetryPolicy? Retry = null);

/// <summary>How often a failing activity is tried again (<c>attempts</c>, at most 10) and how long to wait in between (<c>delayMinutes</c>, default 1).</summary>
public sealed record RetryPolicy(int Attempts, double? DelayMinutes = null);

/// <summary>Kinds of workflow steps (the <c>steps</c> form).</summary>
public static class StepTypes
{
    /// <summary>Runs an action (<c>action</c>, <c>inputs</c>).</summary>
    public const string Action = "action";

    /// <summary>Asks <c>assignees</c> to approve or reject; <c>dueInHours</c> and <c>escalateTo</c> escalate overdue requests.</summary>
    public const string Approval = "approval";

    /// <summary>Waits <c>hours</c>.</summary>
    public const string Delay = "delay";

    /// <summary>Runs <c>then</c> when the condition holds, else <c>else</c>: <c>step</c> + <c>is</c> (an approval outcome) or <c>filter</c> (OData on the item).</summary>
    public const string Condition = "condition";

    public static readonly string[] All = [Action, Approval, Delay, Condition];
}

/// <summary>Activities of flows that the engine runs itself (actions come from the catalog).</summary>
public static class FlowActivities
{
    /// <summary>Waits for a decision (inputs <c>assignees</c>, <c>title</c>, <c>dueInHours</c>, <c>escalateTo</c>); ports <c>approved</c>, <c>rejected</c>.</summary>
    public const string Approval = "approval";

    /// <summary>Waits <c>hours</c>.</summary>
    public const string Delay = "delay";

    /// <summary>
    /// Continues with <c>true</c> or <c>false</c>: <c>filter</c> (OData on the item), <c>step</c> + <c>is</c> (an approval
    /// outcome) or <c>left</c>, <c>op</c> (<see cref="Comparison.Operators"/>) and <c>right</c> (text with tokens).
    /// </summary>
    public const string If = "if";

    /// <summary>Sets the variable <c>name</c> to <c>value</c> (text with tokens, or any JSON value).</summary>
    public const string SetVariable = "setVariable";

    /// <summary>
    /// Runs the nodes on its <c>item</c> port once per element of <c>items</c> (an array, or a text that is exactly one
    /// token, e.g. <c>{step:read.json.lines}</c>) or of the items a <c>query</c> finds (<c>list</c>, <c>filter</c>), with
    /// the element in the variable <c>as</c> (default <c>item</c>); the body leads back to this node. Then <c>done</c>.
    /// </summary>
    public const string ForEach = "forEach";

    /// <summary>
    /// Runs JavaScript (<c>code</c>, ADR-0037) that reads the workspace's lists and plans writes to them (<c>items</c>);
    /// the plan is saved with the run and then applied. Output: <c>result</c> (what the script returned),
    /// <c>created</c>, <c>updated</c>, <c>deleted</c>.
    /// </summary>
    public const string Script = "script";

    /// <summary>
    /// Raises the workflow's event <c>event</c> (ADR-0038): other workflows start on <c>wf.{key}.{event}</c>, with the run's
    /// item and <c>data</c> (strings may be tokens). Then <c>done</c>.
    /// </summary>
    public const string Raise = "event.raise";

    /// <summary>Most elements a <see cref="ForEach"/> goes through.</summary>
    public const int MaxForEachItems = 500;

    /// <summary>Ends the run as completed.</summary>
    public const string End = "end";

    /// <summary>Ends the run as failed with <c>message</c>.</summary>
    public const string Fail = "fail";

    public static readonly string[] All = [Approval, Delay, If, SetVariable, ForEach, Script, Raise, End, Fail];

    /// <summary>The outcome ports each activity has; actions have <c>done</c> and <c>error</c>.</summary>
    public static IReadOnlySet<string> Ports(string activity) => activity switch
    {
        Approval => new HashSet<string>(["done", "approved", "rejected"], StringComparer.Ordinal),
        Delay => new HashSet<string>(["done"], StringComparer.Ordinal),
        If => new HashSet<string>(["true", "false", "error"], StringComparer.Ordinal),
        SetVariable => new HashSet<string>(["done"], StringComparer.Ordinal),
        ForEach => new HashSet<string>(["item", "done", "error"], StringComparer.Ordinal),
        Script => new HashSet<string>(["done", "error"], StringComparer.Ordinal),
        End or Fail => new HashSet<string>(StringComparer.Ordinal),
        _ => new HashSet<string>(["done", "error"], StringComparer.Ordinal),
    };
}

/// <summary>Comparisons of <c>if</c> nodes (<c>left</c>, <c>op</c>, <c>right</c>); numbers compare as numbers.</summary>
public static class Comparison
{
    public static readonly string[] Operators = ["eq", "ne", "gt", "ge", "lt", "le", "contains", "empty", "notEmpty"];

    public static bool Holds(string left, string op, string right)
    {
        var numbers = double.TryParse(left, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l)
            & double.TryParse(right, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r);
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

/// <summary>
/// A step of a workflow (EVT-07, EVT-08). Assignees and recipients are user names, <c>group:Name</c>,
/// <c>field:fieldName</c> (a person field of the item) or <c>creator</c>.
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
    IReadOnlyList<WorkflowStep>? Else = null,
    JsonObject? InputSchema = null);

public static class ApprovalOutcomes
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

internal static class DefinitionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>Parses definition JSON from a template; null with an error when it is not valid.</summary>
    public static (T? Value, string? Error) TryParse<T>(string json)
        where T : class
    {
        try
        {
            return (JsonSerializer.Deserialize<T>(json, Options), null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }
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

    [System.Text.RegularExpressions.GeneratedRegex(@"^wf\.[a-z0-9][a-z0-9._-]*\.[A-Za-z][A-Za-z0-9_-]{0,49}$")]
    private static partial System.Text.RegularExpressions.Regex EventTrigger();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,49}$")]
    private static partial System.Text.RegularExpressions.Regex EventName();
}

/// <summary>Checks definitions and compiles steps into flows.</summary>
internal static class Definitions
{
    public const int MaxSteps = 100;
    public const int MaxDepth = 5;
    public const int MaxNodeId = 200;

    /// <summary>Checks a definition against the known triggers and actions (the list and condition are checked by the caller).</summary>
    public static List<string> Validate(WorkflowSpec? spec, IReadOnlySet<string> triggers, ActionCatalog actions)
    {
        var errors = new List<string>();
        if (spec is not null && spec.Trigger is not null && spec.Triggers is not null)
        {
            errors.Add("Use either trigger or triggers, not both.");
            return errors;
        }

        if (spec is null || spec.AllTriggers.Count == 0)
        {
            errors.Add("A trigger is required (trigger, or a list of triggers).");
            return errors;
        }

        if (spec.AllTriggers.Count > WorkflowSpec.MaxTriggers)
        {
            errors.Add($"A workflow has at most {WorkflowSpec.MaxTriggers} triggers.");
        }

        if (spec.Scope is not (null or "workspace" or "list"))
        {
            errors.Add("scope must be workspace or list.");
        }

        if (spec.Scope == "workspace" && spec.AllTriggers.Any(t => t is not null && t.Type == WorkflowTriggers.Manual
            && (t.List is not null || t.ContentType is not null || t.Terms is { Count: > 0 })))
        {
            errors.Add("Workspace manual launches run without an item: list, contentType and terms do not apply.");
        }

        if (spec.Scope == "list" && spec.AllTriggers.Any(t => t is not null && t.List is null))
        {
            errors.Add("List workflows need a list on every trigger.");
        }

        if (spec.AllTriggers.Count(t => t is not null && t.Type == WorkflowTriggers.Manual) > 1)
        {
            errors.Add("Use a single manual trigger per workflow.");
        }

        errors.AddRange(WorkflowInputs.ValidateSchema(spec.InputSchema));
        var several = spec.Triggers is not null;
        foreach (var (trigger, index) in spec.AllTriggers.Select((t, i) => (t, i)))
        {
            var prefix = several ? $"triggers[{index}]: " : string.Empty;
            if (trigger is null)
            {
                errors.Add($"{prefix}a trigger is required.");
                continue;
            }

            if (!triggers.Contains(trigger.Type) && !WorkflowEvents.IsEventTrigger(trigger.Type))
            {
                errors.Add($"{prefix}Unknown trigger '{trigger.Type}'.");
            }

            if (trigger.ChangedFields is { Count: > 0 } && trigger.Type != WorkflowTriggers.ItemUpdated)
            {
                errors.Add($"{prefix}changedFields is only used with itemUpdated.");
            }

            if (!string.IsNullOrWhiteSpace(spec.Condition) && trigger.Type == WorkflowTriggers.ItemDeleted)
            {
                errors.Add($"{prefix}Conditions cannot be checked on deleted items.");
            }

            if (!string.IsNullOrWhiteSpace(spec.Condition) && trigger.List is null)
            {
                errors.Add($"{prefix}A condition needs the trigger's list (its fields).");
            }

            if (trigger.Concurrency is { } triggerConcurrency && !RunConcurrency.All.Contains(triggerConcurrency))
            {
                errors.Add($"{prefix}concurrency must be one of {string.Join(", ", RunConcurrency.All)}.");
            }

            errors.AddRange(TriggerConditions.Validate(trigger).Select(e => prefix + e));
            errors.AddRange(ValidateTrigger(trigger).Select(e => prefix + e));
        }

        if (spec.TriggerTypes.Length > 200)
        {
            errors.Add("The triggers' types are too long together (at most 200 characters).");
        }

        if (spec.Concurrency is { } concurrency && !RunConcurrency.All.Contains(concurrency))
        {
            errors.Add($"concurrency must be one of {string.Join(", ", RunConcurrency.All)}.");
        }

        if (spec.Flow is not null && spec.Steps is { Count: > 0 })
        {
            errors.Add("Use either steps or flow, not both.");
        }
        else if (spec.Flow is { } flow)
        {
            errors.AddRange(ValidateFlow(flow, actions));
        }
        else
        {
            errors.AddRange(ValidateSteps(spec.Steps, actions));
        }

        return errors;
    }

    /// <summary>Triggers without data of their own.</summary>
    private static readonly HashSet<string> ItemTriggers =
    [
        WorkflowTriggers.Manual, WorkflowTriggers.ItemAdded, WorkflowTriggers.ItemUpdated, WorkflowTriggers.ItemDeleted,
        WorkflowTriggers.ItemRestored, WorkflowTriggers.Schedule, WorkflowTriggers.Date, WorkflowTriggers.Webhook,
    ];

    /// <summary>Checks the settings of schedule and date triggers, and that other triggers do not use them.</summary>
    private static IEnumerable<string> ValidateTrigger(WorkflowTrigger trigger)
    {
        var isSchedule = trigger.Type == WorkflowTriggers.Schedule;
        var isDate = trigger.Type == WorkflowTriggers.Date;
        if (trigger.Type == WorkflowTriggers.Webhook && (trigger.List is not null || trigger.ContentType is not null || trigger.Terms is { Count: > 0 }))
        {
            yield return "webhook runs without an item: list, contentType and terms do not apply.";
        }

        if (isSchedule)
        {
            if (TriggerSchedules.ParseCron(trigger.Cron) is null)
            {
                yield return "schedule needs cron: 5 fields (minute hour day month weekday), e.g. 0 8 * * 1-5.";
            }

            if (trigger.List is not null || trigger.ContentType is not null || trigger.Terms is { Count: > 0 })
            {
                yield return "schedule runs without an item: list, contentType and terms do not apply.";
            }
        }
        else if (trigger.Cron is not null)
        {
            yield return "cron is only used with schedule.";
        }

        if (trigger.TimeZone is { } zone && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            yield return $"Unknown time zone '{zone}'.";
        }
        else if (trigger.TimeZone is not null && !isSchedule)
        {
            yield return "timeZone is only used with schedule.";
        }

        if (isDate)
        {
            if (trigger.List is null || string.IsNullOrWhiteSpace(trigger.Field))
            {
                yield return "date needs list and field (a date field of the list).";
            }

            if (trigger.OffsetHours is { } offset && (double.IsNaN(offset) || Math.Abs(offset) > 24 * 366))
            {
                yield return "offsetHours must be within a year.";
            }
        }
        else if (trigger.Field is not null || trigger.OffsetHours is not null)
        {
            yield return "field and offsetHours are only used with date.";
        }

        if (trigger.Terms is { Count: > 0 } terms && terms.Any(string.IsNullOrWhiteSpace))
        {
            yield return "terms are term paths such as Group/Set/Term.";
        }

        if (trigger.Inputs is not null && trigger.Type != WorkflowTriggers.Manual)
        {
            yield return "inputs are only used with manual.";
        }

        if (trigger.Data is not null && ItemTriggers.Contains(trigger.Type))
        {
            yield return "data is only used with module and extension triggers (they have data).";
        }

        foreach (var error in WorkflowInputs.ValidateSchema(trigger.Inputs))
        {
            yield return error;
        }
    }

    /// <summary>The flow a run executes: the definition's flow, or its steps compiled into one.</summary>
    public static FlowDefinition FlowOf(WorkflowSpec spec) => spec.Flow ?? Compile(spec.Steps ?? []);

    public static List<string> ValidateSteps(IReadOnlyList<WorkflowStep>? steps, ActionCatalog actions)
    {
        var errors = new List<string>();
        if (steps is not { Count: > 0 })
        {
            errors.Add("At least one step is required.");
            return errors;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        void Check(IReadOnlyList<WorkflowStep> steps, string path, int depth)
        {
            if (depth > MaxDepth)
            {
                errors.Add($"{path}: conditions can be nested at most {MaxDepth} levels.");
                return;
            }

            foreach (var (step, index) in steps.Select((s, i) => (s, i)))
            {
                var at = $"{path}[{index}]";
                if (++count > MaxSteps)
                {
                    errors.Add($"A workflow has at most {MaxSteps} steps.");
                    return;
                }

                if (step.Name is { } name && (!names.Add(name) || name.Length > MaxNodeId))
                {
                    errors.Add(name.Length > MaxNodeId ? $"{at}: step names have at most {MaxNodeId} characters." : $"{at}: the step name '{name}' is used twice.");
                }

                switch (step.Type)
                {
                    case StepTypes.Action:
                        errors.AddRange(actions.Validate(new ActionDefinition(step.Action ?? string.Empty, step.Inputs)).Select(e => $"{at}: {e}"));
                        break;
                    case StepTypes.Approval:
                        if (step.Name is null)
                        {
                            errors.Add($"{at}: approval steps need a name (conditions refer to their outcome).");
                        }

                        errors.AddRange(WorkflowInputs.ValidateSchema(step.InputSchema).Select(e => $"{at}: {e}"));
                        errors.AddRange(ValidateApproval(step.Assignees, step.DueInHours).Select(e => $"{at}: {e}"));
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
                        else if (step.Step is { } target && !names.Contains(target))
                        {
                            errors.Add($"{at}: step '{target}' is not an earlier approval step.");
                        }
                        else if (step.Step is not null && step.Is is not (ApprovalOutcomes.Approved or ApprovalOutcomes.Rejected))
                        {
                            errors.Add($"{at}: is must be approved or rejected.");
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

        Check(steps, "steps", 0);

        // Steps without a name get ids from the steps around them (Name.then.1): those have a length limit too.
        if (errors.Count == 0 && Compile(steps).Nodes.Keys.FirstOrDefault(k => k.Length > MaxNodeId) is { } tooLong)
        {
            errors.Add($"The step '{tooLong[..50]}…' is nested under names that are too long: step ids have at most {MaxNodeId} characters.");
        }

        return errors;
    }

    /// <summary>
    /// Checks a flow: the start and every next node exist, ports fit their activity, inputs are valid, every node can be
    /// reached, and <c>if</c> nodes on approval outcomes name an approval node.
    /// </summary>
    public static List<string> ValidateFlow(FlowDefinition flow, ActionCatalog actions)
    {
        var errors = new List<string>();
        if (flow.Nodes is not { Count: > 0 })
        {
            errors.Add("A flow needs at least one node.");
            return errors;
        }

        if (flow.Nodes.Count > MaxSteps)
        {
            errors.Add($"A flow has at most {MaxSteps} nodes.");
            return errors;
        }

        if (!flow.Nodes.ContainsKey(flow.Start ?? string.Empty))
        {
            errors.Add($"The start node '{flow.Start}' does not exist.");
        }

        foreach (var (id, node) in flow.Nodes)
        {
            var at = $"flow.nodes['{id}']";
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxNodeId)
            {
                errors.Add($"{at}: node ids have 1 to {MaxNodeId} characters.");
            }

            if (node is null || string.IsNullOrWhiteSpace(node.Activity))
            {
                errors.Add($"{at}: an activity is required.");
                continue;
            }

            var inputs = node.Inputs ?? [];
            switch (node.Activity)
            {
                case FlowActivities.Approval:
                    if (inputs["review"] is { } review && (review is not JsonObject reference
                        || ActivityInputs.Text(reference, "type") is null || ActivityInputs.Text(reference, "key") is null))
                    {
                        errors.Add($"{at}: review needs a type and key.");
                    }

                    if (inputs["inputSchema"] is not (null or JsonObject))
                    {
                        errors.Add($"{at}: inputSchema must be a JSON Schema object.");
                    }
                    errors.AddRange(WorkflowInputs.ValidateSchema(inputs["inputSchema"] as JsonObject).Select(e => $"{at}: {e}"));
                    errors.AddRange(ValidateApproval(ActivityInputs.Texts(inputs, "assignees"), ActivityInputs.Number(inputs, "dueInHours")).Select(e => $"{at}: {e}"));
                    break;
                case FlowActivities.Delay:
                    if (ActivityInputs.Number(inputs, "hours") is not > 0)
                    {
                        errors.Add($"{at}: hours must be positive.");
                    }

                    break;
                case FlowActivities.If:
                    var kinds = new[] { ActivityInputs.Text(inputs, "filter") is not null, ActivityInputs.Text(inputs, "step") is not null, ActivityInputs.Text(inputs, "op") is not null };
                    if (kinds.Count(k => k) != 1)
                    {
                        errors.Add($"{at}: use one of filter, step (with is) or left, op and right.");
                    }
                    else if (ActivityInputs.Text(inputs, "step") is { } step)
                    {
                        if (flow.Nodes.GetValueOrDefault(step)?.Activity != FlowActivities.Approval)
                        {
                            errors.Add($"{at}: '{step}' is not an approval node.");
                        }

                        if (ActivityInputs.Text(inputs, "is") is not (ApprovalOutcomes.Approved or ApprovalOutcomes.Rejected))
                        {
                            errors.Add($"{at}: is must be approved or rejected.");
                        }
                    }
                    else if (ActivityInputs.Text(inputs, "op") is { } op && !Comparison.Operators.Contains(op))
                    {
                        errors.Add($"{at}: op must be one of {string.Join(", ", Comparison.Operators)}.");
                    }

                    break;
                case FlowActivities.Raise:
                    if (WorkflowEvents.Check(ActivityInputs.Text(inputs, "event")) is { } eventError)
                    {
                        errors.Add($"{at}: {eventError}");
                    }

                    if (inputs["data"] is not (null or JsonObject))
                    {
                        errors.Add($"{at}: data must be an object.");
                    }

                    break;
                case FlowActivities.SetVariable:
                    if (ActivityInputs.Text(inputs, "name") is null)
                    {
                        errors.Add($"{at}: name is required.");
                    }

                    break;
                case FlowActivities.ForEach:
                    var query = inputs["query"] as JsonObject;
                    if ((inputs["items"] is null) == (query is null))
                    {
                        errors.Add($"{at}: use either items (an array, or one token such as {{step:read.json.lines}}) or query (list, filter).");
                    }
                    else if (inputs["items"] is { } list && list is not JsonArray && !(list is JsonValue value && value.GetValueKind() == JsonValueKind.String))
                    {
                        errors.Add($"{at}: items must be an array or a token.");
                    }
                    else if (query is not null && ActivityInputs.Text(query, "list") is null)
                    {
                        errors.Add($"{at}: query needs list.");
                    }

                    if (ActivityInputs.Text(inputs, "as") is { } variable && !System.Text.RegularExpressions.Regex.IsMatch(variable, "^[A-Za-z][A-Za-z0-9_]*$"))
                    {
                        errors.Add($"{at}: as must be a name (letters, digits and _).");
                    }

                    if (node.Next?.ContainsKey("item") != true)
                    {
                        errors.Add($"{at}: forEach needs an item port (the nodes to run for each element).");
                    }

                    break;
                case FlowActivities.Script:
                    if (ScriptRunner.Check(ScriptRunner.Code(inputs)) is { } problem)
                    {
                        errors.Add($"{at}: {problem}");
                    }

                    break;
                case FlowActivities.End or FlowActivities.Fail:
                    break;
                default:
                    errors.AddRange(actions.Validate(new ActionDefinition(node.Activity, node.Inputs)).Select(e => $"{at}: {e}"));
                    break;
            }

            var ports = FlowActivities.All.Contains(node.Activity)
                ? FlowActivities.Ports(node.Activity)
                : new HashSet<string>([.. FlowActivities.Ports(node.Activity), .. actions.Find(node.Activity)?.Outcomes ?? []], StringComparer.Ordinal);
            foreach (var (port, target) in node.Next ?? new Dictionary<string, string>())
            {
                if (!ports.Contains(port))
                {
                    errors.Add(ports.Count == 0
                        ? $"{at}: {node.Activity} nodes have no next node."
                        : $"{at}: unknown port '{port}' (use {string.Join(", ", ports.Order(StringComparer.Ordinal))}).");
                }
                else if (!flow.Nodes.ContainsKey(target ?? string.Empty))
                {
                    errors.Add($"{at}: the next node '{target}' ({port}) does not exist.");
                }
            }

            if (node.Retry is { } retry && (retry.Attempts is < 1 or > 10 || retry.DelayMinutes is < 0 or > 1440))
            {
                errors.Add($"{at}: retry needs 1 to 10 attempts and a delay of 0 to 1440 minutes.");
            }
        }

        if (errors.Count == 0)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([flow.Start!]);
            while (pending.TryPop(out var id))
            {
                if (reached.Add(id))
                {
                    foreach (var next in flow.Nodes[id].Next?.Values ?? [])
                    {
                        pending.Push(next);
                    }
                }
            }

            errors.AddRange(flow.Nodes.Keys.Where(id => !reached.Contains(id)).Select(id => $"flow.nodes['{id}']: the node cannot be reached from the start."));

            // A loop inside a loop needs its own variable, or it would overwrite the outer loop's element.
            static string Variable(FlowNode node) => ActivityInputs.Text(node.Inputs ?? [], "as") ?? "item";
            foreach (var (id, node) in flow.Nodes.Where(n => n.Value.Activity == FlowActivities.ForEach))
            {
                errors.AddRange(LoopBody(flow, id)
                    .Where(b => flow.Nodes[b].Activity == FlowActivities.ForEach && Variable(flow.Nodes[b]) == Variable(node))
                    .Select(inner => $"flow.nodes['{inner}']: a loop inside the loop '{id}' needs its own variable (as), not '{Variable(node)}'."));
            }
        }

        return errors;
    }

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

    private static IEnumerable<string> ValidateApproval(IReadOnlyList<string>? assignees, double? dueInHours)
    {
        if (assignees is not { Count: > 0 })
        {
            yield return "assignees are required.";
        }

        if (dueInHours is <= 0)
        {
            yield return "dueInHours must be positive.";
        }
    }

    /// <summary>
    /// Compiles steps into a flow: each step becomes a node named like the step (or <c>step 1</c>, <c>Name.then.1</c>, …),
    /// connected by <c>done</c>; a condition becomes an <c>if</c> node whose <c>true</c> and <c>false</c> ports lead into
    /// its branches, which continue with the step after the condition.
    /// </summary>
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
                var done = next is null ? null : new Dictionary<string, string> { ["done"] = next };
                nodes[id] = step.Type switch
                {
                    StepTypes.Action => new FlowNode(step.Action ?? string.Empty, step.Inputs, done),
                    StepTypes.Approval => new FlowNode(FlowActivities.Approval, Object(
                        ("assignees", Strings(step.Assignees)), ("title", step.Title), ("dueInHours", step.DueInHours), ("escalateTo", Strings(step.EscalateTo)), ("inputSchema", step.InputSchema?.DeepClone())), done),
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
            var ports = new Dictionary<string, string>();
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
        return new FlowDefinition(start ?? string.Empty, nodes);
    }

    private static IEnumerable<string> Names(IReadOnlyList<WorkflowStep> steps) =>
        steps.SelectMany(s => (s.Name is { } name ? [name] : System.Array.Empty<string>()).Concat(Names(s.Then ?? [])).Concat(Names(s.Else ?? [])));

    private static JsonArray? Strings(IReadOnlyList<string>? values) => values is null ? null : new JsonArray([.. values.Select(v => JsonValue.Create(v))]);

    private static JsonObject Object(params (string Name, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            switch (value)
            {
                case null:
                    break;
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
