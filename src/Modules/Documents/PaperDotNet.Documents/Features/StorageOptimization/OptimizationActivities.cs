using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features.StorageOptimization;

internal sealed class PrepareOptimization(IDocumentFileStore files, IEnumerable<IDocumentOptimizationAdapter> adapters,
    IWorkspaceAccess workspaces, IUserDirectory users, IWorkflowRecipients recipients) : IWorkflowActivity
{
    public string Key => StorageOptimizationWorkflows.Id + ".prepare";
    public string Description => "Analyzes the current image and stages a smaller file for review; skipped files keep their source.";
    public IReadOnlyList<string> Outcomes => ["candidate", "skipped"];
    public JsonObject? InputSchema => StorageOptimizationWorkflows.Workflow.Parameters;
    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("candidate", ActivitySchemas.Text("Staged candidate id.")),
        ("approvers", ActivitySchemas.People("Resolved reviewers.")), ("reason", ActivitySchemas.Text("Why the original was retained.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item || context.RunId is not { } runId)
        {
            return WorkflowActivityResult.Fail("Optimization requires a document workflow run.");
        }

        var store = files.AsSystem();
        if (await store.GetCandidateAsync(context.ExecutionId, cancellationToken) is { State: "pending" } repeated)
        {
            return await CandidateAsync(repeated.Id, context, cancellationToken);
        }

        var file = await store.GetCurrentAsync(item.ItemId, cancellationToken);
        if (file is null)
        {
            return WorkflowActivityResult.Fail("The item has no file.");
        }

        // A run raised by a superseded upload must not optimize the replacement.
        if (context.Data?["version"] is JsonValue version && version.TryGetValue<int>(out var number) && number != file.Number)
        {
            return WorkflowActivityResult.Ok("skipped", new JsonObject { ["reason"] = "The source upload was superseded." });
        }

        var adapter = adapters.SingleOrDefault(a => a.MediaTypes.Contains(file.MediaType, StringComparer.Ordinal));
        if (file.Source == "optimization" || adapter is null || await store.FindSelectionAsync(file.Id, cancellationToken) is not null)
        {
            return WorkflowActivityResult.Ok("skipped", new JsonObject { ["reason"] = "The current file is already selected or its format is not supported." });
        }

        try
        {
            async Task<double> NumberAsync(string name, double fallback)
            {
                var node = context.Inputs[name];
                if (node is JsonValue token && token.TryGetValue<string>(out var template))
                {
                    node = await context.ResolveAsync(template, cancellationToken);
                }

                return node is null ? fallback : node.GetValue<double>();
            }

            var options = new DocumentOptimizationOptions(await NumberAsync("targetHeight", 12), await NumberAsync("minConfidence", 50),
                await NumberAsync("percentile", 5), checked((int)await NumberAsync("maxAnalysisDimension", 2600)),
                await NumberAsync("minimumScale", 0.05), checked((int)await NumberAsync("quality", 80)));
            await using var source = await store.OpenVersionAsync(file.Id, cancellationToken)
                ?? throw new InvalidOperationException("The source content is missing.");
            await using var optimized = await adapter.OptimizeAsync(source, file.AnalysisLanguages ?? file.Languages, options, cancellationToken);
            optimized.Metrics["languages"] = file.AnalysisLanguages ?? file.Languages;
            if (optimized.Content is null)
            {
                optimized.Metrics["settings"] = JsonSerializer.SerializeToNode(options);
                await store.RetainOriginalAsync(context.ExecutionId, runId, file.Id,
                    optimized.SkipReason ?? "The original is retained.", optimized.Metrics, cancellationToken);
                return WorkflowActivityResult.Ok("skipped", new JsonObject { ["reason"] = optimized.SkipReason });
            }

            var candidate = await store.StageAsync(context.ExecutionId, runId, file.Id, optimized.Content,
                Path.GetFileNameWithoutExtension(file.FileName) + optimized.Extension, optimized.Metrics, cancellationToken);
            return await CandidateAsync(candidate.Id, context, cancellationToken);
        }
#pragma warning disable CA1031 // A failed adapter must fail the activity while retaining the source file.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return WorkflowActivityResult.Fail($"Optimization failed: {ex.Message}");
        }
    }

    private async Task<WorkflowActivityResult> CandidateAsync(Guid candidateId, WorkflowActivityContext context, CancellationToken ct)
    {
        var people = context.Inputs["approvers"];
        if (people is JsonValue token && token.TryGetValue<string>(out var template))
        {
            people = await context.ResolveAsync(template, ct);
        }

        var specs = ActivityInputs.Texts(new JsonObject { ["approvers"] = people?.DeepClone() }, "approvers");
        IReadOnlyList<Guid> ids;
        if (specs is { Count: > 0 })
        {
            var resolved = await recipients.ResolveAsync(specs, context, ct);
            if (resolved.Unknown.Count > 0)
            {
                return WorkflowActivityResult.Fail($"Unknown approvers: {string.Join(", ", resolved.Unknown)}.");
            }

            ids = resolved.Users;
        }
        else
        {
            ids = (await workspaces.GetMembersAsync(context.WorkspaceId, ct)).Where(m => m.Level >= WorkspaceAccessLevel.Manage).Select(m => m.UserId).ToArray();
        }

        var active = new List<Guid>();
        foreach (var id in ids)
        {
            if (await users.IsActiveAsync(id, ct))
            {
                active.Add(id);
            }
        }

        var names = await users.GetUserNamesAsync(active, ct);
        return names.Count == 0 ? WorkflowActivityResult.Fail("No active managers or configured approvers can review this file.")
            : WorkflowActivityResult.Ok("candidate", new JsonObject
            {
                ["candidate"] = candidateId.ToString(),
                ["approvers"] = new JsonArray(names.Values.Select(n => JsonValue.Create(n)).ToArray()),
            });
    }
}

