using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.AiWorkflows.Features;

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

    /// <summary>How long call records are kept (days; at least <see cref="CacheDays"/>).</summary>
    public int RecordDays { get; set; } = 90;
}

/// <summary>
/// Daily: deletes AI call records older than <see cref="WorkflowAiOptions.RecordDays"/> (and the cache), in batches (no
/// bulk deletes under Native AOT).
/// </summary>
internal sealed class AiCallCleanupJob(AiWorkflowsDbContext db, IOptions<WorkflowAiOptions> options, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "ai.callCleanup";
    public const string Schedule = "23 3 * * *";
    private const int Batch = 500;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var days = Math.Max(Math.Max(1, options.Value.RecordDays), options.Value.CacheDays);
        var cutoff = time.GetUtcNow().AddDays(-days).ToUnixTimeMilliseconds();
        while (true)
        {
            var old = await OldAsync(db, tenantId, cutoff, cancellationToken);
            if (old.Count == 0)
            {
                return;
            }

            db.AiCalls.RemoveRange(old);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    private static Task<List<AiCall>> OldAsync(AiWorkflowsDbContext database, Guid tenantId, long cutoffUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var cutoff = cutoffUnixMs;
        var batch = Batch;
        var ct = cancellationToken;
        return context.AiCalls.Where(c => c.TenantId == tenant && c.CreatedAtUnixMs < cutoff).Take(batch).ToListAsync(ct);
    }
}

/// <summary>
/// A question to the chat model: instructions, the input, optionally a JSON Schema the answer must follow, and images of
/// the item's pages.
/// </summary>
internal sealed record AiQuestion(string Activity, string Instructions, string Input, JsonObject? Schema = null, AiImages? Images = null);

/// <summary>
/// The first <paramref name="Pages"/> pages of an item as images, sent with the question (<c>includeImages</c>). Kept as a
/// reference (batched questions wait with it) and loaded when the question is sent; <paramref name="Digest"/> identifies
/// the images for the cache.
/// </summary>
internal sealed record AiImages(Guid ItemId, int Pages, string Digest);

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

/// <summary>The run's item, for activities that work on it.</summary>
internal sealed record AiItem(Guid WorkspaceId, Guid ListId, Guid ItemId)
{
    public static AiItem? Of(WorkflowActivityContext context) =>
        context.ListId is { } list && context.ItemId is { } item ? new AiItem(context.WorkspaceId, list, item) : null;
}

/// <summary>
/// All AI activities call the chat model through here (AI-06, ADR-0036): the same model and input reuse an earlier answer
/// (the cache), an organization's daily token budget is enforced, and every call is recorded (<see cref="AiCall"/>: a hash
/// of what was sent, the model, tokens and the answer), in this module's own table. With <c>execution: batch</c> (AI-08)
/// the step waits on an <c>ai.batch</c> wait with its question as data, which the workspace's batch workflow answers
/// (<see cref="AiBatchActivity"/>); the step then runs again with it. Every call names its tenant (no ambient tenant in
/// workflow runs).
/// </summary>
internal sealed class AiGateway(
    IServiceProvider services, AiWorkflowsDbContext db, IWorkflowDirectory workflows, IOptions<WorkflowAiOptions> options,
    IOptions<AiBatchOptions> batchOptions, TimeProvider time)
{
    private readonly IChatClient? _client = services.GetService<IChatClient>();

    public const string NotConfigured = "AI is not configured on this server (AI:Chat).";

    /// <summary>Where calls of workflow runs come from (<see cref="AiCall.Source"/>).</summary>
    public const string WorkflowSource = "workflow";

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

        var tenantId = context.TenantId;
        var model = Model;
        var hash = Hash(model, question);
        var now = time.GetUtcNow();
        Task RecordAsync(string? response, bool cached) =>
            AddAsync(NewCall(tenantId, question.Activity, WorkflowSource, context.RunId, model, hash, response, cached, now), ct);

        var execution = ActivityInputs.Text(context.Inputs, AiActivity.Execution) ?? batchOptions.Value.Execution;
        if (execution == AiActivity.Batch)
        {
            var immediately = ActivityInputs.Text(context.Inputs, AiActivity.OnDeadline) != AiActivity.Fail;
            if (context.Resumed is { Kind: AiBatch.WaitKind } resumed)
            {
                // Back from the batch: its answer, the model's error, or the deadline passed without an answer.
                if (resumed.Payload?["text"] is JsonValue answer && answer.TryGetValue<string>(out var answered))
                {
                    await RecordAsync(answered, cached: true);
                    if (Fits(question, answered))
                    {
                        return (new AiAnswer(answered, false, 0), null, null);
                    }

                    // An answer the step cannot use (a retry would get it again): the question is asked again now.
                }
                else if (resumed.Payload?["error"] is JsonValue failure && failure.TryGetValue<string>(out var failed) && !resumed.TimedOut)
                {
                    return (null, $"The AI model failed: {failed}", null);
                }
                else if (!immediately)
                {
                    return (null, AiBatch.MissedDeadline, null);
                }
            }
            else if (await CachedAsync(tenantId, hash, question, now, ct) is { } cachedAnswer)
            {
                await RecordAsync(cachedAnswer, cached: true);
                return (new AiAnswer(cachedAnswer, true, 0), null, null);
            }
            else if (await workflows.IsActivityUsedAsync(tenantId, context.WorkspaceId, AiBatch.ActivityKey, ct))
            {
                // Waits for the workspace's batch workflow (ai.batch) with the question as the wait's data.
                var hours = ActivityInputs.Number(context.Inputs, AiActivity.DeadlineHours) ?? batchOptions.Value.DeadlineHours;
                return (null, null, WorkflowActivityResult.WaitAndRunAgain(
                    AiBatch.WaitKind, context.ExecutionId.ToString("N"), now.AddHours(Math.Clamp(hours, 1, 24 * 14)), AiBatch.Data(hash, model, question)));
            }

            // No batch workflow is on in the workspace: nothing would answer, so the question is asked now.
        }
        else if (await CachedAsync(tenantId, hash, question, now, ct) is { } cachedAnswer)
        {
            await RecordAsync(cachedAnswer, cached: true);
            return (new AiAnswer(cachedAnswer, true, 0), null, null);
        }

        var (call, error) = await CallAsync(tenantId, question, model, hash, context.RunId, ct);
        return call?.Response is { } text ? (new AiAnswer(text, false, call.InputTokens + call.OutputTokens), null, null) : (null, error, null);
    }

    /// <summary>
    /// Calls the model now (within the day's budget) and records the call: the record, or null and why when the budget is
    /// used up; a failed call is recorded with its error.
    /// </summary>
    public async Task<(AiCall? Call, string? Error)> CallAsync(Guid tenantId, AiQuestion question, string model, string hash, Guid? runId, CancellationToken ct)
    {
        if (_client is null)
        {
            return (null, NotConfigured);
        }

        if (await BudgetProblemAsync(tenantId, ct) is { } problem)
        {
            return (null, problem);
        }

        var chat = new ChatOptions();
        if (question.Schema is { } format)
        {
            chat.ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonElement.Parse(format.ToJsonString()), question.Activity.Replace('.', '_'));
        }

        var now = time.GetUtcNow();
        ChatResponse response;
        try
        {
            List<AIContent> input = [new TextContent(question.Input)];
            if (question.Images is { } images)
            {
                input.AddRange((await LoadImagesAsync(tenantId, images.ItemId, images.Pages, ct)).Select(i => new DataContent(i.Content, i.MediaType)));
            }

            response = await _client.GetResponseAsync([new(ChatRole.System, question.Instructions), new(ChatRole.User, input)], chat, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = NewCall(tenantId, question.Activity, WorkflowSource, runId, model, hash, null, false, now);
            failed.Error = Truncate(ex.Message, 2000);
            await AddAsync(failed, ct);
            return (failed, $"The AI model failed: {ex.Message}");
        }

        var call = NewCall(tenantId, question.Activity, WorkflowSource, runId, model, hash, response.Text, false, now);
        call.InputTokens = response.Usage?.InputTokenCount ?? 0;
        call.OutputTokens = response.Usage?.OutputTokenCount ?? 0;
        await AddAsync(call, ct);
        return (call, null);
    }

    /// <summary>Saves the record of a call (right away: an answer paid for is kept even when the step fails later).</summary>
    public async Task AddAsync(AiCall call, CancellationToken ct)
    {
        db.AiCalls.Add(call);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Why the organization cannot use more tokens today, or null.</summary>
    public async Task<string?> BudgetProblemAsync(Guid tenantId, CancellationToken ct)
    {
        var limit = options.Value.DailyTokens;
        if (limit <= 0)
        {
            return null;
        }

        var today = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var used = await TokensSinceAsync(db, tenantId, today, ct);
        return used >= limit ? $"The organization's AI budget for today ({limit} tokens) is used up." : null;
    }

    private static Task<long> TokensSinceAsync(AiWorkflowsDbContext database, Guid tenantId, long sinceUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var since = sinceUnixMs;
        var ct = cancellationToken;
        return context.AiCalls.Where(c => c.TenantId == tenant && c.CreatedAtUnixMs >= since).SumAsync(c => c.InputTokens + c.OutputTokens, ct);
    }

    public static string Hash(string model, AiQuestion question) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
            [model, question.Instructions, question.Input, question.Schema?.ToJsonString() ?? string.Empty, .. question.Images is { } images ? [images.Digest] : Array.Empty<string>()]))));

    /// <summary>The item's page images the activity asks for (<c>includeImages</c>), or null for none.</summary>
    public async Task<AiImages?> ImagesAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        var pages = AiActivity.ImagePages(context.Inputs);
        if (pages == 0 || context.ItemId is not { } itemId)
        {
            return null;
        }

        var images = await LoadImagesAsync(context.TenantId, itemId, pages, ct);
        if (images.Count == 0)
        {
            return null;
        }

        var digest = Convert.ToHexString(SHA256.HashData([.. images.SelectMany(i => SHA256.HashData(i.Content))]));
        return new AiImages(itemId, pages, digest);
    }

    /// <summary>The page images of an item (none without a source, e.g. an item without a file).</summary>
    public async Task<IReadOnlyList<ItemPageImage>> LoadImagesAsync(Guid tenantId, Guid itemId, int pages, CancellationToken ct) =>
        services.GetService<IItemPageImageSource>() is { } source ? await source.GetPageImagesAsync(tenantId, itemId, pages, ct) : [];

    public static AiCall NewCall(Guid tenantId, string activity, string source, Guid? runId, string model, string hash, string? response, bool cached, DateTimeOffset at) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        Activity = Truncate(activity, 200),
        Source = Truncate(source, 300),
        RunId = runId,
        Model = Truncate(model, 200),
        InputHash = hash,
        Cached = cached,
        Response = response,
        CreatedAt = at,
        CreatedAtUnixMs = at.ToUnixTimeMilliseconds(),
    };

    public static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    /// <summary>
    /// An earlier answer to the same model and input within <see cref="WorkflowAiOptions.CacheDays"/>, or null. Only answers
    /// that fit the question are reused (JSON when it has a schema): a bad answer is asked again, not repeated for days.
    /// </summary>
    public async Task<string?> CachedAsync(Guid tenantId, string hash, AiQuestion question, DateTimeOffset now, CancellationToken ct)
    {
        if (options.Value.CacheDays <= 0)
        {
            return null;
        }

        var answers = await AnswersAsync(db, tenantId, hash, now.AddDays(-options.Value.CacheDays).ToUnixTimeMilliseconds(), ct);
        return answers.FirstOrDefault(a => a is not null && Fits(question, a));
    }

    private static Task<List<string?>> AnswersAsync(AiWorkflowsDbContext database, Guid tenantId, string inputHash, long sinceUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var hash = inputHash;
        var since = sinceUnixMs;
        var take = 5;
        var ct = cancellationToken;
        return context.AiCalls.AsNoTracking()
            .Where(c => c.TenantId == tenant && c.InputHash == hash && c.Response != null && c.CreatedAtUnixMs >= since)
            .OrderByDescending(c => c.CreatedAtUnixMs)
            .Select(c => c.Response)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>Whether an answer can be used for the question: JSON when the question has a schema, else any text.</summary>
    public static bool Fits(AiQuestion question, string answer) => question.Schema is null || new AiAnswer(answer, false, 0).Json is not null;

    /// <summary>
    /// What the model reads about an item: its values (<c>name: value</c> lines), then the text others contribute to search
    /// (e.g. a document's pages), cut to <see cref="WorkflowAiOptions.MaxInputCharacters"/>.
    /// </summary>
    public async Task<string> ItemTextAsync(WorkflowActivityContext context, AiItem item, CancellationToken ct)
    {
        var store = services.GetRequiredService<IListItemStore>().AsSystem(context.Actor);
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
            if ((await contributor.GetContentAsync(context.TenantId, [item.ItemId], ct)).TryGetValue(item.ItemId, out var content)
                && (content.Pages is { Count: > 0 } pages ? string.Join("\n\n", pages) : content.Text) is { Length: > 0 } contributed)
            {
                text.AppendLine().AppendLine(contributed);
            }
        }

        var max = Math.Max(1000, options.Value.MaxInputCharacters);
        return text.Length > max ? text.ToString(0, max) : text.ToString();
    }
}
