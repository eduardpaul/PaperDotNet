using System.Text.Json.Nodes;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.UnitTests;

public sealed class WorkflowDefinitionTests
{
    private sealed class FakeAction(string key) : IWorkflowActivity
    {
        public string Key => key;

        public string Description => key;

        public IEnumerable<string> Validate(JsonObject inputs) => inputs.ContainsKey("bad") ? ["bad input."] : [];

        public Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowActivityResult.Ok());
    }

    private static readonly ActionCatalog Actions = new([new FakeAction("item.update")]);

    private static WorkflowStep Act(string name) => new(StepTypes.Action, name, Action: "item.update");

    [Fact]
    public void Conditions_compile_to_branches_and_jumps()
    {
        var program = Definitions.Compile(
        [
            new(StepTypes.Approval, "Manager", Assignees: ["alice"]),
            new(StepTypes.Condition, "Approved?", Step: "Manager", Is: "approved", Then: [Act("a"), Act("b")], Else: [Act("c")]),
            Act("d"),
        ]);

        Assert.Equal(
            ["Approval Manager", "Branch Approved? -> 5", "Action a", "Action b", "Jump Approved? -> 6", "Action c", "Action d"],
            program.Select(i => $"{i.Op} {i.StepName}" + (i.Target >= 0 ? $" -> {i.Target}" : string.Empty)));
    }

    [Fact]
    public void Steps_are_checked_before_they_are_saved()
    {
        var errors = Definitions.ValidateSteps(
        [
            new(StepTypes.Condition, Step: "Later", Is: "approved"),
            new(StepTypes.Approval, "Later"),
            new(StepTypes.Action, Action: "nope"),
            new(StepTypes.Action, Action: "item.update", Inputs: new JsonObject { ["bad"] = true }),
            new(StepTypes.Delay, Hours: 0),
            new("loop"),
        ], Actions);

        Assert.Equal(
        [
            "steps[0]: step 'Later' is not an earlier approval step.",
            "steps[1]: assignees are required.",
            "steps[2]: Unknown action 'nope'.",
            "steps[3]: bad input.",
            "steps[4]: hours must be positive.",
            "steps[5]: unknown step type 'loop' (use action, approval, delay, condition).",
        ], errors);
    }

    [Fact]
    public void Workflows_need_a_known_trigger_and_steps()
    {
        var triggers = new HashSet<string>(["itemAdded", "itemDeleted", "manual"]);
        Assert.Equal(["Unknown trigger 'x'.", "At least one step is required."],
            Definitions.Validate(new WorkflowSpec(new WorkflowTrigger("x"), null, []), triggers, Actions));
        Assert.Equal(["Conditions cannot be checked on deleted items."],
            Definitions.Validate(new WorkflowSpec(new WorkflowTrigger("itemDeleted", "Bills"), "fields/a eq 1", [Act("a")]), triggers, Actions));
        Assert.Equal(["changedFields is only used with itemUpdated."],
            Definitions.Validate(new WorkflowSpec(new WorkflowTrigger("manual", ChangedFields: ["a"]), null, [Act("a")]), triggers, Actions));
    }

    [Theory]
    [InlineData("ACME/Corp", "ACME-Corp")]
    [InlineData("  a:b*c?  ", "a-b-c-")]
    [InlineData("...", "_")]
    [InlineData("", "_")]
    public void Folder_names_from_path_templates_are_cleaned(string value, string expected) =>
        Assert.Equal(expected, ItemFileAction.Clean(value));
}
