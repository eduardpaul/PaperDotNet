using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>Reusable workflow parts for reactions to saved item changes.</summary>
public static class ItemChangeWorkflows
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A visible, initially enabled process using immutable item-event context.</summary>
    public static BuiltInWorkflow Create(string key, string name, string description, string activity, params string[] triggers) =>
        new(key, name, description, new JsonObject
        {
            ["scope"] = "workspace",
            ["triggers"] = new JsonArray([.. triggers.Select(t => (JsonNode)new JsonObject { ["type"] = t })]),
            ["flow"] = new JsonObject
            {
                ["start"] = "process",
                ["nodes"] = new JsonObject { ["process"] = new JsonObject { ["activity"] = activity } },
            },
        }) { EnabledByDefault = true };

    /// <summary>Reads the original event, without querying mutable item values to reconstruct it.</summary>
    public static ItemEvent? ReadEvent(JsonObject? data) => data?["eventType"]?.GetValue<string>() switch
    {
        WorkflowTriggers.ItemAdded => data["event"]?.Deserialize<ItemAdded>(Json),
        WorkflowTriggers.ItemUpdated => data["event"]?.Deserialize<ItemUpdated>(Json),
        WorkflowTriggers.ItemDeleted => data["event"]?.Deserialize<ItemDeleted>(Json),
        WorkflowTriggers.ItemRestored => data["event"]?.Deserialize<ItemRestored>(Json),
        _ => null,
    };
}

/// <summary>An activity for an item event; manual execution without an event is rejected explicitly.</summary>
public abstract class ItemChangeActivity : IWorkflowActivity
{
    public abstract string Key { get; }

    public abstract string Description { get; }

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.ItemChange is not { } change)
        {
            return WorkflowActivityResult.Fail("This activity requires an item-change trigger with its original event.");
        }

        await ExecuteAsync(change, cancellationToken);
        return WorkflowActivityResult.Ok(new JsonObject { ["eventId"] = change.EventId.ToString() });
    }

    protected abstract Task ExecuteAsync(ItemEvent change, CancellationToken cancellationToken);
}
