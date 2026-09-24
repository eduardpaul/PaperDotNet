using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NpgsqlTypes;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>
/// Full-text search on PostgreSQL: a stored, weighted <c>tsvector</c> column
/// (<c>simple</c> configuration, no stemming yet: SRC-05) with a GIN index, queried
/// with <c>to_tsquery</c> and ranked with <c>ts_rank</c>. Punctuation becomes spaces
/// first, so words split like FTS5 and <see cref="FullTextQuery.Tokenize"/> (e.g. <c>INV-2026</c>
/// is <c>inv 2026</c>, not a signed number).
/// </summary>
internal sealed class PostgreSqlFullTextSearch : IFullTextSearch
{
    public const string VectorProperty = "SearchVector";
    private static readonly string[] Weights = ["A", "B", "C", "D"];

    public IQueryable<FullTextMatch> Match<TEntity>(DbContext db, FullTextQuery query)
        where TEntity : class
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped.");
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var id = entityType.FindProperty("Id")!.GetColumnName(table);
        var vector = entityType.FindProperty(VectorProperty)?.GetColumnName(table)
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no full-text index.");
        var source = table.Schema is null ? Quote(table.Name) : $"{Quote(table.Schema)}.{Quote(table.Name)}";
        var sql = $"SELECT d.{Quote(id!)} AS \"{FullTextMatch.IdColumn}\", ts_rank(d.{Quote(vector)}, q)::float8 AS \"{FullTextMatch.RankColumn}\" " +
                  $"FROM {source} AS d, to_tsquery('simple', {{0}}) AS q WHERE d.{Quote(vector)} @@ q";
        return db.Set<FullTextMatch>().FromSqlRaw(sql, Render(query));
    }

    /// <summary>Renders the query as a <c>tsquery</c>: <c>&amp;</c>, <c>|</c>, <c>!</c>, phrases with <c>&lt;-&gt;</c>, prefixes with <c>:*</c>.</summary>
    internal static string Render(FullTextQuery query)
    {
        var parts = query.Groups.Select(g => g.Count == 1 ? Term(g[0]) : $"({string.Join(" | ", g.Select(Term))})")
            .Concat(query.Excluded.Select(t => $"!{Term(t)}"));
        return string.Join(" & ", parts);
    }

    private static string Term(FullTextTerm term)
    {
        var tokens = term.Tokens.Select((t, i) => term.Prefix && i == term.Tokens.Count - 1 ? $"'{t}':*" : $"'{t}'");
        return term.Tokens.Count == 1 ? tokens.First() : $"({string.Join(" <-> ", tokens)})";
    }

    /// <summary>Adds the generated vector column and its GIN index to entities with a full-text annotation.</summary>
    internal static void Customize(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => t.FindAnnotation(FullTextExtensions.Annotation) is not null).ToList())
        {
            var properties = ((string)entityType.FindAnnotation(FullTextExtensions.Annotation)!.Value!).Split(',');
            var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
            var expression = string.Join(" || ", properties.Select((p, i) =>
                $"setweight(to_tsvector('simple', regexp_replace(coalesce({Quote(entityType.FindProperty(p)!.GetColumnName(table)!)}, ''), '[[:punct:][:space:]]+', ' ', 'g')), '{Weights[Math.Min(i, Weights.Length - 1)]}')"));
            var entity = modelBuilder.Entity(entityType.ClrType);
            entity.Property<NpgsqlTsVector>(VectorProperty).HasComputedColumnSql(expression, stored: true);
            entity.HasIndex(VectorProperty).HasMethod("gin");
        }
    }

    private static string Quote(string identifier) => string.Create(CultureInfo.InvariantCulture, $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
}