internal sealed class AcceptOptimization(IDocumentFileStore files) : IWorkflowActivity
{
    public string Key => StorageOptimizationWorkflows.Id + ".accept";
    public string Description => "Promotes the reviewed candidate only if its source is still current, and releases the source version.";
    public JsonObject? InputSchema => ActivitySchemas.Of(["candidate"], ("candidate", ActivitySchemas.Text("Staged candidate id.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var id = await OptimizationCandidate.IdAsync(context, cancellationToken);
        var candidate = id is { } value ? await files.AsSystem().GetCandidateAsync(value, cancellationToken) : null;
        return candidate is not null && candidate.Source.ItemId == context.Item?.ItemId && candidate.RunId == context.RunId
            && await files.AsSystem().PromoteAsync(candidate.Id, cancellationToken)
            ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail("The candidate is unavailable or its source file changed.");
    }
}

internal sealed class DiscardOptimization(IDocumentFileStore files) : IWorkflowActivity
{
    public string Key => StorageOptimizationWorkflows.Id + ".discard";
    public string Description => "Retains the original and releases the rejected candidate's storage.";
    public JsonObject? InputSchema => ActivitySchemas.Of(["candidate"], ("candidate", ActivitySchemas.Text("Staged candidate id.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var id = await OptimizationCandidate.IdAsync(context, cancellationToken);
        var candidate = id is { } value ? await files.AsSystem().GetCandidateAsync(value, cancellationToken) : null;
        return candidate is not null && candidate.Source.ItemId == context.Item?.ItemId && candidate.RunId == context.RunId
            && await files.AsSystem().DiscardAsync(candidate.Id, cancellationToken)
            ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail("The candidate is unavailable.");
    }
}

internal static class OptimizationCandidate
{
    public static async Task<Guid?> IdAsync(WorkflowActivityContext context, CancellationToken ct)
    {
        var node = context.Inputs["candidate"];
        if (node is JsonValue token && token.TryGetValue<string>(out var template))
        {
            node = await context.ResolveAsync(template, ct);
        }

        return Guid.TryParse(node?.GetValue<string>(), out var id) ? id : null;
    }
}
