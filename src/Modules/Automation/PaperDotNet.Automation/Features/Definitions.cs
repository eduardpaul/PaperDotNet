using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PaperDotNet.Automation.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>
/// When an automation runs: <c>type</c> is <c>manual</c> (started on an item by a person), <c>itemAdded</c>,
/// <c>itemUpdated</c>, <c>itemDeleted</c>, <c>itemRestored</c> or an extension trigger; <c>list</c> and
/// <c>contentType</c> narrow it by name; <c>changedFields</c> (updates) needs one of them to change.
/// </summary>
public sealed record AutomationTrigger(string Type, string? List = null, string? ContentType = null, IReadOnlyList<string>? ChangedFields = null);

/// <summary>An action with its inputs (strings may contain tokens such as <c>{title}</c>).</summary>
public sealed record ActionDefinition(string Type, JsonObject? Inputs = null);

/// <summary>
/// What an automation does (a version of it): its trigger, an OData condition on the item checked when the trigger
/// fires (optional), and the steps a run executes.
/// </summary>
public sealed record AutomationSpec(AutomationTrigger Trigger, string? Condition, IReadOnlyList<AutomationStep> Steps);

/// <summary>Kinds of automation steps.</summary>
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

/// <summary>
/// A step of an automation (EVT-07, EVT-08). Assignees and recipients are user names, <c>group:Name</c>,
/// <c>field:fieldName</c> (a person field of the item) or <c>creator</c>.
/// </summary>
public sealed record AutomationStep(
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
    IReadOnlyList<AutomationStep>? Then = null,
    IReadOnlyList<AutomationStep>? Else = null);

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

/// <summary>An instruction of a compiled automation: steps become a flat program with forward jumps.</summary>
internal enum OpCode
{
    Action,
    Approval,
    Delay,

    /// <summary>Evaluates the condition; jumps to <see cref="Instruction.Target"/> when it does not hold.</summary>
    Branch,
    Jump,
}

internal sealed record Instruction(OpCode Op, StepNode Step, string StepName, int Target = -1);

/// <summary>A step after <see cref="AutomationStep"/> is read: only the fields of its type exist.</summary>
internal abstract record StepNode(string? Name);

internal sealed record ActionNode(string? Name, string? Action, JsonObject? Inputs) : StepNode(Name);

internal sealed record ApprovalNode(string? Name, IReadOnlyList<string>? Assignees, string? Title, double? DueInHours, IReadOnlyList<string>? EscalateTo) : StepNode(Name);

internal sealed record DelayNode(string? Name, double? Hours) : StepNode(Name);

internal sealed record ConditionNode(
    string? Name, string? ApprovalName, string? Outcome, string? Filter, IReadOnlyList<StepNode> Then, IReadOnlyList<StepNode> Else) : StepNode(Name);

internal sealed record UnknownNode(string? Name, string Type) : StepNode(Name);

internal static class StepNodes
{
    public static StepNode Bind(AutomationStep step) => step.Type switch
    {
        StepTypes.Action => new ActionNode(step.Name, step.Action, step.Inputs),
        StepTypes.Approval => new ApprovalNode(step.Name, step.Assignees, step.Title, step.DueInHours, step.EscalateTo),
        StepTypes.Delay => new DelayNode(step.Name, step.Hours),
        StepTypes.Condition => new ConditionNode(step.Name, step.Step, step.Is, step.Filter, BindAll(step.Then), BindAll(step.Else)),
        _ => new UnknownNode(step.Name, step.Type),
    };

    public static IReadOnlyList<StepNode> BindAll(IReadOnlyList<AutomationStep>? steps) =>
        steps is null ? [] : [.. steps.Select(Bind)];
}

/// <summary>Checks definitions and compiles their steps.</summary>
internal static class Definitions
{
    public const int MaxSteps = 100;
    public const int MaxDepth = 5;

    /// <summary>Checks a definition against the known triggers and actions (the list and condition are checked by the caller).</summary>
    public static List<string> Validate(AutomationSpec? spec, IReadOnlySet<string> triggers, ActionCatalog actions)
    {
        var errors = new List<string>();
        if (spec?.Trigger is null)
        {
            errors.Add("A trigger is required.");
            return errors;
        }

        var trigger = spec.Trigger;
        if (!triggers.Contains(trigger.Type))
        {
            errors.Add($"Unknown trigger '{trigger.Type}'.");
        }

        if (trigger.ChangedFields is { Count: > 0 } && trigger.Type != AutomationTriggers.ItemUpdated)
        {
            errors.Add("changedFields is only used with itemUpdated.");
        }

        if (!string.IsNullOrWhiteSpace(spec.Condition) && trigger.Type == AutomationTriggers.ItemDeleted)
        {
            errors.Add("Conditions cannot be checked on deleted items.");
        }

        if (!string.IsNullOrWhiteSpace(spec.Condition) && trigger.List is null)
        {
            errors.Add("A condition needs the trigger's list (its fields).");
        }

        errors.AddRange(ValidateSteps(spec.Steps, actions));
        return errors;
    }

