using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.AiWorkflows.Features;

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
    public const string IncludeImages = "includeImages";

    /// <summary>Most pages sent as images.</summary>
    public const int MaxImagePages = 10;

    /// <summary>How many pages go as images: <c>includeImages</c> true (3) or a number of pages; 0 without.</summary>
    public static int ImagePages(JsonObject inputs) => inputs[IncludeImages] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.True => 3,
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => Math.Clamp((int)value.GetValue<double>(), 0, MaxImagePages),
        _ => 0,
    };

    /// <summary>The inputs of an AI activity with those every AI activity has (batch execution).</summary>
    public static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties) => ActivitySchemas.Of(required,
    [
        .. properties,
        (Execution, ActivitySchemas.Text("immediate (call the model now) or batch (queue for the organization's next batch: cheaper, "
            + "the run waits); default from AI:Batch:Execution.")),
        (DeadlineHours, ActivitySchemas.Number("With batch: the longest wait in hours (default from AI:Batch:DeadlineHours).")),
        (OnDeadline, ActivitySchemas.Text("With batch, when the deadline passes without an answer: immediate (default: call the model now) or fail.")),
        (IncludeImages, ActivitySchemas.Any($"true (3 pages) or a number of pages (up to {MaxImagePages}) to send the item's pages as images, "
            + "for models that read images (e.g. photos of receipts).")),
    ]);

    /// <summary>Checks the inputs every AI activity has.</summary>
    public static IEnumerable<string> ValidateExecution(JsonObject inputs)
    {
        if (ActivityInputs.Text(inputs, Execution) is { } execution && execution is not (Immediate or Batch))
        {
            yield return "execution must be immediate or batch.";
        }

        if (inputs[DeadlineHours] is not null && ActivityInputs.Number(inputs, DeadlineHours) is not (>= 1 and <= 336))
        {
            yield return "deadlineHours must be from 1 to 336.";
        }

        if (ActivityInputs.Text(inputs, OnDeadline) is { } onDeadline && onDeadline is not (Immediate or Fail))
        {
            yield return "onDeadline must be immediate or fail.";
        }

        if (inputs[IncludeImages] is { } images && !(images.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            || (images.GetValueKind() == JsonValueKind.Number && ActivityInputs.Number(inputs, IncludeImages) is >= 0 and <= MaxImagePages)))
        {
            yield return $"includeImages must be true, false or a number of pages up to {MaxImagePages}.";
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

        if (ActivityInputs.Text(inputs, "mode") is { } mode && mode is not (Apply or Suggest))
        {
            yield return "mode must be apply or suggest.";
        }

        if (inputs["minConfidence"] is not null && ActivityInputs.Number(inputs, "minConfidence") is not (>= 0 and <= 1))
        {
            yield return "minConfidence must be a number from 0 to 1.";
        }
    }

    public static bool Applies(JsonObject inputs) => ActivityInputs.Text(inputs, "mode") != Suggest;

    public static double MinConfidence(JsonObject inputs) => ActivityInputs.Number(inputs, "minConfidence") ?? 0.7;

    public static double Confidence(JsonNode? value) =>
        value is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number.GetValue<double>() : 0;
}

/// <summary>
/// <c>ai.extract</c> (AI-03, AI-07): fills chosen fields of the item from its text (e.g. a receipt's store, date and total)
/// with a structured-output call. Values with at least <c>minConfidence</c> are applied (<c>mode: suggest</c> only
/// returns them); when a requested field stays empty or uncertain the outcome is <c>lowConfidence</c>.
/// </summary>
internal sealed class AiExtractActivity : IWorkflowActivity
{
    public string Key => "ai.extract";

    public string Description => "Fills fields of the item from its text with AI: { \"fields\": [\"store\", \"total\"], \"mode\": \"apply\", \"minConfidence\": 0.8 }.";

    public IReadOnlyList<string> Outcomes => [AiActivity.LowConfidence];

