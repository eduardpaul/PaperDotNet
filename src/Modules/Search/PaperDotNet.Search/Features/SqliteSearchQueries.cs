using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Search.Data;

namespace PaperDotNet.Search.Features;

/// <summary>
/// Which documents a search looks at: the tenant, what the caller may read (null: everything, for system callers) and
/// the filters. <see cref="Terms"/> holds a term with its descendants (any of them matches); <see cref="Ids"/> limits the
/// search to these documents (semantic candidates).
/// </summary>
internal sealed record SearchFilter(
    Guid TenantId,
    ReadableScopes? Access,
    Guid? WorkspaceId = null,
    Guid? ContainerId = null,
    Guid? ContentTypeId = null,
    IReadOnlyCollection<Guid>? Terms = null,
    Guid? CreatedBy = null,
    long? UpdatedFrom = null,
    long? UpdatedTo = null,
    IReadOnlyCollection<Guid>? Ids = null);

internal sealed record SearchRow(
    Guid Id, string SourceType, Guid WorkspaceId, Guid? ContainerId, Guid? ContentTypeId, string Title, string Body, Guid? CreatedBy, long UpdatedAt, double Rank);

internal sealed record PassageRow(Guid DocumentId, int Ordinal, int? Page, string Text, double Rank);

internal sealed record SearchPage(IReadOnlyList<SearchRow> Rows, int Count, SearchFacets? Facets);

/// <summary>
/// Search SQL for one database provider (ADR-0039: EF Core cannot precompile full-text queries or dynamic filters):
/// matching, ranking, facets and the bulk changes of the index.
/// </summary>
internal interface ISearchQueries
{
    /// <summary>
    /// Documents matching <paramref name="match"/> (FTS5 syntax; null: filters only, newest first) and the filter,
    /// best first, with the total and, when asked, the facets.
    /// </summary>
    Task<SearchPage> SearchAsync(SearchFilter filter, string? match, int top, int skip, bool facets, CancellationToken cancellationToken);

    /// <summary>The passages of <paramref name="documentIds"/> matching <paramref name="match"/>, with their rank.</summary>
    Task<IReadOnlyList<PassageRow>> PassagesAsync(Guid tenantId, string match, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);

    /// <summary>The passages with <paramref name="passageIds"/> (semantic matches; rank 0).</summary>
    Task<IReadOnlyList<PassageRow>> PassagesByIdAsync(Guid tenantId, IReadOnlyCollection<Guid> passageIds, CancellationToken cancellationToken);

    /// <summary>Removes documents (with their tags and passages): by id, container or source type.</summary>
    Task DeleteAsync(Guid tenantId, IReadOnlyCollection<Guid>? ids, Guid? containerId, string? sourceType, CancellationToken cancellationToken);

    /// <summary>Moves the documents with <paramref name="ids"/> to <paramref name="scopeId"/>.</summary>
    Task SetScopeAsync(Guid tenantId, Guid scopeId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The number of documents tagged with each term.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountTermsAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken);
}

/// <summary>
/// SQLite: FTS5 tables over documents (title, keywords, body; ranked with <c>bm25</c> weights 10, 4, 1) and passages,
/// kept in sync by triggers (see the Search migration). Sets of ids are one JSON parameter (<c>json_each</c>), so the
/// statement does not grow with them.
/// </summary>
internal sealed class SqliteSearchQueries(SearchDbContext db) : ISearchQueries
{
    private const int FacetSize = 20;
    private const string Fts = "\"search_documents_fts\"";
    private const string PassageFts = "\"search_passages_fts\"";

    public async Task<SearchPage> SearchAsync(SearchFilter filter, string? match, int top, int skip, bool facets, CancellationToken cancellationToken)
    {
        return await RunAsync(async command =>
        {
            var sql = new Sql(command);
            var (from, where) = Documents(sql, filter, match);
            command.CommandText = $"SELECT COUNT(*) FROM {from} WHERE {where}";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            var rank = match is null ? "0.0" : $"-bm25({Fts}, 10.0, 4.0, 1.0)";
            var order = match is null ? "\"d\".\"UpdatedAt\" DESC, \"d\".\"Id\"" : "6 DESC, \"d\".\"Id\"";
            command.CommandText =
                $"SELECT \"d\".\"Id\", \"d\".\"SourceType\", \"d\".\"WorkspaceId\", \"d\".\"ContainerId\", \"d\".\"ContentTypeId\", {rank}, "
                + $"\"d\".\"Title\", \"d\".\"Body\", \"d\".\"CreatedBy\", \"d\".\"UpdatedAt\" FROM {from} WHERE {where} "
                + $"ORDER BY {order} LIMIT {sql.Parameter(top)} OFFSET {sql.Parameter(skip)}";
            var rows = new List<SearchRow>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new SearchRow(
                        GuidOf(reader, 0)!.Value, reader.GetString(1), GuidOf(reader, 2)!.Value, GuidOf(reader, 3), GuidOf(reader, 4), reader.GetString(6),
                        reader.GetString(7), GuidOf(reader, 8), reader.GetInt64(9), reader.GetDouble(5)));
                }
            }

