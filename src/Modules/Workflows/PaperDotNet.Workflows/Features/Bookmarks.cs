using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Messaging;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Completes waits of runs for other modules and extensions (<see cref="IWorkflowBookmarks"/>, ADR-0036). The bookmark and
/// the message that resumes its run are saved together. A completion that arrives before the run has saved its wait is
/// kept as a bookmark without a run (<see cref="Unclaimed"/>), which the run takes over when it starts waiting. Each call
/// works in its own scope, so an activity can use it during a run without saving (or clearing) the run's state.
/// </summary>
internal sealed class WorkflowBookmarks(ITenantScopeFactory scopes, ITenantContext tenant, TimeProvider time) : IWorkflowBookmarks
{
    /// <summary>The run id of a completion that no run waits for yet.</summary>
    public static readonly Guid Unclaimed = Guid.Empty;

    private static readonly string[] Reserved = [BookmarkKinds.Approval, BookmarkKinds.Delay, BookmarkKinds.Retry];

    private AsyncServiceScope Scope() => scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!);

    public async Task<bool> CompleteAsync(string kind, string key, JsonObject? payload, CancellationToken cancellationToken)
    {
        CheckWait(kind, key);
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var runs = scope.ServiceProvider.GetRequiredService<RunService>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        for (var attempt = 0; ; attempt++)
        {
            var bookmark = await db.Bookmarks.FirstOrDefaultAsync(b => b.Kind == kind && b.Key == key, cancellationToken);
            try
            {
                if (bookmark is null)
                {
                    db.Bookmarks.Add(new WorkflowBookmark
                    {
                        Id = Ids.New(),
                        RunId = Unclaimed,
                        Node = string.Empty,
                        Kind = kind,
                        Key = key,
                        CreatedAt = time.GetUtcNow(),
                        CompletedAt = time.GetUtcNow(),
                        Payload = payload?.ToJsonString(),
                    });
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }

                if (bookmark.CompletedAt is not null)
                {
                    return false;
                }

                runs.Complete(bookmark, payload);
                await outbox.SaveChangesAsync(db, [], [runs.Resume(bookmark.RunId, bookmark.Id)], cancellationToken);
                return true;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Created or completed concurrently (unique kind and key, or the version): look again.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<WorkflowOpenWait>> ListOpenAsync(string kind, Guid workspaceId, int skip, int take, CancellationToken cancellationToken)
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var waits = await db.Bookmarks.AsNoTracking()
            .Where(b => b.Kind == kind && b.CompletedAt == null && db.Runs.Any(r => r.Id == b.RunId && r.WorkspaceId == workspaceId))
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 1000))
            .Select(b => new { b.Key, b.RunId, b.Data, b.CreatedAt, b.ResumeAt })
            .ToListAsync(cancellationToken);
        return [.. waits.Select(w => new WorkflowOpenWait(w.Key, w.RunId, w.Data is { } json ? JsonNode.Parse(json) as JsonObject : null, w.CreatedAt, w.ResumeAt))];
    }

    public async Task<bool> SetDataAsync(string kind, string key, JsonObject? data, CancellationToken cancellationToken)
    {
        CheckWait(kind, key);
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var bookmark = await db.Bookmarks.FirstOrDefaultAsync(b => b.Kind == kind && b.Key == key, cancellationToken);
        if (bookmark is null || bookmark.CompletedAt is not null)
        {
            return false;
        }

        bookmark.Data = data?.ToJsonString();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>Checks the kind and key of a wait; the engine's own kinds cannot be completed from outside.</summary>
    public static void CheckWait(string kind, string key)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || Reserved.Contains(kind))
        {
            throw new ArgumentException($"'{kind}' is not a kind of wait others can complete (1 to 100 characters, not {string.Join(", ", Reserved)}).", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            throw new ArgumentException("A wait key has 1 to 200 characters.", nameof(key));
        }
    }
}

/// <summary>What activities may ask the engine about workflows and runs (<see cref="IWorkflowDirectory"/>).</summary>
internal sealed class WorkflowDirectory(WorkflowsDbContext db) : IWorkflowDirectory
{
    /// <summary>The key as it appears in stored definitions (steps: <c>"action"</c>, flows: <c>"activity"</c>).</summary>
    public Task<bool> IsActivityUsedAsync(Guid workspaceId, string activityKey, CancellationToken cancellationToken)
    {
        var quoted = System.Text.Json.JsonSerializer.Serialize(activityKey);
        return db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Enabled
            && db.Versions.Any(v => v.WorkflowId == w.Id && v.Number == w.CurrentVersion && v.Definition.Contains(quoted)), cancellationToken);
    }

    public Task<bool> IsRunActiveAsync(Guid runId, CancellationToken cancellationToken) =>
        db.Runs.AnyAsync(r => r.Id == runId && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting), cancellationToken);
}

/// <summary>People inputs of activities (<see cref="IWorkflowRecipients"/>), read against the run's item.</summary>
internal sealed class WorkflowRecipientResolver(RecipientResolver resolver, PaperDotNet.Lists.Contracts.IListItemStore items) : IWorkflowRecipients
{
    public async Task<WorkflowRecipients> ResolveAsync(IEnumerable<string> people, WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var item = context.Item is { } target ? await items.AsSystem().GetAsync(target.WorkspaceId, target.ListId, target.ItemId, cancellationToken) : null;
        var (users, unknown) = await resolver.ResolveAsync(people, item, context.UserId, cancellationToken);
        return new WorkflowRecipients(users, unknown);
    }
}
