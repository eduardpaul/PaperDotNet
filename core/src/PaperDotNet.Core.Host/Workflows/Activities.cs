using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace PaperDotNet.Core.Host.Workflows;

/// <summary>Outcome of an activity: done with an output (and optionally another outcome port than <c>done</c>), or failed.</summary>
public sealed record WorkflowActivityResult(bool Succeeded, string? Error = null, JsonObject? Output = null, string? Outcome = null)
{
    public static WorkflowActivityResult Ok(JsonObject? output = null) => new(true, Output: output);

    public static WorkflowActivityResult Fail(string error) => new(false, error);
}

/// <summary>What an activity runs with.</summary>
public sealed class WorkflowActivityContext
{
    public required Guid TenantId { get; init; }

    public required Guid RunId { get; init; }

    /// <summary>The run's item, if any.</summary>
    public Guid? ListId { get; init; }

    public Guid? ItemId { get; init; }

    /// <summary>The inputs as saved (tokens not expanded).</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>
    /// A stable id of this execution (the same when the node runs again after a crash): use it as the id of what the
    /// activity creates, so a repeat creates nothing.
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>The author of the changes the activity makes: the organization, one level deeper than the run.</summary>
    public required Lists.ItemActor Actor { get; init; }

    public required IServiceProvider Services { get; init; }

    /// <summary>Expands tokens in a text (<see cref="TokenExpander"/>).</summary>
    public required Func<string, CancellationToken, Task<string>> ExpandAsync { get; init; }

    /// <summary>An input as JSON: a text that is exactly one token gives its value with its type; other text is expanded.</summary>
    public required Func<string, CancellationToken, Task<JsonNode?>> ResolveAsync { get; init; }

    /// <summary>Resolves every text value of an object (nested objects and arrays included) with <see cref="ResolveAsync"/>.</summary>
    public async Task<JsonObject> ResolveObjectAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            result[name] = await ResolveNodeAsync(value, cancellationToken);
        }

        return result;
    }

    private async Task<JsonNode?> ResolveNodeAsync(JsonNode? value, CancellationToken cancellationToken) => value switch
    {
        JsonValue text when text.TryGetValue<string>(out var template) => await ResolveAsync(template, cancellationToken),
        JsonObject obj => await ResolveObjectAsync(obj, cancellationToken),
        JsonArray array => new JsonArray([.. await Task.WhenAll(array.Select(e => ResolveNodeAsync(e, cancellationToken)))]),
        _ => value?.DeepClone(),
    };
}

/// <summary>
/// An action workflow nodes can run (<c>item.update</c>, …), registered with
/// <see cref="WorkflowServiceCollectionExtensions.AddWorkflowActivity{T}"/>. It must be safe to run again with the same
/// <see cref="WorkflowActivityContext.ExecutionId"/> (after a crash).
/// </summary>
public interface IWorkflowActivity
{
    string Key { get; }

    string Description { get; }

    /// <summary>Checks the inputs when a workflow is saved.</summary>
    IEnumerable<string> Validate(JsonObject inputs) => [];

    /// <summary>Outcome ports besides <c>done</c> and <c>error</c>.</summary>
    IReadOnlyList<string> Outcomes => [];

    Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken);
}

public static class WorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowActivity<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services)
        where T : class, IWorkflowActivity
    {
        services.AddSingleton<IWorkflowActivity, T>();
        return services;
    }
}

/// <summary>
/// <c>item.create</c>: creates an item in <c>list</c> (by name) with <c>fields</c> (texts may be tokens; a text that is
/// exactly one token keeps the value's type). The new item's id is the execution id, so a repeat creates nothing.
/// Output: <c>id</c>.
/// </summary>
internal sealed class ItemCreateActivity : IWorkflowActivity
{
    public string Key => "item.create";

    public string Description => "Creates an item in a list (list, fields).";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        (DefinitionValidator.Text(inputs, "list") is null ? ["list is required."] : Array.Empty<string>())
            .Concat(inputs["fields"] is JsonObject ? [] : ["fields must be an object."]);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var items = context.Services.GetRequiredService<Lists.ListItemService>();
        var listName = await context.ExpandAsync(DefinitionValidator.Text(context.Inputs, "list")!, cancellationToken);
        if (await items.FindListByNameAsync(context.TenantId, listName, cancellationToken) is not { } list)
        {
            return WorkflowActivityResult.Fail($"The list '{listName}' does not exist.");
        }

        var fields = await context.ResolveObjectAsync((JsonObject)context.Inputs["fields"]!, cancellationToken);
        var result = await items.CreateAsync(context.Actor, list.Id, context.ExecutionId, fields, cancellationToken);
        return result.Succeeded
            ? WorkflowActivityResult.Ok(new JsonObject { ["id"] = result.Item!.Id.ToString() })
            : WorkflowActivityResult.Fail(result.Describe());
    }
}

/// <summary>
/// <c>item.update</c>: changes <c>fields</c> of the run's item, or of the item <c>id</c> in <c>list</c> (tokens as in
/// <c>item.create</c>; <c>null</c> removes a value). Setting the same values again is harmless.
/// </summary>
internal sealed class ItemUpdateActivity : IWorkflowActivity
{
    public string Key => "item.update";

    public string Description => "Changes values of the run's item, or of the item id in list (fields).";

    public IEnumerable<string> Validate(JsonObject inputs) => inputs["fields"] is JsonObject ? [] : ["fields must be an object."];

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var items = context.Services.GetRequiredService<Lists.ListItemService>();
        Guid listId, itemId;
        if (DefinitionValidator.Text(context.Inputs, "list") is { } listTemplate)
        {
            var listName = await context.ExpandAsync(listTemplate, cancellationToken);
            var id = await context.ExpandAsync(DefinitionValidator.Text(context.Inputs, "id") ?? "", cancellationToken);
            if (await items.FindListByNameAsync(context.TenantId, listName, cancellationToken) is not { } list || !Guid.TryParse(id, out itemId))
            {
                return WorkflowActivityResult.Fail($"No item '{id}' in the list '{listName}'.");
            }

            listId = list.Id;
        }
        else if (context.ListId is { } runList && context.ItemId is { } runItem)
        {
            (listId, itemId) = (runList, runItem);
        }
        else
        {
            return WorkflowActivityResult.Fail("The run has no item: give list and id.");
        }

        var fields = await context.ResolveObjectAsync((JsonObject)context.Inputs["fields"]!, cancellationToken);
        var result = await items.UpdateAsync(context.Actor, listId, itemId, fields, null, cancellationToken);
        return result.Succeeded ? WorkflowActivityResult.Ok(new JsonObject { ["id"] = itemId.ToString() }) : WorkflowActivityResult.Fail(result.Describe());
    }
}
