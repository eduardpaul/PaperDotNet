using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PaperDotNet.Automation.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>
/// When a rule runs: <c>type</c> is <c>itemAdded</c>, <c>itemUpdated</c>, <c>itemDeleted</c> or an extension trigger;
/// <c>list</c> and <c>contentType</c> narrow it by name; <c>changedFields</c> (updates) needs one of them to change.
/// </summary>
public sealed record RuleTrigger(string Type, string? List = null, string? ContentType = null, IReadOnlyList<string>? ChangedFields = null);

/// <summary>An action with its inputs (strings may contain tokens such as <c>{title}</c>).</summary>
public sealed record ActionDefinition(string Type, JsonObject? Inputs = null);

/// <summary>A rule's definition: trigger, OData condition on the item (optional) and actions, run in order.</summary>
public sealed record RuleDefinition(RuleTrigger Trigger, string? Condition, IReadOnlyList<ActionDefinition> Actions);

/// <summary>Kinds of workflow steps.</summary>
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
/// A workflow step (EVT-08). Assignees and recipients are user names, <c>group:Name</c>,
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
    IReadOnlyList<WorkflowStep>? Else = null);

public sealed record WorkflowSteps(IReadOnlyList<WorkflowStep> Steps);

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

/// <summary>An instruction of a compiled workflow: steps become a flat program with forward jumps.</summary>
internal enum OpCode
{
    Action,
    Approval,
    Delay,

    /// <summary>Evaluates the condition; jumps to <see cref="Instruction.Target"/> when it does not hold.</summary>
    Branch,
    Jump,
}

internal sealed record Instruction(OpCode Op, WorkflowStep Step, string StepName, int Target = -1);

/// <summary>Checks definitions and compiles workflow steps.</summary>
internal static class Definitions
{
    public const int MaxActions = 20;
    public const int MaxSteps = 100;
    public const int MaxDepth = 5;

    public static List<string> ValidateRule(RuleDefinition? rule, IReadOnlySet<string> triggers, ActionCatalog actions)
    {
        var errors = new List<string>();
        if (rule?.Trigger is null)
        {
            errors.Add("A trigger is required.");
            return errors;
        }

        if (!triggers.Contains(rule.Trigger.Type))
        {
            errors.Add($"Unknown trigger '{rule.Trigger.Type}'.");
        }

        if (rule.Trigger.ChangedFields is { Count: > 0 } && rule.Trigger.Type != AutomationTriggers.ItemUpdated)
        {
            errors.Add("changedFields is only used with itemUpdated.");
        }

        if (!string.IsNullOrWhiteSpace(rule.Condition) && rule.Trigger.Type == AutomationTriggers.ItemDeleted)
        {
            errors.Add("Conditions cannot be checked on deleted items.");
        }

        if (!string.IsNullOrWhiteSpace(rule.Condition) && rule.Trigger.List is null)
        {
            errors.Add("A condition needs the trigger's list (its fields).");
        }

        if (rule.Actions is not { Count: > 0 and <= MaxActions })
        {
            errors.Add($"Between 1 and {MaxActions} actions are required.");
        }

        foreach (var (action, index) in (rule.Actions ?? []).Select((a, i) => (a, i)))
        {
            errors.AddRange(actions.Validate(action).Select(e => $"actions[{index}]: {e}"));
        }

        return errors;
    }

    public static List<string> ValidateWorkflow(WorkflowSteps? workflow, ActionCatalog actions)
    {
        var errors = new List<string>();
        if (workflow?.Steps is not { Count: > 0 })
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

                if (step.Name is { } name && !names.Add(name))
                {
                    errors.Add($"{at}: the step name '{name}' is used twice.");
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

                        if (step.Assignees is not { Count: > 0 })
                        {
                            errors.Add($"{at}: assignees are required.");
                        }

                        if (step.DueInHours is <= 0)
                        {
                            errors.Add($"{at}: dueInHours must be positive.");
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

        Check(workflow.Steps, "steps", 0);
        return errors;
    }

    /// <summary>Flattens the steps: a condition becomes Branch(else) + then + Jump(end) + else.</summary>
    public static List<Instruction> Compile(WorkflowSteps workflow)
    {
        var program = new List<Instruction>();
        void Emit(IReadOnlyList<WorkflowStep> steps, string path)
        {
            foreach (var (step, index) in steps.Select((s, i) => (s, i)))
            {
                var name = step.Name ?? $"{path}{index + 1}";
                switch (step.Type)
                {
                    case StepTypes.Action:
                        program.Add(new Instruction(OpCode.Action, step, name));
                        break;
                    case StepTypes.Approval:
                        program.Add(new Instruction(OpCode.Approval, step, name));
                        break;
                    case StepTypes.Delay:
                        program.Add(new Instruction(OpCode.Delay, step, name));
                        break;
                    case StepTypes.Condition:
                        var branch = program.Count;
                        program.Add(new Instruction(OpCode.Branch, step, name));
                        Emit(step.Then ?? [], $"{name}.then.");
                        var jump = program.Count;
                        program.Add(new Instruction(OpCode.Jump, step, name));
                        program[branch] = program[branch] with { Target = program.Count };
                        Emit(step.Else ?? [], $"{name}.else.");
                        program[jump] = program[jump] with { Target = program.Count };
                        break;
                }
            }
        }

        Emit(workflow.Steps, "step ");
        return program;
    }
}
