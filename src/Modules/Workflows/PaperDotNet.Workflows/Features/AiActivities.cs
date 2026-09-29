using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Limits of AI activities, from the same section as the chat model (<c>AI:Chat</c>).</summary>
public sealed class WorkflowAiOptions
{
    public const string Section = "AI:Chat";

    /// <summary>The model's name, recorded with each call and part of the cache key.</summary>
    public string? Model { get; set; }

    /// <summary>Most tokens (input and output) an organization uses per UTC day; 0 for no limit.</summary>
    public long DailyTokens { get; set; }

    /// <summary>Most characters of an item's text sent to the model (longer text is cut).</summary>
    public int MaxInputCharacters { get; set; } = 24_000;

    /// <summary>How long an answer is reused for the same model and input (days; 0 turns the cache off).</summary>
    public int CacheDays { get; set; } = 30;
}

/// <summary>A question to the chat model: instructions, the input, and optionally a JSON Schema the answer must follow.</summary>
internal sealed record AiQuestion(string Activity, string Instructions, string Input, JsonObject? Schema = null);

/// <summary>The model's answer (text, or JSON when a schema was given).</summary>
internal sealed record AiAnswer(string Text, bool Cached, long Tokens)
{
    /// <summary>The answer as a JSON object (code fences some models add are removed), or null.</summary>
    public JsonObject? Json
    {
        get
        {
            var text = Text.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                text = text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..].TrimEnd('`', '\n', '\r', ' ');
            }

            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// All AI activities call the chat model through here (AI-06, ADR-0036): the same model and input reuse an earlier answer
/// (the cache), an organization's daily token budget is enforced, and every call is recorded (<see cref="AiCall"/>: a hash
/// of what was sent, the model, tokens and the answer). Records are saved with the run's next step, so they are atomic
/// with its progress. With <c>execution: batch</c> (AI-08) a question is queued for the organization's next batch
/// (<see cref="AiBatchRequest"/>) and the run waits; its node runs again with the answer.
/// </summary>
internal sealed class AiGateway(
    IServiceProvider services, WorkflowsDbContext db, IOptions<WorkflowAiOptions> options, IOptions<AiBatchOptions> batchOptions, TimeProvider time)
{
    private readonly IChatClient? _client = services.GetService<IChatClient>();

    public const string NotConfigured = "AI is not configured on this server (AI:Chat).";

    /// <summary>The model's name: configured, else the client's default.</summary>
    public string Model => options.Value.Model ?? _client?.GetService<ChatClientMetadata>()?.DefaultModelId ?? "default";

    /// <summary>
    /// Asks the question: an answer, an error, or (batch execution) the wait the activity returns; the activity runs again
    /// when the answer is there, with the same execution id, and gets it from here.
    /// </summary>
    public async Task<(AiAnswer? Answer, string? Error, WorkflowActivityResult? Wait)> AskAsync(AiQuestion question, WorkflowActivityContext context, CancellationToken ct)
    {
        if (_client is null)
        {
            return (null, NotConfigured, null);
        }

        var model = Model;
        var hash = Hash(model, question);
        var now = time.GetUtcNow();
        AiCall Record(string? response, bool cached) => db.AiCalls.Add(NewCall(question.Activity, context.Source, context.RunId, model, hash, response, cached, now)).Entity;

        var execution = Inputs.Text(context.Inputs, AiActivity.Execution) ?? batchOptions.Value.Execution;
        if (execution == AiActivity.Batch && context.RunId is { } runId)
        {
            var suffix = ":" + context.ExecutionId.ToString("N");
            var waiting = await db.Bookmarks.AsNoTracking()
                .Where(b => b.RunId == runId && b.Kind == AiBatchRequests.WaitKind && b.Key.EndsWith(suffix))
                .Select(b => b.Key).FirstOrDefaultAsync(ct);
            var request = waiting is not null && Guid.TryParseExact(waiting[..32], "N", out var requestId)
                ? await db.AiBatchRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
                : null;
            var immediately = Inputs.Text(context.Inputs, AiActivity.OnDeadline) != AiActivity.Fail;
            if (request is not null)
            {
                // The node runs again after its wait: the answer, or what to do at the deadline.
                if (request is { Status: AiBatchRequestStatus.Completed, Response: { } response })
                {
                    Record(response, cached: true);
                    return (new AiAnswer(response, false, 0), null, null);
                }

                var late = now >= request.DeadlineAt;
                if (!late && request.Status is AiBatchRequestStatus.Queued or AiBatchRequestStatus.Submitted)
                {
                    return (null, null, AiBatchRequests.Wait(request, context.ExecutionId));
                }

                if (!late || !immediately)
                {
                    return (null, request.Error ?? AiBatchRequests.MissedDeadline, null);
                }
            }
            else if (await CachedAsync(hash, now, ct) is { } cachedAnswer)
            {
                Record(cachedAnswer, cached: true);
                return (new AiAnswer(cachedAnswer, true, 0), null, null);
            }
            else
            {
                // A pending request for the same question is shared (one per question: a concurrent second one conflicts,
                // and the retry joins); joining conflicts with a concurrent answer (the version), so the run is never left
                // waiting for an answer that was already given.
                var pending = await db.AiBatchRequests.FirstOrDefaultAsync(r => r.PendingHash == hash, ct);
                if (pending is not null && now >= pending.DeadlineAt)
                {
                    // Pending past its deadline (not expired yet): as at the deadline.
                    if (!immediately)
                    {
                        return (null, AiBatchRequests.MissedDeadline, null);
                    }
                }
                else
                {
                    pending ??= NewRequest();
                    pending.Waiters++;
                    return (null, null, AiBatchRequests.Wait(pending, context.ExecutionId));
                }

                AiBatchRequest NewRequest()
                {
                    var hours = Inputs.Number(context.Inputs, AiActivity.DeadlineHours) ?? batchOptions.Value.DeadlineHours;
                    var created = new AiBatchRequest
                    {
                        Id = Ids.New(),
                        Activity = question.Activity,
                        Source = Truncate(context.Source, 300),
                        Model = Truncate(model, 200),
                        InputHash = hash,
                        PendingHash = hash,
                        Question = JsonSerializer.Serialize(question),
                        Status = AiBatchRequestStatus.Queued,
                        CreatedAt = now,
                        DeadlineAt = now.AddHours(Math.Clamp(hours, 1, 24 * 14)),
                    };
                    db.AiBatchRequests.Add(created);
                    return created;
                }
            }
        }
        else if (await CachedAsync(hash, now, ct) is { } cachedAnswer)
        {
            Record(cachedAnswer, cached: true);
            return (new AiAnswer(cachedAnswer, true, 0), null, null);
        }

        var (call, error) = await CallAsync(question, model, hash, context.Source, context.RunId, ct);
        return call?.Response is { } text ? (new AiAnswer(text, false, call.InputTokens + call.OutputTokens), null, null) : (null, error, null);
    }

    /// <summary>
    /// Calls the model now (within the day's budget) and records the call (added to the context, not saved): the record,
    /// or null and why when the budget is used up; a failed call is recorded with its error.
    /// </summary>
    public async Task<(AiCall? Call, string? Error)> CallAsync(AiQuestion question, string model, string hash, string source, Guid? runId, CancellationToken ct)
    {
        if (_client is null)
        {
            return (null, NotConfigured);
        }

        if (await BudgetProblemAsync(ct) is { } problem)
        {
            return (null, problem);
        }

        var chat = new ChatOptions();
        if (question.Schema is { } format)
        {
            chat.ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(format), question.Activity.Replace('.', '_'));
        }

        var now = time.GetUtcNow();
        ChatResponse response;
        try
        {
            response = await _client.GetResponseAsync([new(ChatRole.System, question.Instructions), new(ChatRole.User, question.Input)], chat, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = NewCall(question.Activity, source, runId, model, hash, null, false, now);
            failed.Error = Truncate(ex.Message, 2000);
            db.AiCalls.Add(failed);
            return (failed, $"The AI model failed: {ex.Message}");
        }

        var call = NewCall(question.Activity, source, runId, model, hash, response.Text, false, now);
        call.InputTokens = response.Usage?.InputTokenCount ?? 0;
        call.OutputTokens = response.Usage?.OutputTokenCount ?? 0;
        db.AiCalls.Add(call);
        return (call, null);
    }

    /// <summary>Why the organization cannot use more tokens today, or null.</summary>
    public async Task<string?> BudgetProblemAsync(CancellationToken ct)
    {
        var limit = options.Value.DailyTokens;
        if (limit <= 0)
        {
            return null;
        }

        var today = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var used = await db.AiCalls.Where(c => c.CreatedAt >= today).SumAsync(c => c.InputTokens + c.OutputTokens, ct);
        return used >= limit ? $"The organization's AI budget for today ({limit} tokens) is used up." : null;
    }

    public static string Hash(string model, AiQuestion question) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', model, question.Instructions, question.Input, question.Schema?.ToJsonString() ?? string.Empty))));

    public static AiCall NewCall(string activity, string source, Guid? runId, string model, string hash, string? response, bool cached, DateTimeOffset at) => new()
    {
        Id = Ids.New(),
        Activity = activity,
        Source = Truncate(source, 300),
        RunId = runId,
        Model = Truncate(model, 200),
        InputHash = hash,
        Cached = cached,
        Response = response,
        CreatedAt = at,
    };

    public static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    private async Task<string?> CachedAsync(string hash, DateTimeOffset now, CancellationToken ct)
    {
        if (options.Value.CacheDays <= 0)
        {
            return null;
        }

        var since = now.AddDays(-options.Value.CacheDays);
        return await db.AiCalls.AsNoTracking()
            .Where(c => c.InputHash == hash && c.Response != null && c.CreatedAt >= since)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.Response)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// What the model reads about an item: its title and values (<c>name: value</c> lines), then the text others contribute
    /// to search (e.g. a document's pages), cut to <see cref="WorkflowAiOptions.MaxInputCharacters"/>.
    /// </summary>
    public async Task<string> ItemTextAsync(WorkflowItem item, CancellationToken ct)
    {
        var store = services.GetRequiredService<IListItemStore>().AsSystem();
        var data = await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
        var text = new StringBuilder();
        foreach (var (name, value) in data?.Fields ?? [])
        {
            var shown = value switch
            {
                null => null,
                JsonArray array => string.Join(", ", array.Select(v => v?.ToString())),
                _ => value.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(shown))
            {
                text.Append(name).Append(": ").AppendLine(shown);
            }
        }

        foreach (var contributor in services.GetServices<IItemSearchContributor>())
        {
            if ((await contributor.GetContentAsync([item.ItemId], ct)).TryGetValue(item.ItemId, out var content) && content.Text.Length > 0)
            {
                text.AppendLine().AppendLine(content.Text);
            }
        }

        var max = Math.Max(1000, options.Value.MaxInputCharacters);
        return text.Length > max ? text.ToString(0, max) : text.ToString();
    }
}

/// <summary>Shared parts of AI activities: modes, confidence and schemas of list fields.</summary>
internal static class AiActivity
{
    public const string Apply = "apply";
    public const string Suggest = "suggest";
    public const string LowConfidence = "lowConfidence";
    public const string Execution = "execution";
    public const string Immediate = "immediate";
    public const string Batch = "batch";
    public const string DeadlineHours = "deadlineHours";
    public const string OnDeadline = "onDeadline";
    public const string Fail = "fail";

