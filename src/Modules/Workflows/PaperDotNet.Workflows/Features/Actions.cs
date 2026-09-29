using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>All actions of the current scope: built-in and from enabled extensions.</summary>
internal sealed class ActionCatalog(IEnumerable<IWorkflowActivity> actions)
{
    private readonly Dictionary<string, IWorkflowActivity> _byKey = actions
        .GroupBy(a => a.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IEnumerable<IWorkflowActivity> All => _byKey.Values.OrderBy(a => a.Key, StringComparer.Ordinal);

    /// <summary>The flow activities (the engine's own) and the actions, with their ports and schemas.</summary>
    public IEnumerable<ActivityDescriptor> Describe() =>
        FlowActivityDescriptors.All.Concat(All.Select(a => new ActivityDescriptor(
            a.Key, a.Description, ActivityDescriptor.ActionKind, [.. FlowActivities.Ports(a.Key).Order(StringComparer.Ordinal), .. a.Outcomes],
            a.InputSchema, a.OutputSchema)));

    public IWorkflowActivity? Find(string key) => _byKey.GetValueOrDefault(key);

    public IEnumerable<string> Validate(ActionDefinition action) =>
        Find(action.Type) is { } found
            ? found.Validate(action.Inputs ?? [])
            : [$"Unknown action '{action.Type}'."];
}

/// <summary>Small JSON Schemas for the inputs and outputs of activities (the catalog, forms).</summary>
internal static class Schemas
{
    public static JsonObject Object(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0)
        {
            result["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]);
        }

        return result;
    }

    public static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Texts(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    public static JsonObject Values(string description) => new() { ["type"] = "object", ["description"] = description };

    public static JsonObject Any(string description) => new() { ["description"] = description };

    public static JsonObject People(string description) => Texts(description + " User names, group:Name, field:name, creator or actor.");
}

/// <summary>Reading inputs of actions.</summary>
internal static class Inputs
{
    public static string? Text(JsonObject inputs, string name) =>
        inputs[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    public static List<string>? Texts(JsonObject inputs, string name) => inputs[name] switch
    {
        JsonArray array => [.. array.Select(e => e is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).OfType<string>()],
        JsonValue value when value.TryGetValue<string>(out var single) => [single],
        _ => null,
    };

    public static double? Number(JsonObject inputs, string name) =>
        inputs[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    public static IEnumerable<string> Required(JsonObject inputs, params string[] names) =>
        names.Where(n => Text(inputs, n) is null).Select(n => $"{n} is required.");
}

/// <summary><c>item.update</c>: sets field values of the item (<c>fields</c>; text values may contain tokens).</summary>
internal sealed class ItemUpdateAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => "item.update";

    public string Description => "Sets field values of the item: { \"fields\": { \"status\": \"Approved\" } }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        inputs["fields"] is JsonObject { Count: > 0 } ? [] : ["fields must be an object with at least one value."];

    public JsonObject? InputSchema => Schemas.Object(["fields"], ("fields", Schemas.Values("Field values to set by field name; text may contain tokens.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("The trigger has no item.");
        }

        var fields = new JsonObject();
        foreach (var (name, value) in (JsonObject)context.Inputs["fields"]!)
        {
            fields[name] = value is JsonValue text && text.TryGetValue<string>(out var template)
                ? JsonValue.Create(await context.ExpandAsync(template, cancellationToken))
                : value?.DeepClone();
        }

        var result = await items.AsSystem().UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, fields, null, cancellationToken);
        return result.Succeeded ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail(Describe(result));
    }

    public static string Describe(ListItemResult result) => result.Status switch
    {
        ListItemStatus.Invalid => "Invalid values: " + string.Join(" ", result.Errors?.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}")) ?? []),
        ListItemStatus.Rejected => result.Message ?? "The change was rejected.",
        ListItemStatus.NotFound => "The item no longer exists.",
        _ => result.Status.ToString(),
    };
}

/// <summary>
/// <c>item.file</c> (DOC-14, path templates): moves the item into <c>folder</c> (e.g. <c>Finance/{created:yyyy}/{counterparty}</c>),
/// creating missing folders, and optionally renames it (<c>title</c>). Each folder level is expanded on its own, so values
/// cannot add levels; characters that are not allowed in names are replaced.
/// </summary>
internal sealed class ItemFileAction(IListItemStore items) : IWorkflowActivity
{
    private static readonly char[] Invalid = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];
    private const int MaxSegment = 120;

