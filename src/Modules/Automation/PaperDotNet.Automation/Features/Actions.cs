using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>All actions of the current scope: built-in and from enabled extensions.</summary>
internal sealed class ActionCatalog(IEnumerable<IAutomationAction> actions)
{
    private readonly Dictionary<string, IAutomationAction> _byKey = actions
        .GroupBy(a => a.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IEnumerable<IAutomationAction> All => _byKey.Values.OrderBy(a => a.Key, StringComparer.Ordinal);

    public IAutomationAction? Find(string key) => _byKey.GetValueOrDefault(key);

    public IEnumerable<string> Validate(ActionDefinition action) =>
        Find(action.Type) is { } found
            ? found.Validate(action.Inputs ?? [])
            : [$"Unknown action '{action.Type}'."];
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
        inputs[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    public static IEnumerable<string> Required(JsonObject inputs, params string[] names) =>
        names.Where(n => Text(inputs, n) is null).Select(n => $"{n} is required.");
}

/// <summary><c>item.update</c>: sets field values of the item (<c>fields</c>; text values may contain tokens).</summary>
internal sealed class ItemUpdateAction(IListItemStore items) : IAutomationAction
{
    public string Key => "item.update";

    public string Description => "Sets field values of the item: { \"fields\": { \"status\": \"Approved\" } }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        inputs["fields"] is JsonObject { Count: > 0 } ? [] : ["fields must be an object with at least one value."];

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return AutomationActionResult.Fail("The trigger has no item.");
        }

        var fields = new JsonObject();
        foreach (var (name, value) in (JsonObject)context.Inputs["fields"]!)
        {
            fields[name] = value is JsonValue text && text.TryGetValue<string>(out var template)
                ? JsonValue.Create(await context.ExpandAsync(template, cancellationToken))
                : value?.DeepClone();
        }

        var result = await items.AsSystem().UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, fields, null, cancellationToken);
        return result.Succeeded ? AutomationActionResult.Ok() : AutomationActionResult.Fail(Describe(result));
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
internal sealed class ItemFileAction(IListItemStore items) : IAutomationAction
{
    private static readonly char[] Invalid = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];
    private const int MaxSegment = 120;

    public string Key => "item.file";

    public string Description => "Files the item into a folder path and renames it from its values: { \"folder\": \"{created:yyyy}/{counterparty}\", \"title\": \"{invoiceNumber}\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        Inputs.Text(inputs, "folder") is null && Inputs.Text(inputs, "title") is null ? ["folder or title is required."] : [];

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return AutomationActionResult.Fail("The trigger has no item.");
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
                return AutomationActionResult.Fail(ItemUpdateAction.Describe(problem));
            }

            var moved = await store.MoveAsync(item.WorkspaceId, item.ListId, item.ItemId, folderId, cancellationToken);
            if (!moved.Succeeded)
            {
                return AutomationActionResult.Fail(ItemUpdateAction.Describe(moved));
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
                    return AutomationActionResult.Fail(ItemUpdateAction.Describe(renamed));
                }

                output["title"] = name;
            }
        }

        return AutomationActionResult.Ok(output);
    }

    internal static string Clean(string segment)
    {
        var cleaned = new string([.. segment.Select(c => char.IsControl(c) || Invalid.Contains(c) ? '-' : c)]).Trim().Trim('.').Trim();
        cleaned = cleaned.Length > MaxSegment ? cleaned[..MaxSegment].Trim() : cleaned;
        return cleaned.Length == 0 ? "_" : cleaned;
    }
}

/// <summary><c>task.create</c>: creates a task in a task list of the workspace (<c>list</c>, <c>title</c>, <c>assignedTo</c>, <c>dueInDays</c>, <c>priority</c>, <c>description</c>).</summary>
internal sealed class TaskCreateAction(IListItemStore items, RecipientResolver recipients, TimeProvider time) : IAutomationAction
{
    public string Key => "task.create";

    public string Description => "Creates a task: { \"list\": \"Tasks\", \"title\": \"Check {title}\", \"assignedTo\": [\"field:owner\"], \"dueInDays\": 3 }.";

