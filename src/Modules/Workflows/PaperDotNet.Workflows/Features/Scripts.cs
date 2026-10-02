using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Microsoft.Extensions.Options;
using PaperDotNet.Lists.Contracts;
using JintJsonParser = Jint.Native.Json.JsonParser;
using JintJsonSerializer = Jint.Native.Json.JsonSerializer;

namespace PaperDotNet.Workflows.Features;

/// <summary>Limits of script steps (<c>Workflows:Scripts</c>, ADR-0037).</summary>
public sealed class WorkflowScriptOptions
{
    public const string Section = "Workflows:Scripts";

    /// <summary>Most characters of a script.</summary>
    public const int MaxCodeLength = 50_000;

    public int TimeoutMilliseconds { get; set; } = 2000;

    public int MemoryMegabytes { get; set; } = 32;

    public int MaxStatements { get; set; } = 1_000_000;

    public int MaxRecursion { get; set; } = 100;

    /// <summary>Most <c>items.get</c> and <c>items.query</c> calls per run of the step.</summary>
    public int MaxReads { get; set; } = 200;

    /// <summary>Most planned writes per run of the step.</summary>
    public int MaxWrites { get; set; } = 1000;
}

/// <summary>What a script run gave: its result, the variables, the planned writes and its log lines; or an error.</summary>
internal sealed record ScriptOutcome(JsonNode? Result, JsonObject Variables, JsonArray Plan, IReadOnlyList<string> Log, string? Error);

/// <summary>
/// Runs the JavaScript of a <c>script</c> step (ADR-0037) with Jint: strict mode, limits on time, memory, statements and
/// recursion, and no access to .NET. The script sees JSON copies of the run's item, variables, node outputs and trigger
/// data, and the workspace's lists through <c>items</c>. Reads happen right away; <c>create</c>, <c>update</c> and
/// <c>delete</c> only add to a plan, which the interpreter saves with the run and then applies.
/// </summary>
internal sealed partial class ScriptRunner(IListItemStore items, IOptions<WorkflowScriptOptions> options)
{
    // The script is the body of an async function on the first line, so `return` and `await` work and line numbers stay
    // the same. The same contract runs in Node on the SDK (@paperdotnet/client, runWorkflowScript).
    private const string Prefix = "(async function () {";
    private const string Suffix = "\n})()";
    private const string Source = "script";

    // The items API: async functions over the host's, so scripts `await` them as they do with the SDK in Node.
    private const string Prelude = """
        const items = Object.freeze({
          get: async (list, id) => __items_get(list, id),
          query: async (list, options) => __items_query(list, options === undefined ? null : options),
          create: async (list, fields) => __items_write('create', list, null, fields),
          update: async (list, id, fields) => __items_write('update', list, id, fields),
          delete: async (list, id) => __items_write('delete', list, id, null),
          related: async (id, options) => __items_related(id, options === undefined ? null : options),
          relate: async (id, otherId, type, attributes) => __items_graph('relate', id, otherId, type === undefined ? null : type, attributes === undefined ? null : attributes, null),
          relationships: async (options) => __items_relationships(options === undefined ? null : options),
          updateRelationship: async (id, relationshipId, attributes, version) => __items_graph('updateRelationship', id, relationshipId, null, attributes, version),
          unrelate: async (id, relationshipId) => __items_graph('unrelate', id, relationshipId, null, null, null),
          deleteById: async (id) => __items_graph('deleteGlobal', id, null, null, null, null),
        });
        const log = (text) => __log(text);
        """;

