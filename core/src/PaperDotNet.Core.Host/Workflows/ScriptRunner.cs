using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Microsoft.Extensions.Options;
using PaperDotNet.Core.Api;
using JintJsonParser = Jint.Native.Json.JsonParser;
using JintJsonSerializer = Jint.Native.Json.JsonSerializer;

namespace PaperDotNet.Core.Host.Workflows;

/// <summary>Limits of script steps (<c>Workflows:Scripts</c>, ADR-0037).</summary>
public sealed class WorkflowScriptOptions
{
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
/// Runs the JavaScript of a <c>script</c> node (ADR-0037) with Jint: strict mode, limits on time, memory, statements and
/// recursion, and no access to .NET. The script sees JSON copies of the run's item, variables, node outputs and trigger
/// data, and the tenant's lists through <c>items</c>. Reads happen right away; <c>create</c>, <c>update</c> and
/// <c>delete</c> only add to a plan, which the interpreter saves with the run and then applies.
/// <para>
/// Native AOT (ADR-0039): Jint's interpreter runs under AOT. Host functions are <see cref="ClrFunction"/>s over
/// <see cref="JsValue"/>s and values cross as JSON, so none of Jint's reflection-based .NET interop is used.
/// </para>
/// </summary>
public sealed partial class ScriptRunner(Lists.ListItemService items, Lists.IItemQueries queries, IOptions<WorkflowScriptOptions> options)
{
    // The script is the body of an async function on the first line, so `return` and `await` work and line numbers stay.
    private const string Prefix = "(async function () {";
    private const string Suffix = "\n})()";
    private const string Source = "script";

    private const string Prelude = """
        const items = Object.freeze({
          get: async (list, id) => __items_get(list, id),
          query: async (list, options) => __items_query(list, options === undefined ? null : options),
          create: async (list, fields) => __items_write('create', list, null, fields),
          update: async (list, id, fields) => __items_write('update', list, id, fields),
          delete: async (list, id) => __items_write('delete', list, id, null),
        });
        const log = (text) => __log(text);
        """;

    /// <summary>The code of a node: <c>code</c> as a string or an array of lines.</summary>
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