    /// <summary>The inputs of an AI activity with those every AI activity has (batch execution).</summary>
    public static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties) => Schemas.Object(required,
    [
        .. properties,
        (Execution, Schemas.Text("immediate (call the model now) or batch (queue for the organization's next batch: cheaper, "
            + "the run waits); default from AI:Batch:Execution.")),
        (DeadlineHours, Schemas.Number("With batch: the longest wait in hours (default from AI:Batch:DeadlineHours).")),
        (OnDeadline, Schemas.Text("With batch, when the deadline passes without an answer: immediate (default: call the model now) or fail.")),
    ]);

    /// <summary>Checks the inputs every AI activity has.</summary>
    public static IEnumerable<string> ValidateExecution(JsonObject inputs)
    {
        if (Inputs.Text(inputs, Execution) is { } execution && execution is not (Immediate or Batch))
        {
            yield return "execution must be immediate or batch.";
        }

        if (inputs[DeadlineHours] is not null && Inputs.Number(inputs, DeadlineHours) is not (>= 1 and <= 336))
        {
            yield return "deadlineHours must be from 1 to 336.";
        }

        if (Inputs.Text(inputs, OnDeadline) is { } onDeadline && onDeadline is not (Immediate or Fail))
        {
            yield return "onDeadline must be immediate or fail.";
        }
    }

