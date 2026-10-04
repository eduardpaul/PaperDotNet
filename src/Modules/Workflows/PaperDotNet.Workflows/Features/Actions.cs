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

/// <summary>
/// Field values from action inputs (<c>fields</c> of <c>item.update</c> and <c>item.create</c>). Text fields get the text
/// with tokens expanded (terms and people by name). Other fields get the value a text that is exactly one token refers to,
/// with its type: <c>"total": "{step:read.json.total}"</c> sets a number, a term field gets term ids, a lookup an id.
/// </summary>
internal static class ItemFieldValues
{
    private static readonly HashSet<string> TextTypes = new(["text", "note", "email", "url", "choice"], StringComparer.Ordinal);

    public const string Description = "Field values by field name. Text may contain tokens; for fields that are not text, a text that is exactly one "
        + "token keeps the value's type, e.g. \"total\": \"{step:read.json.total}\".";

    public static async Task<JsonObject> ResolveAsync(JsonObject fields, WorkflowActivityContext context, IListItemStore items, Guid listId, CancellationToken ct)
    {
        var types = (await items.AsSystem().DescribeListAsync(context.WorkspaceId, listId, ct))?.ContentTypes
            .SelectMany(c => c.Fields).GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Type, StringComparer.Ordinal)
            ?? [];
        var values = new JsonObject();
        foreach (var (name, value) in fields)
        {
            values[name] = value is JsonValue text && text.TryGetValue<string>(out var template)
                ? types.TryGetValue(name, out var type) && !TextTypes.Contains(type)
                    ? await context.ResolveAsync(template, ct)
                    : JsonValue.Create(await context.ExpandAsync(template, ct))
                : value?.DeepClone();
        }

        return values;
    }

    /// <summary>An item as JSON for outputs: its fields and <c>id</c>.</summary>
    public static JsonObject Json(ListItemData item)
    {
        var json = item.Fields.DeepClone().AsObject();
        json["id"] = item.Id.ToString();
        return json;
    }

    /// <summary>The item an action works on: <c>list</c> and <c>id</c> when given, else the run's item; or why not.</summary>
    public static async Task<(WorkflowItem? Item, string? Error)> TargetAsync(IListItemStore items, WorkflowActivityContext context, CancellationToken ct)
    {
        if (ActivityInputs.Text(context.Inputs, "list") is null)
        {
            return context.Item is { } item ? (item, null) : (null, "The trigger has no item; give list and id.");
        }

        var (list, error) = await ListAsync(items, context, ct);
        if (list is null)
        {
            return (null, error);
        }

        return Guid.TryParse(await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "id") ?? string.Empty, ct), out var id)
            ? (new WorkflowItem(context.WorkspaceId, list.Id, id), null)
            : (null, "id must be the id of an item.");
    }

    /// <summary>A list of the workspace by name (a template), or why not.</summary>
    public static async Task<(ListData? List, string? Error)> ListAsync(IListItemStore items, WorkflowActivityContext context, CancellationToken ct)
    {
        var name = await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "list")!, ct);
        var list = (await items.AsSystem().GetListsAsync(context.WorkspaceId, null, ct)).FirstOrDefault(l => l.Name == name);
        return list is null ? (null, $"The list '{name}' does not exist in the workspace.") : (list, null);
    }
}

