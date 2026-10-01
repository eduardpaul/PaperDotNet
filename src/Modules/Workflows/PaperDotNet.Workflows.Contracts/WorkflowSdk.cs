using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>Reading the inputs of an activity (<see cref="WorkflowActivityContext.Inputs"/>).</summary>
public static class ActivityInputs
{
    /// <summary>A non-empty text input, or null.</summary>
    public static string? Text(JsonObject inputs, string name) =>
        inputs?[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A list of texts (a single text counts as a list of one), or null.</summary>
    public static List<string>? Texts(JsonObject inputs, string name) => inputs?[name] switch
    {
        JsonArray array => [.. array.Select(e => e is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).OfType<string>()],
        JsonValue value when value.TryGetValue<string>(out var single) => [single],
        _ => null,
    };

    /// <summary>A number input, or null.</summary>
    public static double? Number(JsonObject inputs, string name) =>
        inputs?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>Errors for the text inputs that are missing (for <see cref="IWorkflowActivity.Validate"/>).</summary>
    public static IEnumerable<string> Required(JsonObject inputs, params string[] names) =>
        names.Where(n => Text(inputs, n) is null).Select(n => $"{n} is required.");
}

/// <summary>Building the JSON Schemas of activity inputs and outputs (<see cref="IWorkflowActivity.InputSchema"/>).</summary>
public static class ActivitySchemas
{
    public static JsonObject Of(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0)
        {
            result["required"] = new JsonArray([.. required.Select(r => (JsonNode)JsonValue.Create(r))]);
        }

        return result;
    }

    public static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Texts(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    public static JsonObject Values(string description) => new() { ["type"] = "object", ["description"] = description };

    public static JsonObject Any(string description) => new() { ["description"] = description };

    /// <summary>A list of people as <see cref="IWorkflowRecipients"/> reads them.</summary>
    public static JsonObject People(string description) => Texts(description + " User names, group:Name, field:name, creator or actor.");
}

/// <summary>What the engine knows about workflows, for activities that work across runs (e.g. a batch).</summary>
public interface IWorkflowDirectory
{
    /// <summary>Whether an enabled workflow of the workspace uses the activity (e.g. whether something answers a kind of wait).</summary>
    Task<bool> IsActivityUsedAsync(Guid tenantId, Guid workspaceId, string activityKey, CancellationToken cancellationToken);

    /// <summary>Whether the run is still running or waiting.</summary>
    Task<bool> IsRunActiveAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken);
}

/// <summary>People resolved from an activity's input: users, and the entries that named nobody.</summary>
public sealed record WorkflowRecipients(IReadOnlyList<Guid> Users, IReadOnlyList<string> Unknown);

/// <summary>
/// Resolves people inputs of activities the way the built-in ones do: user names, <c>group:Name</c> (its members, groups
/// inside groups included), <c>field:name</c> (person values of the run's item), <c>creator</c> (of the item) and
/// <c>actor</c> (the user who started the run or whose change triggered it).
/// </summary>
public interface IWorkflowRecipients
{
    Task<WorkflowRecipients> ResolveAsync(IEnumerable<string> people, WorkflowActivityContext context, CancellationToken cancellationToken);
}
