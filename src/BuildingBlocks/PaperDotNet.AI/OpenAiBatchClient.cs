using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Batch;
using OpenAI.Files;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.AI;

/// <summary>
/// The batch API for AI activities with <c>execution: batch</c> (<c>AI:Batch</c>, AI-08, ADR-0036). Off by default: without
/// it, the "AI batch" workflow answers the waiting questions with the chat model.
/// </summary>
public sealed class AiBatchApiOptions
{
    public const string Section = "AI:Batch";

    /// <summary><c>none</c> (default) or <c>openai</c>: the OpenAI Batch API, also Azure OpenAI and Azure AI Foundry (<c>…/openai/v1/</c>).</summary>
    public string Provider { get; set; } = "none";

    /// <summary>Base URL of the API; default: <c>AI:Chat:Endpoint</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>API key; default: <c>AI:Chat:ApiKey</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>How long the provider may take (<c>24h</c>, the only window most providers accept).</summary>
    public string CompletionWindow { get; set; } = "24h";
}

/// <summary>
/// <see cref="IAiBatchClient"/> on the OpenAI Batch API (<c>/files</c> and <c>/batches</c>), which Azure OpenAI and Azure AI
/// Foundry offer at the same paths under <c>…/openai/v1/</c>: the lines go up as a JSONL file of chat completion requests,
/// the batch carries the step's tag in its metadata (so <see cref="FindAsync"/> finds it after a crash), and the answers
/// are read from its output and error files.
/// </summary>
#pragma warning disable OPENAI001 // The batch and file clients are marked experimental; their REST API is stable.
public sealed class OpenAiBatchClient(OpenAIClient client, string completionWindow = "24h") : IAiBatchClient
{
    /// <summary>The metadata key that holds the tag of a batch.</summary>
    public const string TagKey = "paperdotnet_batch";

    private const string Endpoint = "/v1/chat/completions";

    /// <summary>How far back <see cref="FindAsync"/> looks: the longest a batched step waits.</summary>
    private static readonly TimeSpan FindWindow = TimeSpan.FromDays(15);

    private readonly BatchClient _batches = client.GetBatchClient();
    private readonly OpenAIFileClient _files = client.GetOpenAIFileClient();

    public async Task<string> SubmitAsync(Guid batchId, IReadOnlyList<AiBatchLine> lines, CancellationToken cancellationToken)
    {
        var fileId = await UploadAsync(Jsonl(lines), $"paperdotnet-{batchId:N}.jsonl", cancellationToken);
        var request = new JsonObject
        {
            ["input_file_id"] = fileId,
            ["endpoint"] = Endpoint,
            ["completion_window"] = completionWindow,
            ["metadata"] = new JsonObject { [TagKey] = batchId.ToString("N") },
        };
        var operation = await _batches.CreateBatchAsync(BinaryContent.Create(BinaryData.FromString(request.ToJsonString())), waitUntilCompleted: false,
            new RequestOptions { CancellationToken = cancellationToken });
        return JsonNode.Parse(operation.GetRawResponse().Content)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The batch API answered without a batch id.");
    }

    /// <summary>
    /// Uploads the JSONL file (purpose <c>batch</c>). The form is built here: the SDK's typed upload sends the file part
    /// without a content type, which Azure OpenAI rejects ("Invalid file ContentType").
    /// </summary>
    private async Task<string> UploadAsync(string jsonl, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new StringContent(jsonl, Encoding.UTF8);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(new StringContent("batch"), "purpose");
        form.Add(file, "file", fileName);
        var body = await form.ReadAsByteArrayAsync(ct);
        var result = await _files.UploadFileAsync(BinaryContent.Create(BinaryData.FromBytes(body)), form.Headers.ContentType!.ToString(),
            new RequestOptions { CancellationToken = ct });
        return JsonNode.Parse(result.GetRawResponse().Content)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The file API answered without a file id.");
    }