    public IEnumerable<string> Validate(JsonObject inputs) => Inputs.Required(inputs, "list", "title");

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken)
    {
        var store = items.AsSystem();
        var listName = Inputs.Text(context.Inputs, "list")!;
        var list = (await store.GetListsAsync(context.WorkspaceId, null, cancellationToken)).FirstOrDefault(l => l.Name == listName);
        if (list is null)
        {
            return AutomationActionResult.Fail($"The list '{listName}' does not exist in the workspace.");
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

        var created = await store.CreateAsync(context.WorkspaceId, list.Id, fields, null, cancellationToken);
        return created.Succeeded
            ? AutomationActionResult.Ok(new JsonObject { ["taskId"] = created.Item!.Id.ToString() })
            : AutomationActionResult.Fail(ItemUpdateAction.Describe(created));
    }
}

/// <summary><c>notify</c>: sends a notification (<c>to</c>, <c>title</c>, <c>body</c>) linked to the item.</summary>
internal sealed class NotifyAction(IListItemStore items, RecipientResolver recipients, INotificationSender sender) : IAutomationAction
{
    public string Key => "notify";

    public string Description => "Notifies people: { \"to\": [\"alice\", \"group:Accountants\", \"field:owner\", \"creator\"], \"title\": \"{title} was filed\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        Inputs.Required(inputs, "title").Concat(Inputs.Texts(inputs, "to") is { Count: > 0 } ? [] : ["to is required."]);

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken)
    {
        var item = context.Item is { } target ? await items.AsSystem().GetAsync(target.WorkspaceId, target.ListId, target.ItemId, cancellationToken) : null;
        var (users, unknown) = await recipients.ResolveAsync(Inputs.Texts(context.Inputs, "to")!, item, context.UserId, cancellationToken);
        var title = await context.ExpandAsync(Inputs.Text(context.Inputs, "title")!, cancellationToken);
        var body = Inputs.Text(context.Inputs, "body") is { } text ? await context.ExpandAsync(text, cancellationToken) : null;
        var link = context.Item is { } i ? new NotificationLink(i.WorkspaceId, i.ListId, i.ItemId) : null;
        var sent = await sender.SendAsync(new NotificationMessage(NotificationTypes.Automation, title, body, link, context.ExecutionKey), users, cancellationToken);
        return AutomationActionResult.Ok(new JsonObject
        {
            ["recipients"] = sent,
            ["unknown"] = unknown.Count == 0 ? null : new JsonArray([.. unknown.Select(u => JsonValue.Create(u))]),
        });
    }
}

/// <summary><c>workflow.start</c>: starts a workflow of the workspace on the item (<c>workflow</c>: its name).</summary>
internal sealed class WorkflowStartAction(WorkflowStarter starter) : IAutomationAction
{
    public string Key => "workflow.start";

    public string Description => "Starts a workflow on the item: { \"workflow\": \"Invoice approval\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) => Inputs.Required(inputs, "workflow");

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return AutomationActionResult.Fail("The trigger has no item.");
        }

        var (run, error) = await starter.StartAsync(item, Inputs.Text(context.Inputs, "workflow")!, context.UserId, cancellationToken);
        return run is null ? AutomationActionResult.Fail(error!) : AutomationActionResult.Ok(new JsonObject { ["runId"] = run.Id.ToString() });
    }
}

/// <summary>Runs one action with the token context of a rule or workflow.</summary>
internal sealed class ActionExecutor(ActionCatalog catalog, TokenExpander tokens, IListItemStore items, IServiceProvider services)
{
    public async Task<AutomationActionResult> ExecuteAsync(
        ActionDefinition action, Guid workspaceId, AutomationItem? item, Guid? actor, JsonObject? data,
        IReadOnlyDictionary<string, string> outcomes, string source, string executionKey, CancellationToken ct)
    {
        if (catalog.Find(action.Type) is not { } found)
        {
            return AutomationActionResult.Fail($"Unknown action '{action.Type}'.");
        }

        TokenScope? scope = null;
        async Task<string> ExpandAsync(string template, CancellationToken token)
        {
            if (scope is null)
            {
                var store = items.AsSystem();
                var current = item is null ? null : await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, token);
                var list = item is null ? null : await store.GetListAsync(item.WorkspaceId, item.ListId, token);
                scope = new TokenScope(current, list?.Name, outcomes, data);
            }

            return await tokens.ExpandAsync(template, scope, token);
        }

        try
        {
            return await found.ExecuteAsync(new AutomationActionContext
            {
                WorkspaceId = workspaceId,
                Item = item,
                Inputs = (action.Inputs ?? []).DeepClone().AsObject(),
                Services = services,
                UserId = actor,
                Data = data,
                Source = source,
                ExecutionKey = executionKey,
                ExpandAsync = ExpandAsync,
            }, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return AutomationActionResult.Fail(ex.Message);
        }
    }
}
