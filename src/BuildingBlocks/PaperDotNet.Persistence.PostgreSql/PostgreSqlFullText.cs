using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NpgsqlTypes;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>
/// Full-text search on PostgreSQL: a stored, weighted <c>tsvector</c> column with a GIN index,
/// queried with <c>to_tsquery</c> and ranked with <c>ts_rank</c>. The vector holds the exact words
/// (<c>simple</c>) and, for rows with a language (SRC-05), their stems in that language; queries
/// match either, so "invoices" finds "invoice" in English documents. Punctuation becomes spaces
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
        if (entityType.FindAnnotation(FullTextExtensions.LanguageAnnotation) is null)
        {
            return Query(db, source, id!, vector, "to_tsquery('simple', {0})", [Render(query)]);
        }

        var (tsquery, parameters) = RenderWithLanguages(query);
        return Query(db, source, id!, vector, tsquery, parameters);
    }

    private static IQueryable<FullTextMatch> Query(DbContext db, string source, string id, string vector, string tsquery, object[] parameters)
    {
        var sql = $"SELECT d.{Quote(id)} AS \"{FullTextMatch.IdColumn}\", ts_rank(d.{Quote(vector)}, t.q)::float8 AS \"{FullTextMatch.RankColumn}\" " +
                  $"FROM {source} AS d, (SELECT {tsquery} AS q) AS t WHERE d.{Quote(vector)} @@ t.q";
        return db.Set<FullTextMatch>().FromSqlRaw(sql, parameters);
    }

    /// <summary>
    /// SQL building a <c>tsquery</c> in which every term matches its exact form or its stem in any
    /// language (SRC-05); AND, OR and NOT then apply to those alternatives, so an excluded word stays
    /// excluded in every form. Terms are parameters.
    /// </summary>
    internal static (string Sql, object[] Parameters) RenderWithLanguages(FullTextQuery query)
    {
        var parameters = new List<object>();
        string TermSql(FullTextTerm term)
        {
            parameters.Add(Term(term));
            var index = parameters.Count - 1;
            return $"({string.Join(" || ", FullTextLanguages.All.Prepend("simple").Select(c => $"to_tsquery('{c}', {{{index}}})"))})";
        }

        var groups = query.Groups.Select(g => $"({string.Join(" || ", g.Select(TermSql))})").ToList();
        var excluded = query.Excluded.Select(t => $"!!{TermSql(t)}").ToList();
        return (string.Join(" && ", groups.Concat(excluded)), [.. parameters]);
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
            string Vector(string configuration) => string.Join(" || ", properties.Select((p, i) =>
                $"setweight(to_tsvector('{configuration}', regexp_replace(coalesce({Quote(entityType.FindProperty(p)!.GetColumnName(table)!)}, ''), '[[:punct:][:space:]]+', ' ', 'g')), '{Weights[Math.Min(i, Weights.Length - 1)]}')"));

            var expression = Vector("simple");
            if (entityType.FindAnnotation(FullTextExtensions.LanguageAnnotation)?.Value is string languageProperty)
            {
                var language = Quote(entityType.FindProperty(languageProperty)!.GetColumnName(table)!);
                var cases = string.Concat(FullTextLanguages.All.Select(l => $" WHEN '{l}' THEN {Vector(l)}"));
                expression = $"{expression} || (CASE {language}{cases} ELSE ''::tsvector END)";
            }
            var entity = modelBuilder.Entity(entityType.ClrType);
            entity.Property<NpgsqlTsVector>(VectorProperty).HasComputedColumnSql(expression, stored: true);
            entity.HasIndex(VectorProperty).HasMethod("gin");
        }
    }

    private static string Quote(string identifier) => string.Create(CultureInfo.InvariantCulture, $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
}
