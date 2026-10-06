using System.Text.Json.Nodes;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.UnitTests;

public sealed class WorkflowDefinitionTests
{
    [Theory]
    [InlineData("manual", "list", "selection", true)]
    [InlineData("manual", "list", "perItem", true)]
    [InlineData("manual", "workspace", "selection", false)]
    [InlineData("itemAdded", "list", "selection", false)]
    [InlineData("manual", "list", "batch", false)]
    public void Selection_mode_is_only_valid_for_item_based_manual_triggers(string trigger, string scope, string mode, bool valid)
    {
        var spec = new WorkflowSpec(new WorkflowTrigger(trigger, scope == "list" ? "Items" : null, SelectionMode: mode), null,
            [Act("a")], Scope: scope);
        Assert.Equal(valid, Definitions.Validate(spec, new[] { "manual", "itemAdded" }.ToHashSet(), Actions).Count == 0);
    }

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
    public void Domain_selection_schemas_require_typed_configuration_and_validate_cardinality()
    {
        static JsonObject Schema(string property) => JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"selection\":" + property + "},\"required\":[\"selection\"]}")!.AsObject();
        Assert.NotEmpty(WorkflowInputs.ValidateSchema(Schema("""{"type":"string","x-paperdotnet":{"kind":"relationship"}}""")));
        Assert.NotEmpty(WorkflowInputs.ValidateSchema(Schema("""{"type":"number","x-paperdotnet":{"kind":"terms"}}""")));
        Assert.NotEmpty(WorkflowInputs.ValidateSchema(Schema("""{"type":"string","x-paperdotnet":{"kind":"terms","groupId":"invalid"}}""")));
        Assert.NotEmpty(WorkflowInputs.ValidateSchema(Schema("""{"type":"string","x-paperdotnet":{"kind":"keywords","termIds":[]}}""")));
        var schema = Schema("""{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":2,"uniqueItems":true,"x-paperdotnet":{"kind":"terms"}}""");
        Assert.Empty(WorkflowInputs.ValidateSchema(schema));
        Assert.NotNull(WorkflowInputs.Check(schema, new JsonObject { ["selection"] = new JsonArray() }));
        Assert.NotNull(WorkflowInputs.Check(schema, new JsonObject { ["selection"] = new JsonArray("one", "one") }));
        Assert.NotNull(WorkflowInputs.Check(schema, new JsonObject { ["selection"] = new JsonArray("one", "two", "three") }));
        Assert.Null(WorkflowInputs.Check(schema, new JsonObject { ["selection"] = new JsonArray("one", "two") }));
    }

    [Fact]
    public void Approval_input_schemas_are_validated_and_compiled_for_steps_and_flows()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""")!.AsObject();
        var step = new WorkflowStep("approval", Name: "Review", Assignees: ["admin"], InputSchema: schema);
        Assert.Empty(Definitions.ValidateSteps([step], Actions));
        Assert.True(JsonNode.DeepEquals(schema, Definitions.Compile([step]).Nodes["Review"].Inputs!["inputSchema"]));
        var invalid = JsonNode.Parse("""{"type":"object","properties":{"answer":{"type":"unsupported"}}}""")!.AsObject();
        Assert.NotEmpty(Definitions.ValidateSteps([step with { InputSchema = invalid }], Actions));
        var flow = new FlowDefinition("Review", new Dictionary<string, FlowNode>
        {
            ["Review"] = new("approval", new JsonObject { ["assignees"] = new JsonArray("admin"), ["inputSchema"] = invalid.DeepClone() }),
        });
        Assert.NotEmpty(Definitions.ValidateFlow(flow, Actions));
    }

    [Fact]
    public void Explicit_scopes_restrict_trigger_targets()
    {
        var triggers = new HashSet<string>(["manual", "schedule", "webhook", "itemAdded", "itemUpdated"]);
        List<string> Check(string scope, params WorkflowTrigger[] values) => Definitions.Validate(
            new WorkflowSpec(null, null, [Act("a")], Triggers: values, Scope: scope), triggers, Actions);
        Assert.Empty(Check("workspace", new WorkflowTrigger("manual"), new WorkflowTrigger("webhook"), new WorkflowTrigger("schedule", Cron: "0 8 * * *")));
        Assert.NotEmpty(Check("workspace", new WorkflowTrigger("manual", List: "Invoices")));
        Assert.Empty(Check("workspace", new WorkflowTrigger("itemAdded")));
        Assert.Empty(Check("workspace", new WorkflowTrigger("itemUpdated", ContentType: "Invoice", Terms: ["Documents/Tags/Receipt"])));
        Assert.NotEmpty(Check("list", new WorkflowTrigger("manual")));
        Assert.Empty(Check("list", new WorkflowTrigger("manual", List: "Invoices")));
        Assert.NotEmpty(Check("unknown", new WorkflowTrigger("manual")));
    }

    [Fact]
    public void Launch_inputs_apply_defaults_and_enforce_choices_and_ranges()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"choice":{"type":"string","enum":["A","B"]},"count":{"type":"integer","minimum":1,"maximum":5,"default":2},"flag":{"type":"boolean","default":false}},"required":["choice"]}
            """)!.AsObject();
        var inputs = WorkflowInputs.WithDefaults(schema, new JsonObject { ["choice"] = "A" });
        Assert.Empty(WorkflowInputs.ValidateSchema(schema));
        Assert.Null(WorkflowInputs.Check(schema, inputs));
        Assert.Equal(2, inputs["count"]!.GetValue<int>());
        Assert.False(inputs["flag"]!.GetValue<bool>());
        inputs["choice"] = "C";
        Assert.NotNull(WorkflowInputs.Check(schema, inputs));
        inputs["choice"] = "A";
        inputs["count"] = 10;
        Assert.NotNull(WorkflowInputs.Check(schema, inputs));
        Assert.Null(schema["properties"]!["choice"]!["default"]);
    }

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
    public void Input_tokens_are_separate_from_mutable_variables_and_trigger_data()
    {
        var context = new JsonObject { ["workspaceId"] = "workspace", ["input"] = new JsonObject { ["label"] = "original" } };
        var variables = new JsonObject { ["label"] = "changed" };
        var scope = new TokenScope(null, null, null, variables, new JsonObject { ["label"] = "event" }, context);
        Assert.Equal("original", scope.Input("label")!.GetValue<string>());
        Assert.Equal("changed", scope.Variable("label")!.GetValue<string>());
        Assert.Equal("event", scope.Trigger("label")!.GetValue<string>());
        Assert.Equal("workspace", scope.Execution("workspaceId")!.GetValue<string>());
    }

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
    public void Several_triggers_are_checked_each_and_data_filters_match_values()
    {
        var triggers = new HashSet<string>(["schedule", "manual", "itemAdded", "document.processed"]);
        List<string> Check(WorkflowSpec spec) => Definitions.Validate(spec, triggers, Actions);

        var two = new WorkflowSpec(null, null, [Act("a")], Triggers: [new WorkflowTrigger("itemAdded", "Bills"), new WorkflowTrigger("schedule", Cron: "0 8 * * *")]);
        Assert.Empty(Check(two));
        Assert.Equal("itemAdded,schedule", two.TriggerTypes);
        Assert.Equal(["A trigger is required (trigger, or a list of triggers)."], Check(new WorkflowSpec(null, null, [Act("a")], Triggers: [])));
        Assert.Equal(["Use either trigger or triggers, not both."],
            Check(new WorkflowSpec(new WorkflowTrigger("manual"), null, [Act("a")], Triggers: [new WorkflowTrigger("manual")])));
        Assert.Contains("triggers[1]: schedule needs cron: 5 fields (minute hour day month weekday), e.g. 0 8 * * 1-5.",
            Check(new WorkflowSpec(null, null, [Act("a")], Triggers: [new WorkflowTrigger("manual"), new WorkflowTrigger("schedule")])));
        Assert.Contains("A workflow has at most 10 triggers.",
            Check(new WorkflowSpec(null, null, [Act("a")], Triggers: [.. Enumerable.Repeat(new WorkflowTrigger("manual"), 11)])));
        Assert.Contains("triggers[1]: A condition needs the trigger's list (its fields).",
            Check(new WorkflowSpec(null, "fields/a eq 1", [Act("a")], Triggers: [new WorkflowTrigger("itemAdded", "Bills"), new WorkflowTrigger("manual")])));

        var scans = new WorkflowTrigger("document.processed", Data: new JsonObject { ["hasText"] = false });
        Assert.Empty(Check(new WorkflowSpec(scans, null, [Act("a")])));
        Assert.True(scans.MatchesData(new JsonObject { ["hasText"] = false, ["pageCount"] = 3 }));
        Assert.False(scans.MatchesData(new JsonObject { ["hasText"] = true }));
        Assert.False(scans.MatchesData(null));
        Assert.True(new WorkflowTrigger("document.processed").MatchesData(null));
        Assert.Contains("data is only used with module and extension triggers (they have data).",
            Check(new WorkflowSpec(new WorkflowTrigger("itemAdded", Data: []), null, [Act("a")])));
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