    public JsonObject? InputSchema => AiActivity.Schema([],
        ("fields", ActivitySchemas.Texts("Fields to fill (text, number, currency, date, choice, …); default: all that can be filled.")),
        ("instructions", ActivitySchemas.Text("Extra instructions (template), e.g. what the document is.")),
        ("mode", ActivitySchemas.Text("apply (default: set the values) or suggest (only return them).")),
        ("minConfidence", ActivitySchemas.Number("Values with less confidence (0 to 1) are not applied; default 0.7.")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("values", ActivitySchemas.Values("The values found, by field.")), ("confidence", ActivitySchemas.Values("Confidence per field (0 to 1).")),
        ("applied", ActivitySchemas.Texts("Fields that were set.")), ("uncertain", ActivitySchemas.Texts("Fields left empty or uncertain.")));

    public IEnumerable<string> Validate(JsonObject inputs) => AiActivity.ValidateCommon(inputs);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (AiItem.Of(context) is not { } item)
        {
            return WorkflowActivityResult.Fail("ai.extract needs an item.");
        }

        var ai = context.Services.GetRequiredService<AiGateway>();
        var store = context.Services.GetRequiredService<IListItemStore>().AsSystem(context.Actor);
        var data = await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, cancellationToken);
        var list = await store.DescribeListAsync(item.WorkspaceId, item.ListId, cancellationToken);
        var fields = list?.ContentTypes.FirstOrDefault(c => c.Id == data?.ContentTypeId)?.Fields ?? [];
        var wanted = ActivityInputs.Texts(context.Inputs, "fields");
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
            + (ActivityInputs.Text(context.Inputs, "instructions") is { } extra ? "\n" + await context.ExpandAsync(extra, cancellationToken) : string.Empty);
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, instructions, await ai.ItemTextAsync(context, item, cancellationToken), format, await ai.ImagesAsync(context, cancellationToken)), context, cancellationToken);

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
                uncertain.Add((JsonNode)name);
            }
        }

        var applied = new JsonArray();
        if (AiActivity.Applies(context.Inputs) && apply.Count > 0)
        {
            var result = await store.UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, apply, null, cancellationToken);
            if (!result.Succeeded)
            {
                return WorkflowActivityResult.Fail(result.Describe());
            }

            foreach (var (name, _) in apply)
            {
                applied.Add((JsonNode)name);
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
internal sealed class AiClassifyActivity : IWorkflowActivity
{
    public string Key => "ai.classify";

    public string Description => "Classifies the item with a term of a term set: { \"termSet\": \"Documents/Types\", \"field\": \"type\" }.";

    public IReadOnlyList<string> Outcomes => [AiActivity.LowConfidence];

    public JsonObject? InputSchema => AiActivity.Schema(["termSet"],
        ("termSet", ActivitySchemas.Text("The term set as Group/Set.")),
        ("field", ActivitySchemas.Text("A managed metadata field of the item to set (optional).")),
        ("instructions", ActivitySchemas.Text("Extra instructions (template).")),
        ("mode", ActivitySchemas.Text("apply (default) or suggest.")),
        ("minConfidence", ActivitySchemas.Number("Less confidence (0 to 1) is not applied; default 0.7.")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("term", ActivitySchemas.Text("The term's name, or null.")), ("termId", ActivitySchemas.Text("The term's id.")),
        ("confidence", ActivitySchemas.Number("0 to 1.")), ("applied", ActivitySchemas.Any("Whether the field was set.")));

    public IEnumerable<string> Validate(JsonObject inputs) =>
        AiActivity.ValidateCommon(inputs).Concat(ActivityInputs.Text(inputs, "termSet") is { } path && path.Split('/').Length == 2 ? [] : ["termSet must be Group/Set."]);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (AiItem.Of(context) is not { } item)
        {
            return WorkflowActivityResult.Fail("ai.classify needs an item.");
        }

        var ai = context.Services.GetRequiredService<AiGateway>();
        var terms = context.Services.GetRequiredService<ITermStore>();

        var path = ActivityInputs.Text(context.Inputs, "termSet")!.Split('/');
        if (await terms.FindTermSetAsync(context.TenantId, path[0], path[1], cancellationToken) is not { } setId)
        {
            return WorkflowActivityResult.Fail($"The term set '{string.Join('/', path)}' does not exist.");
        }

        var candidates = await terms.ListTermsAsync(context.TenantId, setId, cancellationToken);
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
            + (ActivityInputs.Text(context.Inputs, "instructions") is { } extra ? "\n" + await context.ExpandAsync(extra, cancellationToken) : string.Empty);
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, instructions, await ai.ItemTextAsync(context, item, cancellationToken), format, await ai.ImagesAsync(context, cancellationToken)), context, cancellationToken);

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
        if (sure && AiActivity.Applies(context.Inputs) && ActivityInputs.Text(context.Inputs, "field") is { } fieldName)
        {
            var store = context.Services.GetRequiredService<IListItemStore>().AsSystem(context.Actor);
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
                return WorkflowActivityResult.Fail(result.Describe());
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
internal sealed class AiSummarizeActivity : IWorkflowActivity
{
    public string Key => "ai.summarize";

    public string Description => "Summarizes the item's text with AI: { \"maxWords\": 60, \"field\": \"summary\" }.";

    public JsonObject? InputSchema => AiActivity.Schema([],
        ("maxWords", ActivitySchemas.Number("Longest summary in words; default 80.")),
        ("field", ActivitySchemas.Text("A text field of the item to write the summary to (optional).")),
        ("instructions", ActivitySchemas.Text("Extra instructions (template), e.g. the language.")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("summary", ActivitySchemas.Text("The summary.")));

    public IEnumerable<string> Validate(JsonObject inputs) => AiActivity.ValidateExecution(inputs)
        .Concat(inputs["maxWords"] is null || ActivityInputs.Number(inputs, "maxWords") is > 0 and <= 2000 ? [] : ["maxWords must be from 1 to 2000."]);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (AiItem.Of(context) is not { } item)
        {
            return WorkflowActivityResult.Fail("ai.summarize needs an item.");
        }

        var ai = context.Services.GetRequiredService<AiGateway>();

        var words = (int)(ActivityInputs.Number(context.Inputs, "maxWords") ?? 80);
        var instructions = $"Summarize the document in at most {words.ToString(CultureInfo.InvariantCulture)} words. Answer with the summary only."
            + (ActivityInputs.Text(context.Inputs, "instructions") is { } extra ? "\n" + await context.ExpandAsync(extra, cancellationToken) : string.Empty);
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, instructions, await ai.ItemTextAsync(context, item, cancellationToken), null, await ai.ImagesAsync(context, cancellationToken)), context, cancellationToken);

        if (wait is not null)
        {
            return wait;
        }
        if (answer is null)
        {
            return WorkflowActivityResult.Fail(error!);
        }

        var summary = answer.Text.Trim();
        if (ActivityInputs.Text(context.Inputs, "field") is { } field)
        {
            var result = await context.Services.GetRequiredService<IListItemStore>().AsSystem(context.Actor).UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, new JsonObject { [field] = summary }, null, cancellationToken);
            if (!result.Succeeded)
            {
                return WorkflowActivityResult.Fail(result.Describe());
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
internal sealed class AiPromptActivity : IWorkflowActivity
{
    public string Key => "ai.prompt";

    public string Description => "Asks the AI model: { \"prompt\": \"Is {title} urgent? Answer yes or no.\", \"includeContent\": true }.";

    public JsonObject? InputSchema => AiActivity.Schema(["prompt"],
        ("prompt", ActivitySchemas.Text("The question (template).")),
        ("system", ActivitySchemas.Text("Instructions for the model (template).")),
        ("includeContent", ActivitySchemas.Any("true to add the item's text.")),
        ("schema", ActivitySchemas.Values("A JSON Schema object the answer must follow (structured output).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("text", ActivitySchemas.Text("The answer.")), ("json", ActivitySchemas.Values("The answer as JSON (with a schema).")));

    public IEnumerable<string> Validate(JsonObject inputs) => AiActivity.ValidateExecution(inputs)
        .Concat(ActivityInputs.Required(inputs, "prompt")).Concat(inputs["schema"] is null or JsonObject ? [] : ["schema must be a JSON Schema object."]);

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var ai = context.Services.GetRequiredService<AiGateway>();
        var prompt = await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "prompt")!, cancellationToken);
        if (context.Inputs["includeContent"] is JsonValue include && include.GetValueKind() == JsonValueKind.True && AiItem.Of(context) is { } item)
        {
            prompt += "\n\n" + await ai.ItemTextAsync(context, item, cancellationToken);
        }

        var system = ActivityInputs.Text(context.Inputs, "system") is { } text ? await context.ExpandAsync(text, cancellationToken) : "You help automate work with documents and lists.";
        var schema = context.Inputs["schema"] as JsonObject;
        var (answer, error, wait) = await ai.AskAsync(new AiQuestion(Key, system, prompt, schema?.DeepClone().AsObject(), await ai.ImagesAsync(context, cancellationToken)), context, cancellationToken);

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
