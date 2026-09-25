using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.Mcp.Contracts;

/// <summary>
/// A tool for AI assistants (API-08/09), served by the MCP endpoint <c>/v1.0/mcp</c>. Tools run as
/// the calling user: use the SDK services (e.g. <c>IListItemStore</c>), which check permissions.
/// Modules register tools as scoped <see cref="IMcpTool"/> services; extensions use
/// <c>IExtensionBuilder.AddMcpTool</c> (names start with <c>{extension id}.</c>).
/// </summary>
public interface IMcpTool
{
    /// <summary>Unique name: letters, digits, <c>_</c>, <c>-</c> and <c>.</c> (max 64).</summary>
    string Name { get; }

    /// <summary>What the tool does and when to use it (the assistant reads this).</summary>
    string Description { get; }

    /// <summary>JSON schema of the arguments (an object schema); see <see cref="McpSchema"/>.</summary>
    JsonElement InputSchema { get; }

    /// <summary>A permission scope the caller needs (e.g. <c>list.read</c>), or null.</summary>
    string? RequiredScope { get; }

    /// <summary>True when the tool only reads data.</summary>
    bool IsReadOnly { get; }

    /// <summary>Whether the tool is offered to the current tenant (extensions: when enabled).</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken);
}

/// <summary>The result of a tool call: text for the assistant, optionally structured data.</summary>
public sealed record McpToolResult(string Text, bool IsError = false, JsonNode? Structured = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Returns <paramref name="value"/> as JSON (text and structured content).</summary>
    public static McpToolResult FromJson(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, Json);
        return new McpToolResult(node?.ToJsonString(Json) ?? "null", false, node is JsonObject ? node : new JsonObject { ["value"] = node });
    }

    /// <summary>A failure the assistant can react to (e.g. "not found", invalid arguments).</summary>
    public static McpToolResult Error(string message) => new(message, true);
}

/// <summary>Thrown by <see cref="McpArguments"/> for missing or invalid arguments; reported to the assistant.</summary>
public sealed class McpArgumentException(string message) : Exception(message);

/// <summary>Arguments of a tool call with typed accessors.</summary>
public sealed class McpArguments(IReadOnlyDictionary<string, JsonElement> values)
{
    public IReadOnlyDictionary<string, JsonElement> Values { get; } = values;

    public bool Has(string name) => Values.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public string? GetString(string name) => Has(name)
        ? Values[name].ValueKind == JsonValueKind.String ? Values[name].GetString() : throw Invalid(name, "a string")
        : null;

    public string GetRequiredString(string name) => GetString(name) is { Length: > 0 } value ? value : throw Missing(name);

    public Guid? GetGuid(string name) => GetString(name) is { } value
        ? System.Guid.TryParse(value, out var id) ? id : throw Invalid(name, "an id (GUID)")
        : null;

    public Guid GetRequiredGuid(string name) => GetGuid(name) ?? throw Missing(name);

    public int? GetInt32(string name) => Has(name)
        ? Values[name].ValueKind == JsonValueKind.Number && Values[name].TryGetInt32(out var number) ? number
            : Values[name].ValueKind == JsonValueKind.String && int.TryParse(Values[name].GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number
            : throw Invalid(name, "a whole number")
        : null;

    public JsonObject? GetObject(string name) => Has(name)
        ? Values[name].ValueKind == JsonValueKind.Object ? JsonNode.Parse(Values[name].GetRawText())!.AsObject() : throw Invalid(name, "an object")
        : null;

    public JsonObject GetRequiredObject(string name) => GetObject(name) ?? throw Missing(name);

    private static McpArgumentException Missing(string name) => new($"The argument '{name}' is required.");

    private static McpArgumentException Invalid(string name, string expected) => new($"The argument '{name}' must be {expected}.");
}

/// <summary>Builds simple JSON schemas for tool arguments.</summary>
public static class McpSchema
{
    /// <summary>An object schema; <paramref name="properties"/> are (name, type, description, required).</summary>
    public static JsonElement ObjectSchema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, type, description, _) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        var required = properties.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required);
        }

        return JsonSerializer.SerializeToElement(schema);
    }
}