    public static List<string> ValidateSteps(IReadOnlyList<AutomationStep>? steps, ActionCatalog actions) =>
        ValidateNodes(StepNodes.BindAll(steps), actions);

    private static List<string> ValidateNodes(IReadOnlyList<StepNode> steps, ActionCatalog actions)
    {
        var errors = new List<string>();
        if (steps is not { Count: > 0 })
        {
            errors.Add("At least one step is required.");
            return errors;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        void Check(IReadOnlyList<StepNode> steps, string path, int depth)
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
                    errors.Add($"An automation has at most {MaxSteps} steps.");
                    return;
                }

                if (step.Name is { } name && !names.Add(name))
                {
                    errors.Add($"{at}: the step name '{name}' is used twice.");
                }

                switch (step)
                {
                    case ActionNode action:
                        errors.AddRange(actions.Validate(new ActionDefinition(action.Action ?? string.Empty, action.Inputs)).Select(e => $"{at}: {e}"));
                        break;
                    case ApprovalNode approval:
                        if (approval.Name is null)
                        {
                            errors.Add($"{at}: approval steps need a name (conditions refer to their outcome).");
                        }

                        if (approval.Assignees is not { Count: > 0 })
                        {
                            errors.Add($"{at}: assignees are required.");
                        }

                        if (approval.DueInHours is <= 0)
                        {
                            errors.Add($"{at}: dueInHours must be positive.");
                        }

                        break;
                    case DelayNode delay:
                        if (delay.Hours is not > 0)
                        {
                            errors.Add($"{at}: hours must be positive.");
                        }

                        break;
                    case ConditionNode condition:
                        if ((condition.ApprovalName is null) == (condition.Filter is null))
                        {
                            errors.Add($"{at}: use either step (with is) or filter.");
                        }
                        else if (condition.ApprovalName is { } target && !names.Contains(target))
                        {
                            errors.Add($"{at}: step '{target}' is not an earlier approval step.");
                        }
                        else if (condition.ApprovalName is not null && condition.Outcome is not (ApprovalOutcomes.Approved or ApprovalOutcomes.Rejected))
                        {
                            errors.Add($"{at}: is must be approved or rejected.");
                        }

                        Check(condition.Then, $"{at}.then", depth + 1);
                        Check(condition.Else, $"{at}.else", depth + 1);
                        break;
                    case UnknownNode unknown:
                        errors.Add($"{at}: unknown step type '{unknown.Type}' (use {string.Join(", ", StepTypes.All)}).");
                        break;
                }
            }
        }

        Check(steps, "steps", 0);
        return errors;
    }

    /// <summary>Flattens the steps: a condition becomes Branch(else) + then + Jump(end) + else.</summary>
    public static List<Instruction> Compile(IReadOnlyList<AutomationStep> steps) => CompileNodes(StepNodes.BindAll(steps));

    private static List<Instruction> CompileNodes(IReadOnlyList<StepNode> steps)
    {
        var program = new List<Instruction>();
        void Emit(IReadOnlyList<StepNode> steps, string path)
        {
            foreach (var (step, index) in steps.Select((s, i) => (s, i)))
            {
                var name = step.Name ?? $"{path}{index + 1}";
                switch (step)
                {
                    case ActionNode:
                        program.Add(new Instruction(OpCode.Action, step, name));
                        break;
                    case ApprovalNode:
                        program.Add(new Instruction(OpCode.Approval, step, name));
                        break;
                    case DelayNode:
                        program.Add(new Instruction(OpCode.Delay, step, name));
                        break;
                    case ConditionNode condition:
                        var branch = program.Count;
                        program.Add(new Instruction(OpCode.Branch, step, name));
                        Emit(condition.Then, $"{name}.then.");
                        var jump = program.Count;
                        program.Add(new Instruction(OpCode.Jump, step, name));
                        program[branch] = program[branch] with { Target = program.Count };
                        Emit(condition.Else, $"{name}.else.");
                        program[jump] = program[jump] with { Target = program.Count };
                        break;
                }
            }
        }

        Emit(steps, "step ");
        return program;
    }
}