    /// <summary>Field types the model can fill, with their JSON Schema.</summary>
    public static JsonObject? FieldSchema(ListFieldInfo field)
    {
        static JsonObject Nullable(string type, string? format = null)
        {
            var schema = new JsonObject { ["type"] = new JsonArray(type, "null") };
            if (format is not null)
            {
                schema["description"] = format;
            }

            return schema;
        }

        var schema = field.Type switch
        {
            "text" or "note" or "email" or "url" => Nullable("string"),
            "number" or "currency" => Nullable("number"),
            "boolean" => Nullable("boolean"),
            "date" => Nullable("string", "A date as yyyy-MM-dd."),
            "dateTime" => Nullable("string", "An ISO 8601 date and time with offset."),
            "choice" => new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = new JsonArray([.. (field.Choices ?? []).Select(c => JsonValue.Create(c)), null]) },
            _ => null,
        };
        if (schema is not null && field.AllowMultiple)
        {
            return null;
        }

        return schema;
    }

    public static IEnumerable<string> ValidateCommon(JsonObject inputs)
    {
        foreach (var problem in ValidateExecution(inputs))
        {
            yield return problem;
        }

        if (Inputs.Text(inputs, "mode") is { } mode && mode is not (Apply or Suggest))
        {
            yield return "mode must be apply or suggest.";
        }

        if (inputs["minConfidence"] is not null && Inputs.Number(inputs, "minConfidence") is not (>= 0 and <= 1))
        {
            yield return "minConfidence must be a number from 0 to 1.";
        }
    }

    public static bool Applies(JsonObject inputs) => Inputs.Text(inputs, "mode") != Suggest;

    public static double MinConfidence(JsonObject inputs) => Inputs.Number(inputs, "minConfidence") ?? 0.7;

    public static double Confidence(JsonNode? value) =>
        value is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number.GetValue<double>() : 0;
}