    internal ScriptOutcome Run(string code, Guid tenantId, Guid executionId, ScopeItem? item, string? itemList, JsonObject outputs, JsonObject variables, JsonObject? data, CancellationToken ct)
    {
        var limits = options.Value;
        var lists = new Dictionary<string, Lists.ListDefinition?>(StringComparer.Ordinal);
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

        // A failed items call fails the step even when the script does not await or catches it: the plan would be incomplete.
        string? failure = null;
        JavaScriptException Error(string message)
        {
            failure ??= message;
            return new(engine.Intrinsics.Error, message);
        }

        Lists.ListDefinition List(JsValue name)
        {
            var listName = name.IsString() ? name.AsString() : throw Error("The list must be given by name.");
            if (!lists.TryGetValue(listName, out var list))
            {
                list = items.FindListByNameAsync(tenantId, listName, ct).GetAwaiter().GetResult();
                lists[listName] = list;
            }

            return list ?? throw Error($"The list '{listName}' does not exist.");
        }

        Guid Id(JsValue id) => id.IsString() && Guid.TryParse(id.AsString(), out var value) ? value : throw Error("id must be the id of an item.");

        void Read()
        {
            if (++reads > limits.MaxReads)
            {
                throw Error($"A script reads at most {limits.MaxReads} times.");
            }
        }

        static JsValue Arg(JsValue[] arguments, int index) => index < arguments.Length ? arguments[index] : JsValue.Undefined;
        void Function(string name, Func<JsValue[], JsValue> body) => engine.SetValue(name, new ClrFunction(engine, name, (_, arguments) => body(arguments)));

        engine.SetValue("item", item is null ? JsValue.Null : ToJs(ItemJson(item.Id, item.Fields, itemList)));
        engine.SetValue("vars", ToJs(variables));
        engine.SetValue("steps", ToJs(outputs));
        engine.SetValue("trigger", ToJs(data ?? []));
        Function("__items_get", arguments =>
        {
            Read();
            var target = List(Arg(arguments, 0));
            var found = items.FindAsync(tenantId, target.Id, Id(Arg(arguments, 1)), ct).GetAwaiter().GetResult();
            return found is null ? JsValue.Null : ToJs(ItemJson(found.Id, Lists.FieldValues.ForApi(Lists.ListEndpoints.FieldsOf(target), found.Title, found.Fields), target.Name));
        });
        Function("__items_query", arguments =>
        {
            Read();
            var target = List(Arg(arguments, 0));
            var query = Arg(arguments, 1).IsNull() ? [] : FromJs(Arg(arguments, 1)) as JsonObject ?? throw Error("The query options must be an object.");
            string? Text(string name) => query[name] switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                _ => throw Error($"{name} must be a string."),
            };
            var top = query["top"] switch
            {
                null => 100,
                JsonValue value when value.TryGetValue<double>(out var number) => Math.Clamp((int)number, 1, PageRequest.MaxTop),
                _ => throw Error("top must be a number."),
            };
            var fields = Lists.ListEndpoints.FieldsOf(target);
            var (filter, orderBy, error) = Lists.ItemQueryParser.Parse(Text("filter"), Text("orderBy"), fields);
            if (error is not null)
            {
                throw Error(error);
            }

            try
            {
                var found = queries.QueryAsync(new Lists.ItemQuery(tenantId, target.Id, filter, orderBy, new PageRequest(top, null, 0), false), ct).GetAwaiter().GetResult();
                return ToJs(new JsonArray([.. found.Items.Take(top).Select(i => (JsonNode)ItemJson(i.Id, Lists.FieldValues.ForApi(fields, i.Title, i.Fields), target.Name))]));
            }
            catch (NotSupportedException ex)
            {
                throw Error(ex.Message);
            }
        });
        Function("__items_write", arguments =>
        {
            if (plan.Count >= limits.MaxWrites)
            {
                throw Error($"A script writes at most {limits.MaxWrites} items.");
            }

            var op = Arg(arguments, 0).AsString();
            var target = List(Arg(arguments, 1));
            var itemId = op == "create" ? CreatedId(executionId, plan.Count) : Id(Arg(arguments, 2));
            var values = op == "delete" ? null : FromJs(Arg(arguments, 3)) as JsonObject ?? throw Error("fields must be an object.");
            plan.Add(item: new JsonObject
            {
                ["op"] = op,
                ["list"] = target.Name,
                ["listId"] = target.Id.ToString(),
                ["id"] = itemId.ToString(),
                ["fields"] = values,
            });
            return op == "create" ? itemId.ToString() : JsValue.Undefined;
        });
        Function("__log", arguments =>
        {
            var text = Arg(arguments, 0);
            log.Add(text.IsString() ? text.AsString() : text.ToString());
            return JsValue.Undefined;
        });

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
            return Failed(failure ?? Describe(ex.RejectedValue));
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

    /// <summary>An item as scripts see it: its values, <c>id</c> and <c>list</c> (the list's name).</summary>
    private static JsonObject ItemJson(Guid id, JsonObject fields, string? list)
    {
        var json = fields.DeepClone().AsObject();
        json["id"] = id.ToString();
        json["list"] = list;
        return json;
    }

    /// <summary>The id of the <paramref name="index"/>th planned write's new item: derived from the execution (a name-based UUID, version 8).</summary>
    public static Guid CreatedId(Guid executionId, int index) => DerivedId(executionId, index);

    /// <summary>A name-based UUID (version 8) of an id and a number, the same every time.</summary>
    public static Guid DerivedId(Guid id, int number)
    {
        Span<byte> input = stackalloc byte[20];
        id.TryWriteBytes(input);
        BitConverter.TryWriteBytes(input[16..], number);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
