using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>Reusable workflow parts for reactions to saved item changes.</summary>
public static class ItemChangeWorkflows
{
    /// <summary>Where the engine keeps the item event in a run's execution context; trigger data cannot set it.</summary>
    public const string ContextKey = "itemEvent";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A required system workflow (<see cref="BuiltInWorkflow.Required"/>) with one node running <paramref name="activity"/>
    /// on the original item event. <paramref name="contentType"/> (name or key) keeps the triggers from starting runs for
    /// items of other content types, so a reaction for notes or tasks costs nothing on other lists.
    /// </summary>
    public static BuiltInWorkflow Create(
        string key, string name, string description, string activity, IReadOnlyList<string> triggers, string? contentType = null, bool includeFolders = false) =>
        new(key, name, description, new JsonObject
        {
            ["scope"] = "workspace",
            ["triggers"] = new JsonArray([.. triggers.Select(t => (JsonNode)Trigger(t, contentType))]),
            ["flow"] = new JsonObject
            {
                ["start"] = "process",
                ["nodes"] = new JsonObject { ["process"] = new JsonObject { ["activity"] = activity } },
            },
        })
        { EnabledByDefault = true, Required = true, IncludeFolders = includeFolders };

    private static JsonObject Trigger(string type, string? contentType)
    {
        var trigger = new JsonObject { ["type"] = type };
        if (contentType is not null)
        {
            trigger["contentType"] = contentType;
        }

        return trigger;
    }

    /// <summary>
    /// The item event that started the run, from the engine-owned execution context (<c>trigger</c> must be an item
    /// trigger; the snapshots come from the trigger data, stored once). Data raised by workflows cannot forge it.
    /// </summary>
    public static ItemEvent? ReadEvent(JsonObject? executionContext)
    {
        if (executionContext?[ContextKey] is not JsonObject json)
        {
            return null;
        }

        ItemEvent? change = executionContext["trigger"]?.GetValue<string>() switch
        {
            WorkflowTriggers.ItemAdded => json.Deserialize<ItemAdded>(Json),
            WorkflowTriggers.ItemUpdated => json.Deserialize<ItemUpdated>(Json),
            WorkflowTriggers.ItemDeleted => json.Deserialize<ItemDeleted>(Json),
            WorkflowTriggers.ItemRestored => json.Deserialize<ItemRestored>(Json),
            _ => null,
        };
        var data = executionContext["data"] as JsonObject;
        return change is null ? null : change with
        {
            Before = data?["before"]?.Deserialize<ItemSnapshot>(Json) ?? change.Before,
            After = data?["after"]?.Deserialize<ItemSnapshot>(Json) ?? change.After,
        };
    }
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
