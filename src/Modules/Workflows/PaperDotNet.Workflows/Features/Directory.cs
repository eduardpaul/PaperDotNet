using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>What the engine knows about workflows, for activities that work across runs (<see cref="IWorkflowDirectory"/>).</summary>
internal sealed class WorkflowDirectory(WorkflowsDbContext db) : IWorkflowDirectory
{
    public async Task<bool> IsActivityUsedAsync(Guid tenantId, Guid workspaceId, string activityKey, CancellationToken cancellationToken)
    {
        foreach (var workflow in await EnabledAsync(db, tenantId, workspaceId, cancellationToken))
        {
            if (await WorkflowVersions.FindAsync(db, tenantId, workflow.Id, workflow.CurrentVersion, cancellationToken) is { } version
                && Uses(ParseOrNull(version.Definition), activityKey))
            {
                return true;
            }
        }

        return false;
    }

    public Task<bool> IsRunActiveAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var run = runId;
        var running = RunStatus.Running;
        var waiting = RunStatus.Waiting;
        var ct = cancellationToken;
        return context.WorkflowRuns.AnyAsync(r => r.TenantId == tenant && r.Id == run && (r.Status == running || r.Status == waiting), ct);
    }

    private static Task<List<WorkflowDefinition>> EnabledAsync(WorkflowsDbContext database, Guid tenantId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var workspace = workspaceId;
        var ct = cancellationToken;
        return context.Workflows.AsNoTracking().Where(w => w.TenantId == tenant && w.WorkspaceId == workspace && w.Enabled).ToListAsync(ct);
    }

    private static JsonNode? ParseOrNull(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a node of the definition (flow nodes, loop bodies: <c>activity</c>; steps: <c>action</c>) runs the activity.</summary>
    private static bool Uses(JsonNode? node, string activityKey) => node switch
    {
        JsonObject obj => Is(obj["activity"], activityKey) || Is(obj["action"], activityKey) || obj.Any(p => Uses(p.Value, activityKey)),
        JsonArray array => array.Any(e => Uses(e, activityKey)),
        _ => false,
    };

    private static bool Is(JsonNode? value, string activityKey) => value is JsonValue text && text.TryGetValue<string>(out var key) && key == activityKey;
}