/// <summary>
/// <c>ai.extract</c> (AI-03, AI-07): fills chosen fields of the item from its text (e.g. a receipt's store, date and total)
/// with a structured-output call. Values with at least <c>minConfidence</c> are applied (<c>mode: suggest</c> only
/// returns them); when a requested field stays empty or uncertain the outcome is <c>lowConfidence</c>.
/// </summary>
internal sealed class AiExtractActivity(AiGateway ai, IListItemStore items) : IWorkflowActivity
{
    public string Key => "ai.extract";

    public string Description => "Fills fields of the item from its text with AI: { \"fields\": [\"store\", \"total\"], \"mode\": \"apply\", \"minConfidence\": 0.8 }.";

    public IReadOnlyList<string> Outcomes => [AiActivity.LowConfidence];

    public JsonObject? InputSchema => AiActivity.Schema([],
        ("fields", Schemas.Texts("Fields to fill (text, number, currency, date, choice, …); default: all that can be filled.")),
        ("instructions", Schemas.Text("Extra instructions (template), e.g. what the document is.")),
        ("mode", Schemas.Text("apply (default: set the values) or suggest (only return them).")),
        ("minConfidence", Schemas.Number("Values with less confidence (0 to 1) are not applied; default 0.7.")));

    public JsonObject? OutputSchema => Schemas.Object([],
        ("values", Schemas.Values("The values found, by field.")), ("confidence", Schemas.Values("Confidence per field (0 to 1).")),
        ("applied", Schemas.Texts("Fields that were set.")), ("uncertain", Schemas.Texts("Fields left empty or uncertain.")));

