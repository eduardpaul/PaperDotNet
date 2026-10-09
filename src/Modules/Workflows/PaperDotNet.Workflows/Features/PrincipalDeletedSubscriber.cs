using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Takes a deleted user or group out of people inputs (IAM-14, <c>kind: "people"</c>): a field limited to a deleted group
/// (<c>memberOf</c>) is no longer limited, and defaults leave the deleted id out. Every version of a workflow is changed in
/// place (runs keep their version and still open its forms), and so are the forms of pending approvals. People in
/// assignees and recipients are resolved when a step runs, so a deleted one simply reaches no one.
/// </summary>
internal sealed class PrincipalDeletedSubscriber(WorkflowsDbContext db) : IEventSubscriber<PrincipalDeleted>
{
    public async Task HandleAsync(PrincipalDeleted integrationEvent, CancellationToken cancellationToken)
    {
        var principal = integrationEvent.PrincipalId;
        var (lower, upper) = (principal.ToString(), principal.ToString().ToUpperInvariant());
        var changed = new HashSet<Guid>();
        foreach (var version in await db.Versions.Where(v => v.Definition.Contains(lower) || v.Definition.Contains(upper)).ToListAsync(cancellationToken))
        {
            if (Forget(version.Definition, principal) is { } definition)
            {
                version.Definition = definition;
                changed.Add(version.WorkflowId);
            }
        }

        var approvals = await db.Approvals
            .Where(a => a.Status == ApprovalStatus.Pending && a.InputSchema != null && (a.InputSchema.Contains(lower) || a.InputSchema.Contains(upper)))
            .ToListAsync(cancellationToken);
        foreach (var approval in approvals)
        {
            approval.InputSchema = Forget(approval.InputSchema!, principal) ?? approval.InputSchema;
        }

        // A new ETag for the changed workflows: an editor holding the old definition reloads it.
        foreach (var workflow in await db.Workflows.Where(w => changed.Contains(w.Id)).ToListAsync(cancellationToken))
        {
            db.Entry(workflow).State = EntityState.Modified;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The JSON without the principal in people inputs, or null when it had none there.</summary>
    private static string? Forget(string json, Guid principal) =>
        JsonNode.Parse(json) is { } node && DomainInputs.ForgetPrincipal(node, principal) ? node.ToJsonString(DefinitionJson.Options) : null;
}
