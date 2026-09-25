using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Workspaces.Features;

/// <summary>Removes a deleted user's workspace memberships (IAM-14).</summary>
internal sealed class PrincipalDeletedSubscriber(WorkspacesDbContext db) : IEventSubscriber<PrincipalDeleted>
{
    public async Task HandleAsync(PrincipalDeleted integrationEvent, CancellationToken cancellationToken)
    {
        if (!integrationEvent.IsGroup)
        {
            await db.Members.Where(m => m.UserId == integrationEvent.PrincipalId).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
