using Microsoft.EntityFrameworkCore;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Fills newly indexed fields for the items that existed before (ADR-0035): lists with
/// <see cref="ListDefinition.IndexPending"/> are written batch by batch, then their fields are marked ready and queries
/// use the columns. Items are updated directly (not through the change tracker), so versions, audit and delta stay as
/// they are. Runs every 20 seconds per tenant; an interrupted run starts over.
/// </summary>
internal sealed class IndexedFieldBackfillJob(ListsDbContext db) : ITenantRecurringJob
{
    public const string Name = "lists.indexed-fields";
    public const string Schedule = "*/20 * * * * *";
    private const int BatchSize = 500;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var list in await db.Lists.Where(l => l.IndexPending).ToListAsync(cancellationToken))
        {
            await BackfillAsync(list, cancellationToken);
        }
    }

    public async Task BackfillAsync(ListDefinition list, CancellationToken ct)
    {
        var indexed = list.IndexedFields.ToList();
        var numbers = indexed.Where(f => f.ValueField is not null).Select(f => f.ValueField!.Value).ToArray();
        var lists = new Dictionary<Guid, ListDefinition> { [list.Id] = list };
        Guid? after = null;
        while (true)
        {
            var batch = await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking()
                .Where(i => i.ListId == list.Id && (after == null || i.Id.CompareTo(after.Value) > 0))
                .OrderBy(i => i.Id)
                .Select(i => new { i.Id, i.ListId, i.Fields })
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var desired = new Dictionary<Guid, (ListItem Item, Dictionary<short, HashSet<Guid>> Values)>();
            foreach (var row in batch)
            {
                var (columns, values) = FieldIndex.Compute(row.Fields, indexed);
                if (columns.Count > 0)
                {
                    var id = row.Id;
                    await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(i => i.Id == id).ExecuteUpdateAsync(u =>
                    {
                        foreach (var (column, value) in columns)
                        {
                            if (value is double number)
                            {
                                u.SetProperty(i => EF.Property<double?>(i, column), number);
                            }
                            else if (column.StartsWith(nameof(IndexKind.Number), StringComparison.Ordinal))
                            {
                                u.SetProperty(i => EF.Property<double?>(i, column), (double?)null);
                            }
                            else
                            {
                                u.SetProperty(i => EF.Property<string?>(i, column), value as string);
                            }
                        }
                    }, ct);
                }

                desired[row.Id] = (new ListItem { Id = row.Id, ListId = row.ListId, Title = string.Empty }, values);
            }

            await db.SyncValuesAsync([.. desired.Values.Select(d => d.Item)], desired, lists, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            db.ChangeTracker.Entries<ItemValue>().ToList().ForEach(e => e.State = EntityState.Detached);
            after = batch[^1].Id;
        }

        // Rows of fields that are no longer indexed.
        await db.ItemValues.Where(v => v.ListId == list.Id && !EF.Parameter(numbers).Contains(v.Field)).ExecuteDeleteAsync(ct);

        list.IndexedFields = [.. indexed.Select(f => new IndexedField { Field = f.Field, Kind = f.Kind, Column = f.Column, ValueField = f.ValueField, Ready = true })];
        list.IndexPending = false;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The list changed meanwhile (e.g. another field was indexed): the next run fills it again.
            db.ChangeTracker.Clear();
        }
    }
}