    public async Task<string?> FindAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var tag = batchId.ToString("N");
        var since = DateTimeOffset.UtcNow - FindWindow;
        await foreach (var batch in _batches.GetBatchesAsync(new BatchCollectionOptions { PageSizeLimit = 100 }, cancellationToken))
        {
            if (batch.Metadata is { } metadata && metadata.TryGetValue(TagKey, out var found) && found == tag)
            {
                return batch.Id;
            }

            // Newest first: older batches cannot be one a waiting step sent.
            if (batch.CreatedAt < since)
            {
                return null;
            }
        }

        return null;
    }

    public async Task<AiBatchStatus> GetAsync(string providerBatchId, CancellationToken cancellationToken)
    {
        var response = await _batches.GetBatchAsync(providerBatchId, new RequestOptions { CancellationToken = cancellationToken });
        var batch = JsonNode.Parse(response.GetRawResponse().Content)!.AsObject();
        var status = batch["status"]?.GetValue<string>();
        if (status is "validating" or "in_progress" or "finalizing" or "cancelling")
        {
            return new AiBatchStatus(AiBatchState.Running, []);
        }

        // Failed, expired and cancelled batches can have answered part of their lines; those count.
        var results = new List<AiBatchResult>();
        foreach (var fileId in new[] { Text(batch, "output_file_id"), Text(batch, "error_file_id") })
        {
            if (fileId is not null)
            {
                var content = await _files.DownloadFileAsync(fileId, cancellationToken);
                results.AddRange(ParseResults(content.Value.ToString()));
            }
        }

        return status == "completed"
            ? new AiBatchStatus(AiBatchState.Completed, results)
            : new AiBatchStatus(AiBatchState.Failed, results, Errors(batch) ?? status);
    }

    /// <summary>The lines as the batch API's JSONL: one chat completion request per line, with a JSON schema when the question has one.</summary>
    public static string Jsonl(IEnumerable<AiBatchLine> lines)
    {
        var jsonl = new StringBuilder();
        foreach (var line in lines)
        {
            var body = new JsonObject
            {
                ["model"] = line.Model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = line.Instructions },
                    new JsonObject { ["role"] = "user", ["content"] = UserContent(line) }),
            };
            if (line.Schema is { } schema)
            {
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "answer", ["schema"] = schema.DeepClone() },
                };
            }

            jsonl.Append(new JsonObject { ["custom_id"] = line.CustomId, ["method"] = "POST", ["url"] = Endpoint, ["body"] = body }.ToJsonString()).Append('\n');
        }

        return jsonl.ToString();
    }

    /// <summary>The input as text, or with images as content parts (data URLs).</summary>
    private static JsonNode UserContent(AiBatchLine line)
    {
        if (line.Images is not { Count: > 0 } images)
        {
            return JsonValue.Create(line.Input);
        }

        var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line.Input });
        foreach (var image in images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Content)}" },
            });
        }

        return parts;
    }

    /// <summary>Reads an output or error file: the answer (or error) and tokens of each line.</summary>
    public static IEnumerable<AiBatchResult> ParseResults(string jsonl)
    {
        foreach (var text in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonObject? line;
            try
            {
                line = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (line is null || Text(line, "custom_id") is not { } customId)
            {
                continue;
            }

            var response = line["response"] as JsonObject;
            var body = response?["body"] as JsonObject;
            var usage = body?["usage"] as JsonObject;
            long input = Number(usage, "prompt_tokens"), output = Number(usage, "completion_tokens");
            var status = response?["status_code"] is JsonValue code && code.TryGetValue<int>(out var value) ? value : 0;
            if (status is >= 200 and < 300 && body?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var answer))
            {
                yield return new AiBatchResult(customId, answer, input, output);
                continue;
            }

            var error = Text(line["error"] as JsonObject, "message")
                ?? Text(body?["error"] as JsonObject, "message")
                ?? (status == 0 ? "The line has no answer." : $"The request failed with status {status}.");
            yield return new AiBatchResult(customId, null, input, output, error);
        }
    }

    private static string? Errors(JsonObject batch) =>
        batch["errors"]?["data"] is JsonArray { Count: > 0 } errors
            ? string.Join("; ", errors.OfType<JsonObject>().Select(e => Text(e, "message") ?? Text(e, "code")).OfType<string>())
            : null;

    private static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static long Number(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}
#pragma warning restore OPENAI001