    public IEnumerable<string> Validate(JsonObject inputs) => AiActivity.ValidateCommon(inputs);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("ai.extract needs an item.");
        }

        var store = items.AsSystem();
        var data = await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, cancellationToken);
        var list = await store.DescribeListAsync(item.WorkspaceId, item.ListId, cancellationToken);
        var fields = list?.ContentTypes.FirstOrDefault(c => c.Id == data?.ContentTypeId)?.Fields ?? [];
        var wanted = Inputs.Texts(context.Inputs, "fields");
        var chosen = new List<(ListFieldInfo Field, JsonObject Schema)>();
        foreach (var field in fields.Where(f => f.Name != "title" && (wanted is null || wanted.Contains(f.Name))))
        {
            if (AiActivity.FieldSchema(field) is { } schema)
            {
                chosen.Add((field, schema));
            }
            else if (wanted is not null)
            {
                return WorkflowActivityResult.Fail($"The field '{field.Name}' ({field.Type}) cannot be filled by AI.");
            }
        }

        if (wanted?.FirstOrDefault(name => fields.All(f => f.Name != name)) is { } unknown)
        {
            return WorkflowActivityResult.Fail($"The item has no field '{unknown}'.");
        }

        if (chosen.Count == 0)
        {
            return WorkflowActivityResult.Fail("The item has no fields AI can fill.");
        }

        var values = new JsonObject();
        var confidences = new JsonObject();
        foreach (var (field, schema) in chosen)
        {
            values[field.Name] = schema;
            confidences[field.Name] = new JsonObject { ["type"] = "number" };
        }

        var names = chosen.Select(c => c.Field.Name).ToArray();
        var format = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["values"] = new JsonObject { ["type"] = "object", ["properties"] = values, ["required"] = new JsonArray([.. names.Select(n => JsonValue.Create(n))]), ["additionalProperties"] = false },
                ["confidence"] = new JsonObject { ["type"] = "object", ["properties"] = confidences, ["required"] = new JsonArray([.. names.Select(n => JsonValue.Create(n))]), ["additionalProperties"] = false },
            },
            ["required"] = new JsonArray("values", "confidence"),
            ["additionalProperties"] = false,
        };
        var described = string.Join('\n', chosen.Select(c =>
            $"- {c.Field.Name} ({c.Field.DisplayName}, {c.Field.Type}{(c.Field.Choices is { Count: > 0 } choices ? ": " + string.Join(" | ", choices) : string.Empty)})"
            + (c.Field.Description is { Length: > 0 } description ? $": {description}" : string.Empty)));
        var instructions = "Extract these fields from the document. Answer with JSON: \"values\" by field (null when the document does not say) "
            + "and \"confidence\" by field from 0 to 1. Dates as yyyy-MM-dd, numbers without currency symbols.\n" + described
            + (Inputs.Text(context.Inputs, "instructions") is { } extra ? "\n" + await context.ExpandAsync(extra, cancellationToken) : string.Empty);
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, instructions, await ai.ItemTextAsync(item, cancellationToken), format), context, cancellationToken);

        if (wait is not null)
        {
            return wait;
        }
        if (answer?.Json is not { } json)
        {
            return WorkflowActivityResult.Fail(error ?? "The AI answer was not the expected JSON.");
        }

        var found = json["values"] as JsonObject ?? [];
        var confidence = json["confidence"] as JsonObject ?? [];
        var min = AiActivity.MinConfidence(context.Inputs);
        var apply = new JsonObject();
        var uncertain = new JsonArray();
        foreach (var name in names)
        {
            if (found[name] is { } value && AiActivity.Confidence(confidence[name]) >= min)
            {
                apply[name] = value.DeepClone();
            }
            else
            {
                uncertain.Add(name);
            }
        }

        var applied = new JsonArray();
        if (AiActivity.Applies(context.Inputs) && apply.Count > 0)
        {
            var result = await store.UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, apply, null, cancellationToken);
            if (!result.Succeeded)
            {
                return WorkflowActivityResult.Fail(ItemUpdateAction.Describe(result));
            }

            foreach (var (name, _) in apply)
            {
                applied.Add(name);
            }
        }

        var output = new JsonObject
        {
            ["values"] = found.DeepClone(),
            ["confidence"] = confidence.DeepClone(),
            ["applied"] = applied,
            ["uncertain"] = uncertain,
            ["cached"] = answer.Cached,
        };
        return uncertain.Count > 0 ? WorkflowActivityResult.Ok(AiActivity.LowConfidence, output) : WorkflowActivityResult.Ok(output);
    }
}

/// <summary>
/// <c>ai.classify</c> (AI-02): picks the term of a term set (<c>termSet</c>: <c>Group/Set</c>) that fits the item best and,
/// with <c>field</c>, sets it there (<c>mode: suggest</c> only returns it). No term, or less than <c>minConfidence</c>,
/// gives the outcome <c>lowConfidence</c>.
/// </summary>
internal sealed class AiClassifyActivity(AiGateway ai, IListItemStore items, ITermStore terms) : IWorkflowActivity
{
    public string Key => "ai.classify";

    public string Description => "Classifies the item with a term of a term set: { \"termSet\": \"Documents/Types\", \"field\": \"type\" }.";

    public IReadOnlyList<string> Outcomes => [AiActivity.LowConfidence];

