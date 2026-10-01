using System.Text;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Collaboration.Data;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Collaboration.Features;

/// <summary>Makes comments findable: their text is part of the item's search document (the endpoints index the item again).</summary>
internal sealed class CommentSearchContent(CollaborationDbContext db) : IItemSearchContributor
{
    private const int MaxCharacters = 20_000;

    public async Task<IReadOnlyDictionary<Guid, ItemSearchContent>> GetContentAsync(Guid tenantId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ItemSearchContent>();
        foreach (var itemId in itemIds.Distinct())
        {
            var context = db;
            var tenant = tenantId;
            var id = itemId;
            var ct = cancellationToken;
            var comments = await context.Comments.AsNoTracking().Where(c => c.TenantId == tenant && c.ItemId == id).OrderBy(c => c.Id).Select(c => c.Text).ToListAsync(ct);
            if (comments.Count == 0)
            {
                continue;
            }

            var text = new StringBuilder();
            foreach (var comment in comments.TakeWhile(_ => text.Length < MaxCharacters))
            {
                text.AppendLine(comment);
            }

            result[itemId] = new ItemSearchContent(text.ToString(), null);
        }

        return result;
    }
}