    public string Key => "item.file";

    public string Description => "Files the item into a folder path and renames it from its values: { \"folder\": \"{created:yyyy}/{counterparty}\", \"title\": \"{invoiceNumber}\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        Inputs.Text(inputs, "folder") is null && Inputs.Text(inputs, "title") is null ? ["folder or title is required."] : [];

    public JsonObject? InputSchema => Schemas.Object([],
        ("folder", Schemas.Text("Folder path template, e.g. {created:yyyy}/{counterparty}; missing folders are created.")),
        ("title", Schemas.Text("New title (template).")));

    public JsonObject? OutputSchema => Schemas.Object([], ("folder", Schemas.Text("The folder path it was filed in.")), ("title", Schemas.Text("The new title.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("The trigger has no item.");
        }

        var store = items.AsSystem();
        var output = new JsonObject();
        if (Inputs.Text(context.Inputs, "folder") is { } folder)
        {
            var path = new List<string>();
            foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path.Add(Clean(await context.ExpandAsync(segment, cancellationToken)));
            }

            var (folderId, problem) = await store.EnsureFolderAsync(item.WorkspaceId, item.ListId, path, cancellationToken);
            if (problem is not null)
            {
                return WorkflowActivityResult.Fail(ItemUpdateAction.Describe(problem));
            }

            var moved = await store.MoveAsync(item.WorkspaceId, item.ListId, item.ItemId, folderId, cancellationToken);
            if (!moved.Succeeded)
            {
                return WorkflowActivityResult.Fail(ItemUpdateAction.Describe(moved));
            }

            output["folder"] = string.Join('/', path);
        }

        if (Inputs.Text(context.Inputs, "title") is { } title)
        {
            var name = (await context.ExpandAsync(title, cancellationToken)).Trim();
            if (name.Length > 0)
            {
                var renamed = await store.UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, new JsonObject { ["title"] = name }, null, cancellationToken);
                if (!renamed.Succeeded)
                {
                    return WorkflowActivityResult.Fail(ItemUpdateAction.Describe(renamed));
                }

                output["title"] = name;
            }
        }

        return WorkflowActivityResult.Ok(output);
    }

    internal static string Clean(string segment)
    {
        var cleaned = new string([.. segment.Select(c => char.IsControl(c) || Invalid.Contains(c) ? '-' : c)]).Trim().Trim('.').Trim();
        cleaned = cleaned.Length > MaxSegment ? cleaned[..MaxSegment].Trim() : cleaned;
        return cleaned.Length == 0 ? "_" : cleaned;
    }
}

/// <summary><c>task.create</c>: creates a task in a task list of the workspace (<c>list</c>, <c>title</c>, <c>assignedTo</c>, <c>dueInDays</c>, <c>priority</c>, <c>description</c>).</summary>
internal sealed class TaskCreateAction(IListItemStore items, RecipientResolver recipients, TimeProvider time) : IWorkflowActivity
{
    public string Key => "task.create";

    public string Description => "Creates a task: { \"list\": \"Tasks\", \"title\": \"Check {title}\", \"assignedTo\": [\"field:owner\"], \"dueInDays\": 3 }.";

    public IEnumerable<string> Validate(JsonObject inputs) => Inputs.Required(inputs, "list", "title");

    public JsonObject? InputSchema => Schemas.Object(["list", "title"],
        ("list", Schemas.Text("A task list of the workspace, by name.")),
        ("title", Schemas.Text("Title (template).")),
        ("assignedTo", Schemas.People("Assignees.")),
        ("dueInDays", Schemas.Number("Due this many days from now.")),
        ("priority", Schemas.Text("Priority.")),
        ("description", Schemas.Text("Description (template).")));

