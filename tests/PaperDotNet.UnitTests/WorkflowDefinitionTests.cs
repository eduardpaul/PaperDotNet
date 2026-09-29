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

    private static string Render(FlowDefinition flow) =>
        $"start {flow.Start}; " + string.Join("; ", flow.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n =>
            $"{n.Key}: {n.Value.Activity}" + (n.Value.Next is { Count: > 0 } next
                ? " -> " + string.Join(", ", next.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}"))
                : string.Empty)));

    [Fact]
    public void Steps_compile_to_a_flow_whose_branches_meet_again()
    {
        var flow = Definitions.Compile(
        [
            new(StepTypes.Approval, "Manager", Assignees: ["alice"]),
            new(StepTypes.Condition, "Approved?", Step: "Manager", Is: "approved", Then: [Act("a"), Act("b")], Else: [Act("c")]),
            Act("d"),
            new(StepTypes.Delay, Hours: 2),
        ]);

        Assert.Equal(
            "start Manager; Approved?: if -> false:c, true:a; Manager: approval -> done:Approved?; a: item.update -> done:b; "
            + "b: item.update -> done:d; c: item.update -> done:d; d: item.update -> done:step 4; step 4: delay",
            Render(flow));
        Assert.Equal("Manager", flow.Nodes["Approved?"].Inputs!["step"]!.GetValue<string>());
        Assert.Equal(2, flow.Nodes["step 4"].Inputs!["hours"]!.GetValue<double>());
        Assert.Empty(Definitions.ValidateFlow(flow, Actions));
    }

    [Fact]
    public void Flows_are_checked_before_they_are_saved()
    {
        var flow = new FlowDefinition("start", new Dictionary<string, FlowNode>
        {
            ["start"] = new("item.update", new JsonObject { ["bad"] = true }, new Dictionary<string, string> { ["done"] = "check", ["nope"] = "end", ["error"] = "missing" }),
            ["check"] = new(FlowActivities.If, new JsonObject { ["step"] = "start", ["is"] = "approved" }, new Dictionary<string, string> { ["true"] = "end" }),
            ["end"] = new(FlowActivities.End, Next: new Dictionary<string, string> { ["done"] = "start" }),
            ["lonely"] = new(FlowActivities.Delay, new JsonObject { ["hours"] = 1 }, Retry: new RetryPolicy(20)),
            ["compare"] = new(FlowActivities.If, new JsonObject { ["left"] = "{var:a}", ["op"] = "like" }),
        });

        Assert.Equal(
        [
            "flow.nodes['start']: bad input.",
            "flow.nodes['start']: unknown port 'nope' (use done, error).",
            "flow.nodes['start']: the next node 'missing' (error) does not exist.",
            "flow.nodes['check']: 'start' is not an approval node.",
            "flow.nodes['end']: end nodes have no next node.",
            "flow.nodes['lonely']: retry needs 1 to 10 attempts and a delay of 0 to 1440 minutes.",
            "flow.nodes['compare']: op must be one of eq, ne, gt, ge, lt, le, contains, empty, notEmpty.",
        ], Definitions.ValidateFlow(flow, Actions));

        var unreachable = new FlowDefinition("a", new Dictionary<string, FlowNode>
        {
            ["a"] = new(FlowActivities.SetVariable, new JsonObject { ["name"] = "x", ["value"] = "{title}" }),
            ["b"] = new(FlowActivities.End),
        });
        Assert.Equal(["flow.nodes['b']: the node cannot be reached from the start."], Definitions.ValidateFlow(unreachable, Actions));
        Assert.Equal(["The start node 'x' does not exist."], Definitions.ValidateFlow(unreachable with { Start = "x", Nodes = new Dictionary<string, FlowNode> { ["a"] = new(FlowActivities.End) } }, Actions)[..1]);
    }

    [Theory]
    [InlineData("10", "gt", "9", true)]
    [InlineData("10", "gt", "9a", false)]
    [InlineData("2.50", "eq", "2.5", true)]
    [InlineData("Invoice", "contains", "voice", true)]
    [InlineData(" ", "empty", "", true)]
    [InlineData("x", "notEmpty", "", true)]
    [InlineData("b", "lt", "a", false)]
    public void Comparisons_treat_numbers_as_numbers(string left, string op, string right, bool holds) =>
        Assert.Equal(holds, Comparison.Holds(left, op, right));

    [Fact]
    public void Step_tokens_find_outputs_of_nodes_with_dots_in_their_names()
    {
        var outputs = new JsonObject
        {
            ["extract"] = new JsonObject { ["fields"] = new JsonObject { ["total"] = 12.5 }, ["items"] = new JsonArray("a", "b") },
            ["Approved?.then.1"] = new JsonObject { ["taskId"] = "t1" },
        };
        var scope = new TokenScope(null, null, outputs, null, null);
        Assert.Equal(12.5, scope.Step("extract.fields.total")!.GetValue<double>());
        Assert.Equal("b", scope.Step("extract.items.1")!.GetValue<string>());
        Assert.Equal("t1", scope.Step("Approved?.then.1.taskId")!.GetValue<string>());
        Assert.Null(scope.Step("extract.missing"));
        Assert.Null(scope.Step("nothing"));
    }

    [Fact]
    public void Timed_triggers_and_inputs_are_checked()
    {
        var triggers = new HashSet<string>(["schedule", "date", "manual", "itemAdded"]);
        List<string> Check(WorkflowTrigger trigger) => Definitions.Validate(new WorkflowSpec(trigger, null, [Act("a")]), triggers, Actions);

        Assert.Empty(Check(new WorkflowTrigger("schedule", Cron: "0 8 * * 1-5", TimeZone: "Europe/Berlin")));
        Assert.Contains(Check(new WorkflowTrigger("schedule", Cron: "* * * * * *")), e => e.StartsWith("schedule needs cron", StringComparison.Ordinal));
        Assert.Contains("Unknown time zone 'Mars/Olympus'.", Check(new WorkflowTrigger("schedule", Cron: "0 8 * * *", TimeZone: "Mars/Olympus")));
        Assert.Contains("cron is only used with schedule.", Check(new WorkflowTrigger("itemAdded", Cron: "0 8 * * *")));
        Assert.Empty(Check(new WorkflowTrigger("date", List: "Tasks", Field: "dueDate", OffsetHours: -48)));
        Assert.Contains("date needs list and field (a date field of the list).", Check(new WorkflowTrigger("date", List: "Tasks")));
        Assert.Contains("field and offsetHours are only used with date.", Check(new WorkflowTrigger("itemAdded", OffsetHours: 1)));

        var inputs = new JsonObject
        {
            ["properties"] = new JsonObject { ["label"] = new JsonObject { ["type"] = "string" }, ["copies"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("label"),
        };
        Assert.Empty(Check(new WorkflowTrigger("manual", Inputs: inputs)));
        Assert.Contains("inputs are only used with manual.", Check(new WorkflowTrigger("itemAdded", Inputs: inputs)));
        Assert.Contains("inputs.x needs a type (string, number, integer, boolean, array, object).",
            Check(new WorkflowTrigger("manual", Inputs: new JsonObject { ["properties"] = new JsonObject { ["x"] = new JsonObject() } })));

        Assert.Null(WorkflowInputs.Check(inputs, new JsonObject { ["label"] = "Paid", ["copies"] = 2 }));
        Assert.Equal("The input 'label' is required.", WorkflowInputs.Check(inputs, new JsonObject { ["copies"] = 2 }));
        Assert.Equal("The input 'copies' must be of type integer.", WorkflowInputs.Check(inputs, new JsonObject { ["label"] = "x", ["copies"] = 2.5 }));
        Assert.Equal("Unknown input 'other'.", WorkflowInputs.Check(inputs, new JsonObject { ["label"] = "x", ["other"] = 1 }));
        Assert.Null(WorkflowInputs.Check(null, new JsonObject { ["anything"] = 1 }));

        // A property that is not a schema object is an error, not a crash.
        var shorthand = new JsonObject { ["properties"] = new JsonObject { ["amount"] = "number" } };
        Assert.Contains("inputs.amount needs a type (string, number, integer, boolean, array, object).", Check(new WorkflowTrigger("manual", Inputs: shorthand)));
        Assert.Equal("Unknown input 'amount'.", WorkflowInputs.Check(shorthand, new JsonObject { ["amount"] = 1 }));
    }

    [Fact]
    public void Timed_starts_have_stable_event_ids()
    {
        var workflow = Guid.CreateVersion7();
        Assert.Equal(TriggerSchedules.EventId(workflow, "2026-10-01T06:00:00Z"), TriggerSchedules.EventId(workflow, "2026-10-01T06:00:00Z"));
        Assert.NotEqual(TriggerSchedules.EventId(workflow, "2026-10-01T06:00:00Z"), TriggerSchedules.EventId(workflow, "2026-10-02T06:00:00Z"));
        Assert.NotEqual(TriggerSchedules.EventId(workflow, "x"), TriggerSchedules.EventId(Guid.CreateVersion7(), "x"));
    }

    [Fact]
    public void Workflows_use_either_steps_or_a_flow()
    {
        var triggers = new HashSet<string>(["manual"]);
        var flow = new FlowDefinition("a", new Dictionary<string, FlowNode> { ["a"] = new(FlowActivities.End) });
        Assert.Empty(Definitions.Validate(new WorkflowSpec(new WorkflowTrigger("manual"), null, null, flow), triggers, Actions));
        Assert.Equal(["Use either steps or flow, not both."], Definitions.Validate(new WorkflowSpec(new WorkflowTrigger("manual"), null, [Act("a")], flow), triggers, Actions));
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
    public void Ids_of_nested_steps_stay_within_the_limit()
    {
        var name = new string('n', Definitions.MaxNodeId);
        var nested = new WorkflowStep(StepTypes.Condition, name, Filter: "x eq 1", Then: [new(StepTypes.Action, Action: "item.update")]);
        Assert.Contains(Definitions.ValidateSteps([nested], Actions), e => e.Contains("step ids have at most", StringComparison.Ordinal));
        Assert.Empty(Definitions.ValidateSteps([nested with { Name = "short" }], Actions));
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