    /// <summary>The code of a step: <c>code</c> as a string or an array of lines.</summary>
    public static string? Code(JsonObject inputs) => inputs["code"] switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray lines => string.Join('\n', lines.Select(l => l is JsonValue v && v.TryGetValue<string>(out var line) ? line : string.Empty)),
        _ => null,
    };

    /// <summary>Why the code cannot run (length or syntax, with its line), or null.</summary>
    public static string? Check(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "code is required (a string, or an array of lines).";
        }

        if (code.Length > WorkflowScriptOptions.MaxCodeLength)
        {
            return $"code has at most {WorkflowScriptOptions.MaxCodeLength} characters.";
        }

        try
        {
            Engine.PrepareScript(Prefix + code + Suffix, strict: true);
            return null;
        }
        catch (ScriptPreparationException ex)
        {
            return $"code: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    public ScriptOutcome Run(
        string code, Guid workspaceId, Guid executionId, ListItemData? item, string? itemList, JsonObject outputs, JsonObject variables,
        JsonObject? data, CancellationToken ct)
    {
        var limits = options.Value;
        var store = items.AsSystem();
        var lists = new Dictionary<string, ListData?>(StringComparer.Ordinal);
        var plan = new JsonArray();
        var log = new List<string>();
        var reads = 0;

        var engine = new Engine(o => o
            .Strict()
            .TimeoutInterval(TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds))
            .LimitMemory(limits.MemoryMegabytes * 1024L * 1024L)
            .MaxStatements(limits.MaxStatements)
            .LimitRecursion(limits.MaxRecursion)
            .CancellationToken(ct));
        var parser = new JintJsonParser(engine);
        JsValue ToJs(JsonNode? node) => node is null ? JsValue.Null : parser.Parse(node.ToJsonString());
        JsonNode? FromJs(JsValue value)
        {
            var json = new JintJsonSerializer(engine).Serialize(value);
            return json.IsString() ? JsonNode.Parse(json.AsString()) : null;
        }

        // A failed items call fails the step even when the script does not await it or catches it: the plan would be
        // incomplete.
        string? failure = null;
        JavaScriptException Error(string message)
        {
            failure ??= message;
            return new(engine.Intrinsics.Error, message);
        }

        ListData List(JsValue name)
        {
            var listName = name.IsString() ? name.AsString() : throw Error("The list must be given by name.");
            if (!lists.TryGetValue(listName, out var list))
            {
                list = store.GetListsAsync(workspaceId, null, ct).GetAwaiter().GetResult().FirstOrDefault(l => l.Name == listName);
                lists[listName] = list;
            }

            return list ?? throw Error($"The list '{listName}' does not exist in the workspace.");
        }

        Guid Id(JsValue id) => id.IsString() && Guid.TryParse(id.AsString(), out var value) ? value : throw Error("id must be the id of an item.");

        void Read()
        {
            if (++reads > limits.MaxReads)
            {
                throw Error($"A script reads at most {limits.MaxReads} times.");
            }
        }

        engine.SetValue("item", item is null ? JsValue.Null : ToJs(ItemJson(item, itemList)));
        engine.SetValue("vars", ToJs(variables));
        engine.SetValue("steps", ToJs(outputs));
        engine.SetValue("trigger", ToJs(data ?? []));
        engine.SetValue("__items_get", new Func<JsValue, JsValue, JsValue>((list, id) =>
        {
            Read();
            var target = List(list);
            var found = store.GetAsync(workspaceId, target.Id, Id(id), ct).GetAwaiter().GetResult();
            return found is null ? JsValue.Null : ToJs(ItemJson(found, target.Name));
        }));
        engine.SetValue("__items_query", new Func<JsValue, JsValue, JsValue>((list, options) =>
        {
            Read();
            var target = List(list);
            var query = options.IsNull() ? new JsonObject() : FromJs(options) as JsonObject ?? throw Error("The query options must be an object.");
            string? Text(string name) => query[name] switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                _ => throw Error($"{name} must be a string."),
            };
            var top = query["top"] switch
            {
                null => 100,
                JsonValue value when value.TryGetValue<double>(out var number) => Math.Clamp((int)number, 1, ListItemQuery.MaxTop),
                _ => throw Error("top must be a number."),
            };
            var (found, error) = store.QueryAsync(workspaceId, target.Id, new ListItemQuery(Text("filter"), Text("orderBy"), top), ct)
                .GetAwaiter().GetResult();
            return error is not null ? throw Error(error) : ToJs(new JsonArray([.. found.Select(i => (JsonNode)ItemJson(i, target.Name))]));
        }));
        engine.SetValue("__items_write", new Func<JsValue, JsValue, JsValue, JsValue, JsValue>((kind, list, id, fields) =>
        {
            if (plan.Count >= limits.MaxWrites)
            {
                throw Error($"A script writes at most {limits.MaxWrites} items.");
            }

            var op = kind.AsString();
            var target = List(list);
            var itemId = op == "create" ? CreatedId(executionId, plan.Count) : Id(id);
            var values = op == "delete" ? null : FromJs(fields) as JsonObject ?? throw Error("fields must be an object.");
            plan.Add(new JsonObject
            {
                ["op"] = op,
                ["list"] = target.Name,
                ["listId"] = target.Id.ToString(),
                ["id"] = itemId.ToString(),
                ["fields"] = values,
            });
            return op == "create" ? itemId.ToString() : JsValue.Undefined;
        }));
        engine.SetValue("__items_related", new Func<JsValue, JsValue, JsValue>((id, queryOptions) =>
        {
            Read();
            var query = queryOptions.IsNull() ? new JsonObject() : FromJs(queryOptions) as JsonObject ?? throw Error("The relationship options must be an object.");
            string? Text(string name) => query[name] switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                _ => throw Error($"{name} must be a string.")
            };
            var direction = Text("direction");
            if (direction is not (null or "both" or "incoming" or "outgoing")) throw Error("direction must be both, incoming or outgoing.");
            Guid? after = null;
            if (Text("cursor") is { } cursor)
            {
                if (!PaperDotNet.Api.PageRequest.TryDecodeCursor(cursor, out var decoded)) throw Error("The relationship cursor is invalid.");
                after = decoded;
            }
            var top = query["top"] switch { null => 100, JsonValue value when value.TryGetValue<double>(out var n) => Math.Clamp((int)n, 1, 500), _ => throw Error("top must be a number.") };
            var page = store.GetRelationshipsAsync(Id(id), Text("type"), direction, top, after, ct).GetAwaiter().GetResult();
            if (page is null) throw Error("The item was not found.");
            return ToJs(new JsonObject
            {
                ["value"] = new JsonArray([.. page.Items.Select(edge => (JsonNode)new JsonObject {
                ["id"] = edge.Id.ToString(), ["sourceId"] = edge.SourceItemId.ToString(), ["targetId"] = edge.TargetItemId.ToString(),
                ["directed"] = edge.Directed, ["type"] = edge.Type?.Name, ["attributes"] = edge.Attributes.DeepClone(), ["version"] = edge.Version,
                ["item"] = ItemJson(edge.RelatedItem, store.GetListAsync(edge.RelatedItem.WorkspaceId, edge.RelatedItem.ListId, ct).GetAwaiter().GetResult()?.Name)
            })]),
                ["nextCursor"] = page.NextCursor
            });
        }));
        engine.SetValue("__items_relationships", new Func<JsValue, JsValue>(options =>
        {
            Read();
            var q = options.IsNull() ? new JsonObject() : FromJs(options) as JsonObject ?? throw Error("relationship options must be an object.");
            string? Text(string name) => q[name] is null ? null : q[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw Error($"{name} must be a string.");
            Guid? after = null;
            if (Text("cursor") is { } cursor)
            {
                if (!PaperDotNet.Api.PageRequest.TryDecodeCursor(cursor, out var parsed)) throw Error("The relationship cursor is invalid.");
                after = parsed;
            }
            bool? directed = q["directed"] is null ? null : q["directed"] is JsonValue flag && flag.TryGetValue<bool>(out var direction) ? direction : throw Error("directed must be a boolean.");
            var top = q["top"] is null ? 100 : q["top"] is JsonValue count && count.TryGetValue<double>(out var number) ? (int)Math.Clamp(number, 1, 500) : throw Error("top must be a number.");
            WorkspaceRelationshipPage? page;
            try { page = store.QueryRelationshipsAsync(workspaceId, Text("type"), directed, Text("filter"), top, after, ct).GetAwaiter().GetResult(); }
            catch (ArgumentException ex) { throw Error(ex.Message); }
            if (page is null) throw Error("The workspace was not found.");
            JsonObject Located(ListItemData data) => ItemJson(data, store.GetListAsync(data.WorkspaceId, data.ListId, ct).GetAwaiter().GetResult()?.Name);
            return ToJs(new JsonObject
            {
                ["value"] = new JsonArray([.. page.Items.Select(edge => (JsonNode)new JsonObject {
                ["id"] = edge.Id.ToString(), ["directed"] = edge.Directed, ["type"] = edge.Type?.Name,
                ["attributes"] = edge.Attributes.DeepClone(), ["version"] = edge.Version,
                ["sourceItem"] = Located(edge.SourceItem), ["targetItem"] = Located(edge.TargetItem)
            })]),
                ["nextCursor"] = page.NextCursor
            });
        }));
        engine.SetValue("__items_graph", new Func<JsValue, JsValue, JsValue, JsValue, JsValue, JsValue, JsValue>((kind, id, other, type, attributes, version) =>
        {
            if (plan.Count >= limits.MaxWrites) throw Error($"A script writes at most {limits.MaxWrites} items.");
            if (!type.IsNull() && !type.IsString()) throw Error("type must be a string.");
            var op = kind.AsString();
            var bag = attributes.IsNull() ? null : FromJs(attributes) as JsonObject ?? throw Error("attributes must be an object.");
            if (op == "updateRelationship" && bag is null) throw Error("attributes must be an object.");
            if (op == "updateRelationship" && (!version.IsNumber() || version.AsNumber() < 1 || version.AsNumber() > uint.MaxValue || version.AsNumber() != Math.Truncate(version.AsNumber())))
                throw Error("version must be a positive relationship version.");
            plan.Add(new JsonObject
            {
                ["op"] = op,
                ["list"] = "global items",
                ["listId"] = Guid.Empty.ToString(),
                ["id"] = Id(id).ToString(),
                ["fields"] = new JsonObject { ["otherId"] = other.IsNull() ? null : Id(other).ToString(), ["type"] = type.IsNull() ? null : type.AsString(), ["attributes"] = bag, ["version"] = version.IsNull() ? null : (uint)version.AsNumber() }
            });
            return JsValue.Undefined;
        }));
        engine.SetValue("__log", new Action<JsValue>(text => log.Add(text.IsString() ? text.AsString() : text.ToString())));

        try
        {
            engine.Execute(Prelude);
            var result = engine.Evaluate(Prefix + code + Suffix, Source).UnwrapIfPromise(ct);
            if (failure is not null)
            {
                return Failed(failure);
            }

            var vars = FromJs(engine.GetValue("vars")) as JsonObject ?? [];
            return new ScriptOutcome(result.IsUndefined() ? null : FromJs(result), vars, plan, log, null);
        }
        catch (PromiseRejectedException ex)
        {
            return Failed(Describe(ex.RejectedValue));
        }
        catch (JavaScriptException ex)
        {
            return Failed($"{ex.Message} (line {ex.Location.Start.Line})");
        }
        catch (Exception ex) when (ex is TimeoutException or StatementsCountOverflowException or MemoryLimitExceededException
            or RecursionDepthOverflowException or ExecutionCanceledException)
        {
            return Failed(ex switch
            {
                TimeoutException => $"The script ran longer than {limits.TimeoutMilliseconds} ms.",
                StatementsCountOverflowException => $"The script ran more than {limits.MaxStatements} statements.",
                MemoryLimitExceededException => $"The script used more than {limits.MemoryMegabytes} MB.",
                RecursionDepthOverflowException => $"The script's calls went deeper than {limits.MaxRecursion}.",
                _ => ex.Message,
            });
        }

        ScriptOutcome Failed(string error) => new(null, variables, [], log, error);
    }

    /// <summary>A thrown value as an error message: an error's message and the script line it came from.</summary>
    private static string Describe(JsValue error)
    {
        if (error is not ObjectInstance thrown || !thrown.HasProperty("message"))
        {
            return error.ToString();
        }

        var message = thrown.Get("message").ToString();
        var stack = thrown.Get("stack");
        var line = stack.IsString() ? ScriptLine().Match(stack.AsString()) : Match.Empty;
        return line.Success ? $"{message} (line {line.Groups[1].Value})" : message;
    }

    [GeneratedRegex($@"\b{Source}:(\d+):")]
    private static partial Regex ScriptLine();

    /// <summary>An item as scripts see it: its fields, <c>id</c> and <c>list</c> (the list's name).</summary>
    private static JsonObject ItemJson(ListItemData item, string? list)
    {
        var json = item.Fields.DeepClone().AsObject();
        json["id"] = item.Id.ToString();
        json["list"] = list;
        return json;
    }

    /// <summary>The id of the <paramref name="index"/>th planned write's new item: derived from the execution (a name-based UUID, version 8).</summary>
    public static Guid CreatedId(Guid executionId, int index)
    {
        Span<byte> input = stackalloc byte[20];
        executionId.TryWriteBytes(input);
        BitConverter.TryWriteBytes(input[16..], index);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