    public JsonObject? InputSchema => AiActivity.Schema(["termSet"],
        ("termSet", Schemas.Text("The term set as Group/Set.")),
        ("field", Schemas.Text("A managed metadata field of the item to set (optional).")),
        ("instructions", Schemas.Text("Extra instructions (template).")),
        ("mode", Schemas.Text("apply (default) or suggest.")),
        ("minConfidence", Schemas.Number("Less confidence (0 to 1) is not applied; default 0.7.")));

    public JsonObject? OutputSchema => Schemas.Object([],
        ("term", Schemas.Text("The term's name, or null.")), ("termId", Schemas.Text("The term's id.")),
        ("confidence", Schemas.Number("0 to 1.")), ("applied", Schemas.Any("Whether the field was set.")));

    public IEnumerable<string> Validate(JsonObject inputs) =>
        AiActivity.ValidateCommon(inputs).Concat(Inputs.Text(inputs, "termSet") is { } path && path.Split('/').Length == 2 ? [] : ["termSet must be Group/Set."]);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("ai.classify needs an item.");
        }

        var path = Inputs.Text(context.Inputs, "termSet")!.Split('/');
        if (await terms.FindTermSetAsync(path[0], path[1], cancellationToken) is not { } setId)
        {
            return WorkflowActivityResult.Fail($"The term set '{string.Join('/', path)}' does not exist.");
        }

        var candidates = await terms.ListTermsAsync(setId, cancellationToken);
        if (candidates.Count == 0)
        {
            return WorkflowActivityResult.Fail("The term set has no terms.");
        }

        var format = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["term"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = new JsonArray([.. candidates.Select(t => JsonValue.Create(t.Name)).Distinct(), null]) },
                ["confidence"] = new JsonObject { ["type"] = "number" },
            },
            ["required"] = new JsonArray("term", "confidence"),
            ["additionalProperties"] = false,
        };
        var instructions = "Classify the document with exactly one of these terms, or null when none fits. Answer with JSON: \"term\" and "
            + "\"confidence\" from 0 to 1.\nTerms: " + string.Join(", ", candidates.Select(t => t.Name).Distinct())
            + (Inputs.Text(context.Inputs, "instructions") is { } extra ? "\n" + await context.ExpandAsync(extra, cancellationToken) : string.Empty);
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, instructions, await ai.ItemTextAsync(item, cancellationToken), format), context, cancellationToken);

        if (wait is not null)
        {
            return wait;
        }
        if (answer?.Json is not { } json)
        {
            return WorkflowActivityResult.Fail(error ?? "The AI answer was not the expected JSON.");
        }

        var name = json["term"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var term = candidates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        var confidence = AiActivity.Confidence(json["confidence"]);
        var sure = term is not null && confidence >= AiActivity.MinConfidence(context.Inputs);
        var applied = false;
        if (sure && AiActivity.Applies(context.Inputs) && Inputs.Text(context.Inputs, "field") is { } fieldName)
        {
            var store = items.AsSystem();
            var list = await store.DescribeListAsync(item.WorkspaceId, item.ListId, cancellationToken);
            var field = list?.ContentTypes.SelectMany(c => c.Fields).FirstOrDefault(f => f.Name == fieldName);
            if (field is null)
            {
                return WorkflowActivityResult.Fail($"The item has no field '{fieldName}'.");
            }

            JsonNode termValue = field.AllowMultiple ? new JsonArray(term!.Id.ToString()) : JsonValue.Create(term!.Id.ToString());
            var result = await store.UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, new JsonObject { [fieldName] = termValue }, null, cancellationToken);
            if (!result.Succeeded)
            {
                return WorkflowActivityResult.Fail(ItemUpdateAction.Describe(result));
            }

            applied = true;
        }

        var output = new JsonObject
        {
            ["term"] = term?.Name,
            ["termId"] = term?.Id.ToString(),
            ["confidence"] = confidence,
            ["applied"] = applied,
            ["cached"] = answer.Cached,
        };
        return sure ? WorkflowActivityResult.Ok(output) : WorkflowActivityResult.Ok(AiActivity.LowConfidence, output);
    }
}

/// <summary><c>ai.summarize</c> (AI-04): a short summary of the item's text, optionally written to a text <c>field</c>.</summary>
internal sealed class AiSummarizeActivity(AiGateway ai, IListItemStore items) : IWorkflowActivity
{
    public string Key => "ai.summarize";

    public string Description => "Summarizes the item's text with AI: { \"maxWords\": 60, \"field\": \"summary\" }.";

