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

/// <summary><c>item.update</c>: sets field values of the item (<c>fields</c>; text values may contain tokens).</summary>
internal sealed class ItemUpdateAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => "item.update";

    public string Description => "Sets field values of the item: { \"fields\": { \"status\": \"Approved\" } }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        inputs["fields"] is JsonObject { Count: > 0 } ? [] : ["fields must be an object with at least one value."];

    public JsonObject? InputSchema => ActivitySchemas.Of(["fields"], ("fields", ActivitySchemas.Values("Field values to set by field name; text may contain tokens.")));

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
        return result.Succeeded ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail(result.Describe());
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
        WorkflowResumedWait? resumed = null)
    {
        if (catalog.Find(action.Type) is not { } found)
        {
            return WorkflowActivityResult.Fail($"Unknown action '{action.Type}'.");
        }

        TokenScope? scope = null;
        async Task<string> ExpandAsync(string template, CancellationToken token)
        {
            scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, token);
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
                Resumed = resumed,
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
            ActivitySchemas.Of(["assignees"], ("assignees", ActivitySchemas.People("Who decides.")), ("title", ActivitySchemas.Text("Title (template).")),
                ("dueInHours", ActivitySchemas.Number("Overdue after this many hours.")), ("escalateTo", ActivitySchemas.People("Added when overdue."))),
            ActivitySchemas.Of([], ("outcome", ActivitySchemas.Text("approved or rejected.")), ("decidedBy", ActivitySchemas.Text("Id of the user who decided.")),
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
        new(FlowActivities.End, "Ends the run as completed.", ActivityDescriptor.FlowKind, [], null, null),
        new(FlowActivities.Fail, "Ends the run as failed.", ActivityDescriptor.FlowKind, [],
            ActivitySchemas.Of([], ("message", ActivitySchemas.Text("The error (template)."))), null),
    ];
}
