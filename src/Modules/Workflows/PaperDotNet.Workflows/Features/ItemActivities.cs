using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>The item, or the item <c>id</c> in <c>list</c> (by name), that an item activity works on.</summary>
internal static class ItemTarget
{
    /// <summary>The list and item an activity names with <c>list</c> and <c>id</c> (tokens allowed), or why there is none.</summary>
    public static async Task<(ListData? List, Guid ItemId, string? Error)> ResolveAsync(WorkflowActivityContext context, WorkflowItems items, CancellationToken cancellationToken)
    {
        var listName = await context.ExpandAsync(DefinitionValidator.Text(context.Inputs, "list") ?? "", cancellationToken);
        var id = await context.ExpandAsync(DefinitionValidator.Text(context.Inputs, "id") ?? "", cancellationToken);
        if (await items.FindListByNameAsync(context.Actor, context.WorkspaceId, listName, cancellationToken) is not { } list)
        {
            return (null, Guid.Empty, $"The list '{listName}' does not exist in the workspace.");
        }

        return Guid.TryParse(id, out var itemId) ? (list, itemId, null) : (null, Guid.Empty, $"'{id}' is not the id of an item.");
    }

    /// <summary>An item as flows see it: its values, <c>id</c> and <c>list</c> (the list's name).</summary>
    public static JsonObject Json(ListItemData item, string list)
    {
        var json = item.Fields.DeepClone().AsObject();
        json["id"] = item.Id.ToString();
        json["list"] = list;
        return json;
    }
}

/// <summary><c>item.get</c>: reads the item <c>id</c> of <c>list</c>. Output: the item's values with <c>id</c> and <c>list</c>.</summary>
internal sealed class ItemGetActivity : IWorkflowActivity
{
    public string Key => "item.get";

    public string Description => "Reads an item of the workspace (list, id): its values are the step's output.";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "list", "id");

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var items = context.Services.GetRequiredService<WorkflowItems>();
        var (list, itemId, error) = await ItemTarget.ResolveAsync(context, items, cancellationToken);
        if (list is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        return await items.GetAsync(context.Actor, context.WorkspaceId, list.Id, itemId, cancellationToken) is { } item
            ? WorkflowActivityResult.Ok(ItemTarget.Json(item, list.Name))
            : WorkflowActivityResult.Fail($"No item '{itemId}' in the list '{list.Name}'.");
    }
}

/// <summary>
/// <c>items.query</c>: items of <c>list</c> matching <c>filter</c> (OData, tokens allowed) in <c>orderBy</c> order, at most
/// <c>top</c> (default 100). Output: <c>items</c> and <c>count</c>.
/// </summary>
internal sealed class ItemsQueryActivity : IWorkflowActivity
{
    public string Key => "items.query";

    public string Description => "Finds items of a list (list, filter, orderBy, top): output items and count.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        ActivityInputs.Required(inputs, "list")
            .Concat(ActivityInputs.Number(inputs, "top") is { } top && (top < 1 || top > ListItemQuery.MaxTop) ? [$"top must be 1 to {ListItemQuery.MaxTop}."] : []);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var items = context.Services.GetRequiredService<WorkflowItems>();
        var listName = await context.ExpandAsync(DefinitionValidator.Text(context.Inputs, "list")!, cancellationToken);
        if (await items.FindListByNameAsync(context.Actor, context.WorkspaceId, listName, cancellationToken) is not { } list)
        {
            return WorkflowActivityResult.Fail($"The list '{listName}' does not exist in the workspace.");
        }

        var filter = DefinitionValidator.Text(context.Inputs, "filter") is { } template ? await context.ExpandAsync(template, cancellationToken) : null;
        var top = (int)(ActivityInputs.Number(context.Inputs, "top") ?? 100);
        var (found, error) = await items.QueryAsync(context.Actor, context.WorkspaceId, list.Id, filter, DefinitionValidator.Text(context.Inputs, "orderBy"), top, null, cancellationToken);
        if (error is not null)
        {
            return WorkflowActivityResult.Fail(error);
        }

        var array = new JsonArray();
        foreach (var item in found)
        {
            array.Add((JsonNode)ItemTarget.Json(item, list.Name));
        }

        return WorkflowActivityResult.Ok(new JsonObject { ["items"] = array, ["count"] = found.Count });
    }
}

/// <summary><c>item.delete</c>: moves the item <c>id</c> of <c>list</c> to the recycle bin; an item already gone is not an error.</summary>
internal sealed class ItemDeleteActivity : IWorkflowActivity
{
    public string Key => "item.delete";

    public string Description => "Moves an item to the recycle bin (list, id).";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "list", "id");

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var items = context.Services.GetRequiredService<WorkflowItems>();
        var (list, itemId, error) = await ItemTarget.ResolveAsync(context, items, cancellationToken);
        if (list is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        var result = await items.DeleteAsync(context.Actor, context.WorkspaceId, list.Id, itemId, cancellationToken);
        return result.Succeeded || result.Status == ListItemStatus.NotFound
            ? WorkflowActivityResult.Ok(new JsonObject { ["id"] = itemId.ToString() })
            : WorkflowActivityResult.Fail(result.Describe());
    }
}

/// <summary>
/// <c>item.file</c> (DOC-14, path templates): moves the run's item into <c>folder</c> (e.g. <c>Finance/{created:yyyy}/{counterparty}</c>),
/// creating missing folders, and optionally renames it (<c>title</c>). Each folder level is expanded on its own, so values
/// cannot add levels; characters that are not allowed in names are replaced.
/// </summary>
internal sealed class ItemFileActivity : IWorkflowActivity
{
    private static readonly char[] Invalid = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];
    private const int MaxSegment = 120;

    public string Key => "item.file";

    public string Description => "Files the run's item into a folder path and renames it from its values (folder, title).";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        DefinitionValidator.Text(inputs, "folder") is null && DefinitionValidator.Text(inputs, "title") is null ? ["folder or title is required."] : [];

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.ListId is not { } listId || context.ItemId is not { } itemId)
        {
            return WorkflowActivityResult.Fail("The run has no item.");
        }

        var items = context.Services.GetRequiredService<WorkflowItems>();
        var output = new JsonObject();
        if (DefinitionValidator.Text(context.Inputs, "folder") is { } folder)
        {
            var path = new List<string>();
            foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path.Add(Clean(await context.ExpandAsync(segment, cancellationToken)));
            }

            var (folderId, problem) = await items.EnsureFolderAsync(context.Actor, context.WorkspaceId, listId, path, cancellationToken);
            if (problem is not null)
            {
                return WorkflowActivityResult.Fail(problem.Describe());
            }

            var moved = await items.MoveAsync(context.Actor, context.WorkspaceId, listId, itemId, folderId, cancellationToken);
            if (!moved.Succeeded)
            {
                return WorkflowActivityResult.Fail(moved.Describe());
            }

            output["folder"] = string.Join('/', path);
        }

        if (DefinitionValidator.Text(context.Inputs, "title") is { } title && (await context.ExpandAsync(title, cancellationToken)).Trim() is { Length: > 0 } name)
        {
            var renamed = await items.UpdateAsync(context.Actor, context.WorkspaceId, listId, itemId, new JsonObject { ["title"] = name }, cancellationToken);
            if (!renamed.Succeeded)
            {
                return WorkflowActivityResult.Fail(renamed.Describe());
            }

            output["title"] = name;
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