    public JsonObject? InputSchema => AiActivity.Schema([],
        ("maxWords", Schemas.Number("Longest summary in words; default 80.")),
        ("field", Schemas.Text("A text field of the item to write the summary to (optional).")),
        ("instructions", Schemas.Text("Extra instructions (template), e.g. the language.")));

    public JsonObject? OutputSchema => Schemas.Object([], ("summary", Schemas.Text("The summary.")));

    public IEnumerable<string> Validate(JsonObject inputs) => AiActivity.ValidateExecution(inputs)
        .Concat(inputs["maxWords"] is null || Inputs.Number(inputs, "maxWords") is > 0 and <= 2000 ? [] : ["maxWords must be from 1 to 2000."]);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("ai.summarize needs an item.");
        }

        var words = (int)(Inputs.Number(context.Inputs, "maxWords") ?? 80);
        var instructions = $"Summarize the document in at most {words.ToString(CultureInfo.InvariantCulture)} words. Answer with the summary only."
            + (Inputs.Text(context.Inputs, "instructions") is { } extra ? "\n" + await context.ExpandAsync(extra, cancellationToken) : string.Empty);
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, instructions, await ai.ItemTextAsync(item, cancellationToken)), context, cancellationToken);

        if (wait is not null)
        {
            return wait;
        }
        if (answer is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        var summary = answer.Text.Trim();
        if (Inputs.Text(context.Inputs, "field") is { } field)
        {
            var result = await items.AsSystem().UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, new JsonObject { [field] = summary }, null, cancellationToken);
            if (!result.Succeeded)
            {
                return WorkflowActivityResult.Fail(ItemUpdateAction.Describe(result));
            }
        }

        return WorkflowActivityResult.Ok(new JsonObject { ["summary"] = summary, ["cached"] = answer.Cached });
    }
}

/// <summary>
/// <c>ai.prompt</c>: asks the model anything (<c>prompt</c>, <c>system</c>: templates), optionally with the item's text
/// (<c>includeContent</c>) and a JSON Schema for the answer (<c>schema</c>). The answer is the output's <c>text</c>, or
/// <c>json</c> with a schema; later nodes use it as <c>{step:Node.text}</c> or <c>{step:Node.json.name}</c>.
/// </summary>
internal sealed class AiPromptActivity(AiGateway ai) : IWorkflowActivity
{
    public string Key => "ai.prompt";

    public string Description => "Asks the AI model: { \"prompt\": \"Is {title} urgent? Answer yes or no.\", \"includeContent\": true }.";

    public JsonObject? InputSchema => AiActivity.Schema(["prompt"],
        ("prompt", Schemas.Text("The question (template).")),
        ("system", Schemas.Text("Instructions for the model (template).")),
        ("includeContent", Schemas.Any("true to add the item's text.")),
        ("schema", Schemas.Values("A JSON Schema object the answer must follow (structured output).")));

    public JsonObject? OutputSchema => Schemas.Object([], ("text", Schemas.Text("The answer.")), ("json", Schemas.Values("The answer as JSON (with a schema).")));

    public IEnumerable<string> Validate(JsonObject inputs) => AiActivity.ValidateExecution(inputs)
        .Concat(Inputs.Required(inputs, "prompt")).Concat(inputs["schema"] is null or JsonObject ? [] : ["schema must be a JSON Schema object."]);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var prompt = await context.ExpandAsync(Inputs.Text(context.Inputs, "prompt")!, cancellationToken);
        if (context.Inputs["includeContent"] is JsonValue include && include.GetValueKind() == JsonValueKind.True && context.Item is { } item)
        {
            prompt += "\n\n" + await ai.ItemTextAsync(item, cancellationToken);
        }

        var system = Inputs.Text(context.Inputs, "system") is { } text ? await context.ExpandAsync(text, cancellationToken) : "You help automate work with documents and lists.";
        var schema = context.Inputs["schema"] as JsonObject;
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, system, prompt, schema?.DeepClone().AsObject()), context, cancellationToken);

        if (wait is not null)
        {
            return wait;
        }
        if (answer is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        if (schema is null)
        {
            return WorkflowActivityResult.Ok(new JsonObject { ["text"] = answer.Text.Trim(), ["cached"] = answer.Cached });
        }

        return answer.Json is { } json
            ? WorkflowActivityResult.Ok(new JsonObject { ["json"] = json, ["cached"] = answer.Cached })
            : WorkflowActivityResult.Fail("The AI answer was not the expected JSON.");
    }
}
