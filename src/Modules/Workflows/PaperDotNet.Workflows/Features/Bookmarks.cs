using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Messaging;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Completes waits of runs for other modules and extensions (<see cref="IWorkflowBookmarks"/>, ADR-0036). The bookmark and
/// the message that resumes its run are saved together. A completion that arrives before the run has saved its wait is
/// kept as a bookmark without a run (<see cref="Unclaimed"/>), which the run takes over when it starts waiting.
/// </summary>
internal sealed class WorkflowBookmarks(WorkflowsDbContext db, RunService runs, IOutbox outbox, TimeProvider time) : IWorkflowBookmarks
{
    /// <summary>The run id of a completion that no run waits for yet.</summary>
    public static readonly Guid Unclaimed = Guid.Empty;

    private static readonly string[] Reserved = [BookmarkKinds.Approval, BookmarkKinds.Delay, BookmarkKinds.Retry];

    public async Task<bool> CompleteAsync(string kind, string key, JsonObject? payload, CancellationToken cancellationToken)
    {
        CheckWait(kind, key);
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
