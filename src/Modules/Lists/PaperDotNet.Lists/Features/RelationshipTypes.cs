using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Reuses taxonomy identity, synonyms, translations and open-set term creation for graph predicates.</summary>
internal sealed class RelationshipTypes(ListsDbContext db, ITermStore terms, ITermSetProvisioning provisioning)
{
    public const string Group = "Relationships";
    public const string Set = "Types";

    private async Task<Guid> SetAsync(CancellationToken ct) =>
        (await terms.FindTermSetAsync(Group, Set, ct)) ?? (await provisioning.EnsureAsync(
            new TermSetTemplate(Group, Set, [], "Predicates for global item relationships.", IsOpen: true) { Key = "lists.relationshipTypes" }, ct)).TermSetId;

    public async Task<Guid?> ResolveAsync(string? value, bool create, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Guid.Empty;
        return await terms.ResolveAsync(await SetAsync(ct), value, create, ct);
    }

    public async Task<IReadOnlyList<RelationshipTypeData>> ListAsync(CancellationToken ct)
    {
        var vocabulary = await terms.ListTermsAsync(await SetAsync(ct), ct);
        var definitions = await db.RelationshipTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        return vocabulary.Select(t => Data(t.Id, t.Name, definitions.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, RelationshipTypeData>> DescribeAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var vocabulary = await terms.GetTermsAsync(ids, ct);
        var definitions = await db.RelationshipTypes.AsNoTracking().Where(t => EF.Parameter(ids).Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        return vocabulary.ToDictionary(t => t.Id, t => Data(t.Id, t.Name, definitions.GetValueOrDefault(t.Id)));
    }

    public async Task<RelationshipTypeData> EnsureAsync(RelationshipTypeOptions options, CancellationToken ct)
    {
        options = options with { Name = options.Name?.Trim() ?? "", InverseLabel = string.IsNullOrWhiteSpace(options.InverseLabel) ? null : options.InverseLabel.Trim() };
        Validate(options);
        var id = await ResolveAsync(options.Name, true, ct) ?? throw new ArgumentException("The relationship type is unknown, ambiguous or deprecated.");
        var definition = await db.RelationshipTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (definition is null)
        {
            if (await db.Relations.AnyAsync(r => r.TypeId == id && r.Directed != options.Directed, ct))
                throw new ArgumentException("This predicate already has relationships with a different direction.");
            definition = new ItemRelationshipType { Id = id, Directed = options.Directed, InverseLabel = options.InverseLabel?.Trim(), MaxIncoming = options.MaxIncoming, MaxOutgoing = options.MaxOutgoing };
            db.RelationshipTypes.Add(definition);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.Entry(definition).State = EntityState.Detached;
                definition = await db.RelationshipTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (definition is null) throw;
            }
        }
        if (definition.Directed != options.Directed || definition.InverseLabel != options.InverseLabel?.Trim()
            || definition.MaxIncoming != options.MaxIncoming || definition.MaxOutgoing != options.MaxOutgoing)
            throw new ArgumentException("This type already has different behavior. Choose a new type rather than changing existing graph semantics.");
        var term = (await terms.GetTermsAsync([id], ct)).Single();
        return Data(id, term.Name, definition);
    }

    internal static void Validate(RelationshipTypeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name) || options.Name.Length > 256 || options.InverseLabel?.Length > 256
            || options.MaxIncoming is < 1 or > 1000 || options.MaxOutgoing is < 1 or > 1000
            || (!options.Directed && (options.MaxIncoming is not null || options.MaxOutgoing is not null || options.InverseLabel is not null)))
            throw new ArgumentException("Provide a name of at most 256 characters; inverse labels and limits (1–1000) require a directed type.");
    }

    private static RelationshipTypeData Data(Guid id, string name, ItemRelationshipType? definition) =>
        new(id, name, definition?.Directed ?? false, definition?.InverseLabel, definition?.MaxIncoming, definition?.MaxOutgoing);
}
