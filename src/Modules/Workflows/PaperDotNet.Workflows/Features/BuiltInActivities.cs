using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

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
        var items = context.Services.GetRequiredService<IListItemStore>();
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
        var items = context.Services.GetRequiredService<IListItemStore>();
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
