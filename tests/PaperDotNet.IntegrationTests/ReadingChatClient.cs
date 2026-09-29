using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// A deterministic stand-in for a chat model that "reads" the input:
/// <list type="bullet">
/// <item>extraction (a schema with <c>values</c>): each field's value from a <c>field: value</c> line of the input;</item>
/// <item>classification (a schema with <c>term</c>): the first allowed term the input mentions;</item>
/// <item>other schemas: every property as its name;</item>
/// <item>summaries: the input's first words; anything else: <c>Echo: </c> and the first line of the question.</item>
/// </list>
/// </summary>
internal sealed partial class ReadingChatClient : IChatClient
{
    public static readonly ReadingChatClient Instance = new();

    private int _calls;

    /// <summary>Calls answered so far (cached answers never get here).</summary>
    public int Calls => Volatile.Read(ref _calls);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var list = messages.ToList();
        var system = list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
        var input = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var answer = options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema }
            ? Structured(JsonNode.Parse(schema.GetRawText())!.AsObject(), input).ToJsonString()
            : system.StartsWith("Summarize", StringComparison.Ordinal)
                ? string.Join(' ', input.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(6))
                : "Echo: " + input.Split('\n')[0];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
        {
            Usage = new UsageDetails { InputTokenCount = (system.Length + input.Length) / 4, OutputTokenCount = answer.Length / 4 },
        });
    }

    private static JsonObject Structured(JsonObject schema, string input)
    {
        var properties = schema["properties"]!.AsObject();
        if (properties["values"]?["properties"] is JsonObject fields)
        {
            var values = new JsonObject();
            var confidence = new JsonObject();
            foreach (var (name, field) in fields)
            {
                var match = Regex.Match(input, $@"\b{Regex.Escape(name)}\s*:\s*([^\n]+)", RegexOptions.IgnoreCase);
                var type = field?["type"]?.AsArray().First()?.GetValue<string>();
                JsonNode? value = !match.Success ? null
                    : type == "number" ? JsonValue.Create(decimal.Parse(NumberPart().Match(match.Groups[1].Value).Value, CultureInfo.InvariantCulture))
                    : JsonValue.Create(match.Groups[1].Value.Trim());
                values[name] = value;
                confidence[name] = value is null ? 0 : 0.95;
            }

            return new JsonObject { ["values"] = values, ["confidence"] = confidence };
        }

        if (properties["term"]?["enum"] is JsonArray terms)
        {
            var term = terms.OfType<JsonValue>().Select(t => t.GetValue<string>())
                .FirstOrDefault(t => input.Contains(t, StringComparison.OrdinalIgnoreCase));
            return new JsonObject { ["term"] = term, ["confidence"] = term is null ? 0 : 0.9 };
        }

        var result = new JsonObject();
        foreach (var (name, _) in properties)
        {
            result[name] = name;
        }

        return result;
    }

    [GeneratedRegex(@"-?\d+(\.\d+)?")]
    private static partial Regex NumberPart();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("test", null, "reading-model") : null;

    public void Dispose()
    {
    }
}