/// <summary>
/// <c>item.update</c>: sets field values (<c>fields</c>; text values may contain tokens) of the run's item, or of another
/// item of the workspace (<c>list</c> and <c>id</c>).
/// </summary>
internal sealed class ItemUpdateAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => "item.update";

    public string Description => "Sets field values of the item, or of another one (list, id): { \"fields\": { \"status\": \"Approved\", \"total\": \"{step:read.json.total}\" } }.";

    public IEnumerable<string> Validate(JsonObject inputs)
    {
        if (inputs["fields"] is not JsonObject { Count: > 0 })
        {
            yield return "fields must be an object with at least one value.";
        }

        if ((ActivityInputs.Text(inputs, "list") is null) != (ActivityInputs.Text(inputs, "id") is null))
        {
            yield return "list and id go together.";
        }
    }

    public JsonObject? InputSchema => ActivitySchemas.Of(["fields"],
        ("fields", ActivitySchemas.Values(ItemFieldValues.Description)),
        ("list", ActivitySchemas.Text("Another item's list, by name (template); default: the run's item.")),
        ("id", ActivitySchemas.Text("Another item's id (template), e.g. {step:find.items.0.id}.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var (target, problem) = await ItemFieldValues.TargetAsync(items, context, cancellationToken);
        if (target is null)
        {
            return WorkflowActivityResult.Fail(problem!);
        }

        var fields = await ItemFieldValues.ResolveAsync((JsonObject)context.Inputs["fields"]!, context, items, target.ListId, cancellationToken);
        var result = await items.AsSystem().UpdateAsync(target.WorkspaceId, target.ListId, target.ItemId, fields, null, cancellationToken);
        return result.Succeeded ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail(result.Describe());
    }
}

/// <summary><c>item.get</c>: reads an item of the workspace (<c>list</c>, <c>id</c>); its fields and <c>id</c> are the output.</summary>
internal sealed class ItemGetAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => "item.get";

    public string Description => "Reads an item: { \"list\": \"Vendors\", \"id\": \"{vendor}\" }; later nodes use {step:node.fieldName}.";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "list", "id");

    public JsonObject? InputSchema => ActivitySchemas.Of(["list", "id"],
        ("list", ActivitySchemas.Text("The item's list, by name (template).")), ("id", ActivitySchemas.Text("The item's id (template).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("id", ActivitySchemas.Text("The item's id; its fields are next to it.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var (target, problem) = await ItemFieldValues.TargetAsync(items, context, cancellationToken);
        if (target is null)
        {
            return WorkflowActivityResult.Fail(problem!);
        }

        var found = await items.AsSystem().GetAsync(target.WorkspaceId, target.ListId, target.ItemId, cancellationToken);
        return found is null ? WorkflowActivityResult.Fail("The item does not exist.") : WorkflowActivityResult.Ok(ItemFieldValues.Json(found));
    }
}

/// <summary><c>items.query</c>: finds items of a list of the workspace (<c>list</c>, <c>filter</c> in OData, <c>top</c>).</summary>
internal sealed class ItemsQueryAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => "items.query";

    public string Description => "Finds items: { \"list\": \"Receipt lines\", \"filter\": \"fields/receipt eq {id}\" }; output items (fields and id) and count.";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "list")
        .Concat(inputs["top"] is null || ActivityInputs.Number(inputs, "top") is >= 1 and <= ListItemQuery.MaxTop ? [] : [$"top must be from 1 to {ListItemQuery.MaxTop}."]);

    public JsonObject? InputSchema => ActivitySchemas.Of(["list"],
        ("list", ActivitySchemas.Text("A list of the workspace, by name (template).")),
        ("filter", ActivitySchemas.Text("OData filter (template), e.g. fields/status eq 'open'.")),
        ("top", ActivitySchemas.Number($"Most items (default 100, at most {ListItemQuery.MaxTop}).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("items", ActivitySchemas.Any("The items: their fields and id.")), ("count", ActivitySchemas.Number("How many.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var (list, error) = await ItemFieldValues.ListAsync(items, context, cancellationToken);
        if (list is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        var filter = ActivityInputs.Text(context.Inputs, "filter") is { } text ? await context.ExpandAsync(text, cancellationToken) : null;
        var (found, problem) = await items.AsSystem().QueryAsync(context.WorkspaceId, list.Id,
            new ListItemQuery(filter, Top: (int)(ActivityInputs.Number(context.Inputs, "top") ?? 100)), cancellationToken);
        return problem is not null
            ? WorkflowActivityResult.Fail(problem)
            : WorkflowActivityResult.Ok(new JsonObject { ["items"] = new JsonArray([.. found.Select(i => (JsonNode)ItemFieldValues.Json(i))]), ["count"] = found.Count });
    }
}

/// <summary>
/// <c>item.create</c>: creates an item in a list of the workspace (<c>list</c>, <c>fields</c> as in <c>item.update</c>,
/// optional <c>contentType</c>). Its id is the execution id, so a repeated step finds the item it created.
/// </summary>
internal sealed class ItemCreateAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => "item.create";

    public string Description => "Creates an item in a list: { \"list\": \"Receipt lines\", \"fields\": { \"title\": \"{var:line.description}\", \"receipt\": \"{id}\" } }.";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "list")
        .Concat(inputs["fields"] is JsonObject { Count: > 0 } ? [] : ["fields must be an object with at least one value."]);

    public JsonObject? InputSchema => ActivitySchemas.Of(["list", "fields"],
        ("list", ActivitySchemas.Text("A list of the workspace, by name (template).")),
        ("fields", ActivitySchemas.Values(ItemFieldValues.Description)),
        ("contentType", ActivitySchemas.Text("A content type of the list, by name or key (default: the list's first).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("itemId", ActivitySchemas.Text("Id of the new item.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var (list, error) = await ItemFieldValues.ListAsync(items, context, cancellationToken);
        if (list is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        Guid? contentTypeId = null;
        if (ActivityInputs.Text(context.Inputs, "contentType") is { } typeName)
        {
            contentTypeId = list.ContentTypes.FirstOrDefault(c => c.Name == typeName || c.Key == typeName)?.Id;
            if (contentTypeId is null)
            {
                return WorkflowActivityResult.Fail($"The list '{list.Name}' has no content type '{typeName}'.");
            }
        }

        var fields = await ItemFieldValues.ResolveAsync((JsonObject)context.Inputs["fields"]!, context, items, list.Id, cancellationToken);
        var created = await items.AsSystem().CreateAsync(context.WorkspaceId, list.Id, context.ExecutionId, fields, contentTypeId, cancellationToken);
        return created.Succeeded
            ? WorkflowActivityResult.Ok(new JsonObject { ["itemId"] = created.Item!.Id.ToString() })
            : WorkflowActivityResult.Fail(created.Describe());
    }
}

/// <summary>
/// <c>item.delete</c>: moves an item of a list of the workspace to the recycle bin (<c>list</c>, <c>id</c>: a template,
/// e.g. <c>{var:line.id}</c>). An item that is already gone is not an error.
/// </summary>
internal sealed class ItemDeleteAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => "item.delete";

    public string Description => "Moves an item to the recycle bin: { \"list\": \"Receipt lines\", \"id\": \"{var:line.id}\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "list", "id");

    public JsonObject? InputSchema => ActivitySchemas.Of(["list", "id"],
        ("list", ActivitySchemas.Text("A list of the workspace, by name (template).")),
        ("id", ActivitySchemas.Text("The item's id (template).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("deleted", ActivitySchemas.Any("false when the item was already gone.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var (list, error) = await ItemFieldValues.ListAsync(items, context, cancellationToken);
        if (list is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        if (!Guid.TryParse(await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "id")!, cancellationToken), out var id))
        {
            return WorkflowActivityResult.Fail("id must be the id of an item.");
        }

        var deleted = await items.AsSystem().DeleteAsync(context.WorkspaceId, list.Id, id, null, cancellationToken);
        return deleted.Succeeded || deleted.Status == ListItemStatus.NotFound
            ? WorkflowActivityResult.Ok(new JsonObject { ["deleted"] = deleted.Succeeded })
            : WorkflowActivityResult.Fail(deleted.Describe());
    }
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
        ActivityInputs.Text(inputs, "folder") is null && ActivityInputs.Text(inputs, "title") is null ? ["folder or title is required."] : [];

    public JsonObject? InputSchema => ActivitySchemas.Of([],
        ("folder", ActivitySchemas.Text("Folder path template, e.g. {created:yyyy}/{counterparty}; missing folders are created.")),
        ("title", ActivitySchemas.Text("New title (template).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("folder", ActivitySchemas.Text("The folder path it was filed in.")), ("title", ActivitySchemas.Text("The new title.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("The trigger has no item.");
        }

        var store = items.AsSystem();
        var output = new JsonObject();
        if (ActivityInputs.Text(context.Inputs, "folder") is { } folder)
        {
            var path = new List<string>();
            foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path.Add(Clean(await context.ExpandAsync(segment, cancellationToken)));
            }

            var (folderId, problem) = await store.EnsureFolderAsync(item.WorkspaceId, item.ListId, path, cancellationToken);
            if (problem is not null)
            {
                return WorkflowActivityResult.Fail(problem.Describe());
            }

            var moved = await store.MoveAsync(item.WorkspaceId, item.ListId, item.ItemId, folderId, cancellationToken);
            if (!moved.Succeeded)
            {
                return WorkflowActivityResult.Fail(moved.Describe());
            }

            output["folder"] = string.Join('/', path);
        }

        if (ActivityInputs.Text(context.Inputs, "title") is { } title)
        {
            var name = (await context.ExpandAsync(title, cancellationToken)).Trim();
            if (name.Length > 0)
            {
                var renamed = await store.UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, new JsonObject { ["title"] = name }, null, cancellationToken);
                if (!renamed.Succeeded)
                {
                    return WorkflowActivityResult.Fail(renamed.Describe());
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

/// <summary>Runs one action with the token context of a workflow run.</summary>
internal sealed class ActionExecutor(ActionCatalog catalog, TokenExpander tokens, IListItemStore items, IServiceProvider services)
{
    public async Task<WorkflowActivityResult> ExecuteAsync(
        ActionDefinition action, Guid workspaceId, WorkflowItem? item, Guid? actor, JsonObject? data,
        JsonObject? outputs, JsonObject? variables, string source, string executionKey, Guid executionId, CancellationToken ct, Guid? runId = null,
        WorkflowResumedWait? resumed = null, JsonObject? executionContext = null)
    {
        if (catalog.Find(action.Type) is not { } found)
        {
            return WorkflowActivityResult.Fail($"Unknown action '{action.Type}'.");
        }

        TokenScope? scope = null;
        async Task<string> ExpandAsync(string template, CancellationToken token)
        {
            scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, token, executionContext);
            return await tokens.ExpandAsync(template, scope, token);
        }

        async Task<JsonNode?> ResolveAsync(string template, CancellationToken token)
        {
            scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, token, executionContext);
            return await tokens.ValueAsync(template, scope, token);
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
                ExecutionContext = executionContext?.DeepClone().AsObject(),
                Source = source,
                RunId = runId,
                Resumed = resumed,
                ExecutionKey = executionKey,
                ExecutionId = executionId,
                ExpandAsync = ExpandAsync,
                ResolveAsync = ResolveAsync,
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
            ActivitySchemas.Of(["assignees"], ("assignees", ActivitySchemas.People("Who decides.")), ("title", ActivitySchemas.Text("Title (template).")), ("review", ActivitySchemas.Values("Optional review type and key.")),
                ("inputSchema", new JsonObject { ["type"] = "object", ["description"] = "JSON Schema of information collected with the decision." }),
                ("dueInHours", ActivitySchemas.Number("Overdue after this many hours.")), ("escalateTo", ActivitySchemas.People("Added when overdue."))),
            ActivitySchemas.Of([], ("outcome", ActivitySchemas.Text("approved or rejected.")), ("decidedBy", ActivitySchemas.Text("Id of the user who decided.")),
                ("input", new JsonObject { ["type"] = "object", ["description"] = "Submitted form values." }),
                ("comment", ActivitySchemas.Text("Their comment.")))),
        new(FlowActivities.Delay, "Waits a number of hours.", ActivityDescriptor.FlowKind, ["done"],
            ActivitySchemas.Of(["hours"], ("hours", ActivitySchemas.Number("Hours to wait."))), null),
        new(FlowActivities.If, "Continues with true or false: an OData filter on the item, an approval outcome, or a comparison.", ActivityDescriptor.FlowKind,
            ["true", "false", "error"],
            ActivitySchemas.Of([], ("filter", ActivitySchemas.Text("OData filter on the item.")), ("step", ActivitySchemas.Text("An approval node.")),
                ("is", ActivitySchemas.Text("approved or rejected.")), ("left", ActivitySchemas.Text("Text with tokens.")),
                ("op", ActivitySchemas.Text(string.Join(", ", Comparison.Operators))), ("right", ActivitySchemas.Text("Text with tokens."))), null),
        new(FlowActivities.SetVariable, "Sets a variable of the run.", ActivityDescriptor.FlowKind, ["done"],
            ActivitySchemas.Of(["name"], ("name", ActivitySchemas.Text("Variable name.")), ("value", ActivitySchemas.Any("Text with tokens, or any JSON value."))), null),
        new(FlowActivities.ForEach, "Runs the nodes on its item port for each element of an array or of the items a query finds; the last of them leads back.",
            ActivityDescriptor.FlowKind, ["item", "done", "error"],
            ActivitySchemas.Of([], ("items", ActivitySchemas.Any("An array, or a text that is exactly one token, e.g. {step:read.json.lines}.")),
                ("query", ActivitySchemas.Values("list (by name) and filter (OData, tokens allowed): each element is an item's fields with its id.")),
                ("as", ActivitySchemas.Text("The variable that holds the element (default item): {var:item.name}."))),
            ActivitySchemas.Of([], ("index", ActivitySchemas.Number("The element's position (from 0).")), ("count", ActivitySchemas.Number("How many elements.")))),
        new(FlowActivities.Script, "Runs JavaScript that reads the workspace's lists and changes them: item, vars, steps, trigger, items.get/query/create/update/delete, log.",
            ActivityDescriptor.FlowKind, ["done", "error"],
            ActivitySchemas.Of(["code"], ("code", ActivitySchemas.Any("The script: a string or an array of lines. `return` gives the result."))),
            ActivitySchemas.Of([], ("result", ActivitySchemas.Any("What the script returned.")), ("created", ActivitySchemas.Any("Ids of the items it created.")),
                ("updated", ActivitySchemas.Number("Items it updated.")), ("deleted", ActivitySchemas.Number("Items it deleted.")))),
        new(FlowActivities.Raise, "Raises an event of this workflow: other workflows start on wf.{key}.{event}, with the run's item.", ActivityDescriptor.FlowKind, ["done"],
            ActivitySchemas.Of(["event"], ("event", ActivitySchemas.Text("The event's name, e.g. noText.")),
                ("data", ActivitySchemas.Values("The event's data: strings may be tokens (one token keeps its type)."))),
            ActivitySchemas.Of([], ("event", ActivitySchemas.Text("The trigger type raised, e.g. wf.documents.text.noText.")))),
        new(FlowActivities.End, "Ends the run as completed.", ActivityDescriptor.FlowKind, [], null, null),
        new(FlowActivities.Fail, "Ends the run as failed.", ActivityDescriptor.FlowKind, [],
            ActivitySchemas.Of([], ("message", ActivitySchemas.Text("The error (template)."))), null),
    ];
}

/// <summary>Checks current item terms by path, including descendants, after a long workflow wait.</summary>
internal sealed class ItemHasTermsAction(IListItemStore items, PaperDotNet.Taxonomy.Contracts.ITermStore terms) : IWorkflowActivity
{
    public string Key => "item.hasTerms";
    public string Description => "Checks whether the current item has a term or one below it, by Group/Set/Term path.";
    public IReadOnlyList<string> Outcomes => ["matched", "unmatched"];
    public JsonObject? InputSchema => ActivitySchemas.Of(["terms"], ("terms", ActivitySchemas.Texts("Term paths; any match continues on matched.")));
    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Texts(inputs, "terms") is { Count: > 0 } ? [] : ["terms are required."];

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var item = context.Item is { } target ? await items.AsSystem().GetAsync(target.WorkspaceId, target.ListId, target.ItemId, cancellationToken) : null;
        var wanted = new HashSet<Guid>();
        foreach (var path in ActivityInputs.Texts(context.Inputs, "terms") ?? [])
        {
            if (await terms.FindTermByPathAsync(await context.ExpandAsync(path, cancellationToken), cancellationToken) is { } id)
            {
                wanted.Add(id);
            }
        }

        foreach (var descendants in (await terms.GetDescendantsAsync(wanted, cancellationToken)).Values)
        {
            wanted.UnionWith(descendants);
        }

        var matched = item?.Fields.SelectMany(f => f.Value is JsonArray values ? values.AsEnumerable() : [f.Value])
            .Any(v => v is JsonValue text && text.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) && wanted.Contains(id)) == true;
        return WorkflowActivityResult.Ok(matched ? "matched" : "unmatched");
    }
}
