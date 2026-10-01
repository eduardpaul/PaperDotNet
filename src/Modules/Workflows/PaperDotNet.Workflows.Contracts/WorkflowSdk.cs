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