    public JsonObject? OutputSchema => Schemas.Object([], ("taskId", Schemas.Text("Id of the task.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var store = items.AsSystem();
        var listName = Inputs.Text(context.Inputs, "list")!;
        var list = (await store.GetListsAsync(context.WorkspaceId, null, cancellationToken)).FirstOrDefault(l => l.Name == listName);
        if (list is null)
        {
            return WorkflowActivityResult.Fail($"The list '{listName}' does not exist in the workspace.");
        }

        var fields = new JsonObject { ["title"] = await context.ExpandAsync(Inputs.Text(context.Inputs, "title")!, cancellationToken) };
        var source = context.Item is { } item ? await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, cancellationToken) : null;
        if (Inputs.Texts(context.Inputs, "assignedTo") is { Count: > 0 } specs)
        {
            var (users, _) = await recipients.ResolveAsync(specs, source, context.UserId, cancellationToken);
            fields["assignedTo"] = new JsonArray([.. users.Select(u => JsonValue.Create(u.ToString()))]);
        }

        if (Inputs.Number(context.Inputs, "dueInDays") is { } days)
        {
            fields["dueDate"] = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(days)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        foreach (var name in new[] { "priority", "description" })
        {
            if (Inputs.Text(context.Inputs, name) is { } value)
            {
                fields[name] = await context.ExpandAsync(value, cancellationToken);
            }
        }

        // The execution id as the task's id: a repeated step finds the task it created before.
        var created = await store.CreateAsync(context.WorkspaceId, list.Id, context.ExecutionId, fields, null, cancellationToken);
        return created.Succeeded
            ? WorkflowActivityResult.Ok(new JsonObject { ["taskId"] = created.Item!.Id.ToString() })
            : WorkflowActivityResult.Fail(ItemUpdateAction.Describe(created));
    }
}

/// <summary><c>notify</c>: sends a notification (<c>to</c>, <c>title</c>, <c>body</c>) linked to the item.</summary>
internal sealed class NotifyAction(IListItemStore items, RecipientResolver recipients, INotificationSender sender) : IWorkflowActivity
{
    public string Key => "notify";

    public string Description => "Notifies people: { \"to\": [\"alice\", \"group:Accountants\", \"field:owner\", \"creator\"], \"title\": \"{title} was filed\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        Inputs.Required(inputs, "title").Concat(Inputs.Texts(inputs, "to") is { Count: > 0 } ? [] : ["to is required."]);

    public JsonObject? InputSchema => Schemas.Object(["to", "title"],
        ("to", Schemas.People("Recipients.")), ("title", Schemas.Text("Title (template).")), ("body", Schemas.Text("Text (template).")));

    public JsonObject? OutputSchema => Schemas.Object([],
        ("recipients", Schemas.Number("How many people were notified.")), ("unknown", Schemas.Texts("Recipients that were not found.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var item = context.Item is { } target ? await items.AsSystem().GetAsync(target.WorkspaceId, target.ListId, target.ItemId, cancellationToken) : null;
        var (users, unknown) = await recipients.ResolveAsync(Inputs.Texts(context.Inputs, "to")!, item, context.UserId, cancellationToken);
        var title = await context.ExpandAsync(Inputs.Text(context.Inputs, "title")!, cancellationToken);
        var body = Inputs.Text(context.Inputs, "body") is { } text ? await context.ExpandAsync(text, cancellationToken) : null;
        var link = context.Item is { } i ? new NotificationLink(i.WorkspaceId, i.ListId, i.ItemId) : null;
        var sent = await sender.SendAsync(new NotificationMessage(NotificationTypes.Workflow, title, body, link, context.ExecutionKey), users, cancellationToken);
        return WorkflowActivityResult.Ok(new JsonObject
        {
            ["recipients"] = sent,
            ["unknown"] = unknown.Count == 0 ? null : new JsonArray([.. unknown.Select(u => JsonValue.Create(u))]),
        });
    }
}

/// <summary>Runs one action with the token context of a workflow run.</summary>
internal sealed class ActionExecutor(ActionCatalog catalog, TokenExpander tokens, IListItemStore items, IServiceProvider services)
{
    public async Task<WorkflowActivityResult> ExecuteAsync(
        ActionDefinition action, Guid workspaceId, WorkflowItem? item, Guid? actor, JsonObject? data,
        JsonObject? outputs, JsonObject? variables, string source, string executionKey, Guid executionId, CancellationToken ct, Guid? runId = null)
    {
        if (catalog.Find(action.Type) is not { } found)
        {
            return WorkflowActivityResult.Fail($"Unknown action '{action.Type}'.");
        }

        TokenScope? scope = null;
        async Task<string> ExpandAsync(string template, CancellationToken token)
        {
            if (scope is null)
            {
                var store = items.AsSystem();
                var current = item is null ? null : await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, token);
                var list = item is null ? null : await store.GetListAsync(item.WorkspaceId, item.ListId, token);
                scope = new TokenScope(current, list?.Name, outputs, variables, data);
            }

            return await tokens.ExpandAsync(template, scope, token);
        }

        try
        {
            return await found.ExecuteAsync(new WorkflowActivityContext
            {
                WorkspaceId = workspaceId,
                Item = item,
                Inputs = (action.Inputs ?? []).DeepClone().AsObject(),
                Services = services,
                UserId = actor,
                Data = data,
                Source = source,
                RunId = runId,
                ExecutionKey = executionKey,
                ExecutionId = executionId,
                ExpandAsync = ExpandAsync,
            }, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return WorkflowActivityResult.Fail(ex.Message);
        }
    }
}

/// <summary>An activity in the catalog: <c>kind</c> is <c>flow</c> (run by the engine) or <c>action</c>.</summary>
public sealed record ActivityDescriptor(string Key, string Description, string Kind, IReadOnlyList<string> Ports, JsonObject? InputSchema, JsonObject? OutputSchema)
{
    public const string FlowKind = "flow";
    public const string ActionKind = "action";
}

/// <summary>Descriptors of the flow activities (docs/workflows.md).</summary>
internal static class FlowActivityDescriptors
{
    public static readonly ActivityDescriptor[] All =
    [
        new(FlowActivities.Approval, "Asks people to approve or reject; overdue requests are escalated.", ActivityDescriptor.FlowKind, ["approved", "rejected", "done"],
            Schemas.Object(["assignees"], ("assignees", Schemas.People("Who decides.")), ("title", Schemas.Text("Title (template).")),
                ("dueInHours", Schemas.Number("Overdue after this many hours.")), ("escalateTo", Schemas.People("Added when overdue."))),
            Schemas.Object([], ("outcome", Schemas.Text("approved or rejected.")), ("decidedBy", Schemas.Text("Id of the user who decided.")),
                ("comment", Schemas.Text("Their comment.")))),
        new(FlowActivities.Delay, "Waits a number of hours.", ActivityDescriptor.FlowKind, ["done"],
            Schemas.Object(["hours"], ("hours", Schemas.Number("Hours to wait."))), null),
        new(FlowActivities.If, "Continues with true or false: an OData filter on the item, an approval outcome, or a comparison.", ActivityDescriptor.FlowKind,
            ["true", "false", "error"],
            Schemas.Object([], ("filter", Schemas.Text("OData filter on the item.")), ("step", Schemas.Text("An approval node.")),
                ("is", Schemas.Text("approved or rejected.")), ("left", Schemas.Text("Text with tokens.")),
                ("op", Schemas.Text(string.Join(", ", Comparison.Operators))), ("right", Schemas.Text("Text with tokens."))), null),
        new(FlowActivities.SetVariable, "Sets a variable of the run.", ActivityDescriptor.FlowKind, ["done"],
            Schemas.Object(["name"], ("name", Schemas.Text("Variable name.")), ("value", Schemas.Any("Text with tokens, or any JSON value."))), null),
        new(FlowActivities.End, "Ends the run as completed.", ActivityDescriptor.FlowKind, [], null, null),
        new(FlowActivities.Fail, "Ends the run as failed.", ActivityDescriptor.FlowKind, [],
            Schemas.Object([], ("message", Schemas.Text("The error (template)."))), null),
    ];
}
