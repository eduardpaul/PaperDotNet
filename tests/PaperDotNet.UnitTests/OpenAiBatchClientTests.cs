using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenAI;
using PaperDotNet.AI;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.UnitTests;

public sealed class OpenAiBatchClientTests
{
    private static readonly JsonObject Schema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["store"] = new JsonObject { ["type"] = "string" } } };

    [Fact]
    public async Task Submits_the_lines_as_a_tagged_batch_of_chat_completions()
    {
        var api = new FakeBatchApi();
        var client = Client(api);
        var tag = Guid.CreateVersion7();

        var id = await client.SubmitAsync(tag, [new("h1", "gpt-4.1", "Read the receipt.", "LIDL 2,99", Schema), new("h2", "gpt-4.1", "Say hi.", "hi", null),
            new("h3", "gpt-4.1", "Read the photo.", "Read it.", null, [new("image/jpeg", [1, 2, 3])])],
            TestContext.Current.CancellationToken);

        Assert.Equal("batch_1", id);
        var lines = api.UploadedJsonl!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(["h1", "h2", "h3"], lines.Select(l => l["custom_id"]!.GetValue<string>()));
        Assert.All(lines, l => Assert.Equal("/v1/chat/completions", l["url"]!.GetValue<string>()));
        Assert.Equal("gpt-4.1", lines[0]["body"]!["model"]!.GetValue<string>());
        Assert.Equal("Read the receipt.", lines[0]["body"]!["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("LIDL 2,99", lines[0]["body"]!["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("json_schema", lines[0]["body"]!["response_format"]!["type"]!.GetValue<string>());
        Assert.Null(lines[1]["body"]!["response_format"]);
        var parts = lines[2]["body"]!["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("Read it.", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AQID", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal(tag.ToString("N"), api.Created!["metadata"]![OpenAiBatchClient.TagKey]!.GetValue<string>());
        Assert.Equal("/v1/chat/completions", api.Created["endpoint"]!.GetValue<string>());
        Assert.Equal(api.FileId, api.Created["input_file_id"]!.GetValue<string>());

        // After a crash the step finds the batch it sent by its tag.
        Assert.Equal("batch_1", await client.FindAsync(tag, TestContext.Current.CancellationToken));
        Assert.Null(await client.FindAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reads_answers_errors_and_tokens_of_a_finished_batch()
    {
        var api = new FakeBatchApi { Status = "in_progress" };
        var client = Client(api);

        Assert.Equal(AiBatchState.Running, (await client.GetAsync("batch_1", TestContext.Current.CancellationToken)).State);
        api.FailNext = HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<ClientResultException>(() => client.GetAsync("batch_1", TestContext.Current.CancellationToken));

        api.Status = "completed";
        api.Output = """
            {"custom_id": "h1", "response": {"status_code": 200, "body": {"choices": [{"message": {"content": "{\"store\":\"LIDL\"}"}}], "usage": {"prompt_tokens": 120, "completion_tokens": 8}}}, "error": null}
            {"custom_id": "h2", "response": {"status_code": 400, "body": {"error": {"message": "Invalid schema."}}}, "error": null}
            """;
        api.Errors = """{"custom_id": "h3", "response": null, "error": {"code": "timeout", "message": "The request timed out."}}""";
        var status = await client.GetAsync("batch_1", TestContext.Current.CancellationToken);

        Assert.Equal(AiBatchState.Completed, status.State);
        var results = status.Results.ToDictionary(r => r.CustomId);
        Assert.Equal("""{"store":"LIDL"}""", results["h1"].Text);
        Assert.Equal((120, 8), (results["h1"].InputTokens, results["h1"].OutputTokens));
        Assert.Equal("Invalid schema.", results["h2"].Error);
        Assert.Null(results["h2"].Text);
        Assert.Equal("The request timed out.", results["h3"].Error);

        api.Status = "expired";
        var expired = await client.GetAsync("batch_1", TestContext.Current.CancellationToken);
        Assert.Equal(AiBatchState.Failed, expired.State);
        Assert.Contains(expired.Results, r => r.CustomId == "h1" && r.Text is not null);
    }

    private static OpenAiBatchClient Client(FakeBatchApi api) =>
        new(new OpenAIClient(new ApiKeyCredential("key"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://foundry.example/openai/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(api)),
            RetryPolicy = new ClientRetryPolicy(0),
        }));

    /// <summary>The <c>/files</c> and <c>/batches</c> endpoints with one batch.</summary>
    private sealed class FakeBatchApi : HttpMessageHandler
    {
        public string FileId { get; } = "file-in";

        public string? UploadedJsonl { get; private set; }

        public JsonObject? Created { get; private set; }

        public string Status { get; set; } = "validating";

        public string? Output { get; set; }

        public string? Errors { get; set; }

        public HttpStatusCode? FailNext { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal("Bearer key", request.Headers.Authorization?.ToString());
            if (FailNext is { } failure)
            {
                FailNext = null;
                return new HttpResponseMessage(failure) { Content = new StringContent("""{"error":{"message":"No."}}""", Encoding.UTF8, "application/json") };
            }

            switch (request.Method.Method, path)
            {
                case ("POST", "/openai/v1/files"):
                    var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                    Assert.Contains("batch", form, StringComparison.Ordinal);
                    // Azure OpenAI rejects a file part without a content type.
                    Assert.Contains("Content-Type: application/octet-stream", form, StringComparison.Ordinal);
                    UploadedJsonl = string.Join('\n', form.Split('\n').Where(l => l.TrimStart().StartsWith("{\"custom_id\"", StringComparison.Ordinal)));
                    return Json(HttpStatusCode.Created, new JsonObject { ["id"] = FileId, ["object"] = "file", ["purpose"] = "batch", ["filename"] = "x.jsonl", ["bytes"] = 1, ["created_at"] = 1, ["status"] = "processed" });
                case ("POST", "/openai/v1/batches"):
                    Created = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                    return Json(HttpStatusCode.Created, Batch()); // Azure answers 201 where OpenAI answers 200
                case ("GET", "/openai/v1/batches"):
                    return Json(new JsonObject { ["object"] = "list", ["data"] = new JsonArray(Batch()), ["has_more"] = false });
                case ("GET", "/openai/v1/batches/batch_1"):
                    return Json(Batch());
                case ("GET", "/openai/v1/files/file-out/content"):
                    return Text(Output!);
                case ("GET", "/openai/v1/files/file-err/content"):
                    return Text(Errors!);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private JsonObject Batch() => new()
        {
            ["id"] = "batch_1",
            ["object"] = "batch",
            ["endpoint"] = "/v1/chat/completions",
            ["input_file_id"] = FileId,
            ["completion_window"] = "24h",
            ["status"] = Status,
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["output_file_id"] = Output is null ? null : "file-out",
            ["error_file_id"] = Errors is null ? null : "file-err",
            ["metadata"] = Created?["metadata"]?.DeepClone(),
        };

        private static HttpResponseMessage Json(JsonObject body) => Json(HttpStatusCode.OK, body);

        private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/octet-stream") };
    }
}