            if (!facets)
            {
                return new SearchPage(rows, count, null);
            }

            async Task<List<FacetValue>> FacetAsync(string sqlText)
            {
                command.CommandText = sqlText;
                var values = new List<FacetValue>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    values.Add(new FacetValue(GuidOf(reader, 0)!.Value, reader.GetInt32(1)));
                }

                return values;
            }

            var limit = sql.Parameter(FacetSize);
            string Facet(string column) =>
                $"SELECT \"d\".\"{column}\", COUNT(*) FROM {from} WHERE {where} AND \"d\".\"{column}\" IS NOT NULL GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT {limit}";
            return new SearchPage(rows, count, new SearchFacets(
                await FacetAsync(Facet("WorkspaceId")),
                await FacetAsync(Facet("ContainerId")),
                await FacetAsync(Facet("ContentTypeId")),
                await FacetAsync(
                    $"SELECT \"t\".\"TermId\", COUNT(*) FROM \"search_tags\" AS \"t\" WHERE \"t\".\"TenantId\" = {sql.Parameter(GuidText(filter.TenantId))} "
                    + $"AND \"t\".\"DocumentId\" IN (SELECT \"d\".\"Id\" FROM {from} WHERE {where}) GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT {limit}")));
        }, cancellationToken);
    }

    /// <summary>The FROM and WHERE of documents matching the filter (and the full-text query).</summary>
    private static (string From, string Where) Documents(Sql sql, SearchFilter filter, string? match)
    {
        var where = new StringBuilder($"\"d\".\"TenantId\" = {sql.Parameter(GuidText(filter.TenantId))}");
        var from = "\"search_documents\" AS \"d\"";
        if (match is not null)
        {
            from = $"{Fts} JOIN \"search_documents\" AS \"d\" ON \"d\".\"Key\" = {Fts}.\"rowid\"";
            where.Append($" AND {Fts} MATCH {sql.Parameter(match)}");
        }

        if (filter.Access is { } access)
        {
            // Managed workspaces in full; elsewhere the scopes the caller's principals reach, in workspaces they belong to.
            where.Append($" AND (\"d\".\"WorkspaceId\" IN (SELECT \"value\" FROM json_each({sql.Ids(access.FullWorkspaces)})) OR ("
                + $"\"d\".\"WorkspaceId\" IN (SELECT \"value\" FROM json_each({sql.Ids(access.Workspaces)})) "
                + $"AND \"d\".\"ScopeId\" IN (SELECT \"value\" FROM json_each({sql.Ids(access.Scopes)}))))");
        }

        void Equal(string column, Guid? value)
        {
            if (value is { } id)
            {
                where.Append($" AND \"d\".\"{column}\" = {sql.Parameter(GuidText(id))}");
            }
        }

        Equal("WorkspaceId", filter.WorkspaceId);
        Equal("ContainerId", filter.ContainerId);
        Equal("ContentTypeId", filter.ContentTypeId);
        Equal("CreatedBy", filter.CreatedBy);
        if (filter.UpdatedFrom is { } updatedFrom)
        {
            where.Append($" AND \"d\".\"UpdatedAt\" >= {sql.Parameter(updatedFrom)}");
        }

        if (filter.UpdatedTo is { } updatedTo)
        {
            where.Append($" AND \"d\".\"UpdatedAt\" < {sql.Parameter(updatedTo)}");
        }

        if (filter.Ids is { } ids)
        {
            where.Append($" AND \"d\".\"Id\" IN (SELECT \"value\" FROM json_each({sql.Ids(ids)}))");
        }

        if (filter.Terms is { } terms)
        {
            where.Append($" AND EXISTS (SELECT 1 FROM \"search_tags\" AS \"t\" WHERE \"t\".\"DocumentId\" = \"d\".\"Id\" "
                + $"AND \"t\".\"TermId\" IN (SELECT \"value\" FROM json_each({sql.Ids(terms)})))");
        }

        return (from, where.ToString());
    }

    public async Task<IReadOnlyList<PassageRow>> PassagesAsync(Guid tenantId, string match, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        return await RunAsync<IReadOnlyList<PassageRow>>(async command =>
        {
            var sql = new Sql(command);
            command.CommandText =
                $"SELECT \"p\".\"DocumentId\", \"p\".\"Ordinal\", \"p\".\"Page\", \"p\".\"Text\", -bm25({PassageFts}) "
                + $"FROM {PassageFts} JOIN \"search_passages\" AS \"p\" ON \"p\".\"Key\" = {PassageFts}.\"rowid\" "
                + $"WHERE {PassageFts} MATCH {sql.Parameter(match)} AND \"p\".\"TenantId\" = {sql.Parameter(GuidText(tenantId))} "
                + $"AND \"p\".\"DocumentId\" IN (SELECT \"value\" FROM json_each({sql.Ids(documentIds)}))";
            var rows = new List<PassageRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PassageRow(GuidOf(reader, 0)!.Value, reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3), reader.GetDouble(4)));
            }

            return rows;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<PassageRow>> PassagesByIdAsync(Guid tenantId, IReadOnlyCollection<Guid> passageIds, CancellationToken cancellationToken)
    {
        if (passageIds.Count == 0)
        {
            return [];
        }

        return await RunAsync<IReadOnlyList<PassageRow>>(async command =>
        {
            var sql = new Sql(command);
            command.CommandText =
                "SELECT \"DocumentId\", \"Ordinal\", \"Page\", \"Text\" FROM \"search_passages\" "
                + $"WHERE \"TenantId\" = {sql.Parameter(GuidText(tenantId))} AND \"Id\" IN (SELECT \"value\" FROM json_each({sql.Ids(passageIds)}))";
            var rows = new List<PassageRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PassageRow(GuidOf(reader, 0)!.Value, reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3), 0));
            }

            return rows;
        }, cancellationToken);
    }

    public Task DeleteAsync(Guid tenantId, IReadOnlyCollection<Guid>? ids, Guid? containerId, string? sourceType, CancellationToken cancellationToken) =>
        RunAsync(async command =>
        {
            var sql = new Sql(command);
            var tenant = sql.Parameter(GuidText(tenantId));
            var documents = $"\"TenantId\" = {tenant}"
                + (ids is null ? "" : $" AND \"Id\" IN (SELECT \"value\" FROM json_each({sql.Ids(ids)}))")
                + (containerId is { } container ? $" AND \"ContainerId\" = {sql.Parameter(GuidText(container))}" : "")
                + (sourceType is null ? "" : $" AND \"SourceType\" = {sql.Parameter(sourceType)}");
            command.CommandText =
                $"DELETE FROM \"search_passages\" WHERE \"TenantId\" = {tenant} AND \"DocumentId\" IN (SELECT \"Id\" FROM \"search_documents\" WHERE {documents});"
                + $"DELETE FROM \"search_tags\" WHERE \"TenantId\" = {tenant} AND \"DocumentId\" IN (SELECT \"Id\" FROM \"search_documents\" WHERE {documents});"
                + $"DELETE FROM \"search_documents\" WHERE {documents};";
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task SetScopeAsync(Guid tenantId, Guid scopeId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        RunAsync(async command =>
        {
            var sql = new Sql(command);
            var scope = sql.Parameter(GuidText(scopeId));
            command.CommandText =
                $"UPDATE \"search_documents\" SET \"ScopeId\" = {scope} WHERE \"TenantId\" = {sql.Parameter(GuidText(tenantId))} "
                + $"AND \"Id\" IN (SELECT \"value\" FROM json_each({sql.Ids(ids)})) AND \"ScopeId\" <> {scope}";
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountTermsAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        if (termIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        return await RunAsync<IReadOnlyDictionary<Guid, int>>(async command =>
        {
            var sql = new Sql(command);
            command.CommandText =
                $"SELECT \"TermId\", COUNT(*) FROM \"search_tags\" WHERE \"TenantId\" = {sql.Parameter(GuidText(tenantId))} "
                + $"AND \"TermId\" IN (SELECT \"value\" FROM json_each({sql.Ids(termIds)})) GROUP BY 1";
            var counts = new Dictionary<Guid, int>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts[GuidOf(reader, 0)!.Value] = reader.GetInt32(1);
            }

            return counts;
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<DbCommand, Task<T>> run, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            return await run(command);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>GUIDs as EF Core stores them on SQLite (upper-case text).</summary>
    private static string GuidText(Guid value) => value.ToString("D").ToUpperInvariant();

    private static Guid? GuidOf(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private sealed class Sql(DbCommand command)
    {
        public string Parameter(object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{command.Parameters.Count}";
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
            return parameter.ParameterName;
        }

        /// <summary>A set of ids as one JSON array parameter, read with <c>json_each</c>.</summary>
        public string Ids(IEnumerable<Guid> ids) => Parameter("[" + string.Join(',', ids.Select(id => $"\"{GuidText(id)}\"")) + "]");
    }
}
