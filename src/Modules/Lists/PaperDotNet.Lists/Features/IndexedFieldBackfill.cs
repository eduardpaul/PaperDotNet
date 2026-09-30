using Microsoft.EntityFrameworkCore;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Fills newly indexed fields for the items that existed before (ADR-0035): lists with
/// <see cref="ListDefinition.IndexPending"/> are written batch by batch, then their fields are marked ready and queries
/// use the columns. Columns are written directly (<see cref="IItemQueries.WriteIndexColumnsAsync"/>), so versions, audit
/// and delta stay as they are. Runs every 20 seconds per tenant; an interrupted run starts over. Value rows of fields
/// that are no longer indexed stay (their field numbers are not used again).
/// </summary>
internal sealed class IndexedFieldBackfillJob(ListsDbContext db, IItemQueries queries) : ITenantRecurringJob
{
    public const string Name = "lists.indexed-fields";
    public const string Schedule = "*/20 * * * * *";
    private const int BatchSize = 500;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        foreach (var list in await context.Lists.Where(l => l.TenantId == tenant && l.IndexPending).ToListAsync(ct))
        {
            await BackfillAsync(list, cancellationToken);
        }
    }

    private async Task BackfillAsync(ListDefinition list, CancellationToken cancellationToken)
    {
        var indexed = FieldIndex.Read(list);
        var after = Guid.Empty;
        while (true)
        {
            var batch = await BatchAsync(list.TenantId, list.Id, after, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                var (columns, values) = FieldIndex.Compute(row.Fields, indexed);
                await queries.WriteIndexColumnsAsync(list.TenantId, row.Id, columns, cancellationToken);
                var item = new ListItem { Id = row.Id, TenantId = list.TenantId, ListId = list.Id };
                db.SyncValues(item, await db.ValuesOfAsync(list.TenantId, row.Id, cancellationToken), values);
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var entry in db.ChangeTracker.Entries<ItemValue>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            after = batch[^1].Id;
        }

        FieldIndex.Write(list, [.. indexed.Select(f => f with { Ready = true })]);
        list.IndexPending = false;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The list changed meanwhile (e.g. another field was indexed): the next run fills it again.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>The next items of the list (also in the recycle bin), in id order.</summary>
    private Task<List<ItemFields>> BatchAsync(Guid tenantId, Guid listId, Guid afterId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var after = afterId;
        var take = BatchSize;
        var ct = cancellationToken;
        return context.Items.AsNoTracking()
            .Where(i => i.TenantId == tenant && i.ListId == list && i.Id.CompareTo(after) > 0)
            .OrderBy(i => i.Id)
            .Select(i => new ItemFields(i.Id, i.Fields))
            .Take(take)
            .ToListAsync(ct);
    }
}
