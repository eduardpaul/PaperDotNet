using Npgsql;

namespace PaperDotNet.Persistence.PostgreSql.Content;

/// <summary>One physical PaperGo organization database per tenant, owned by the application role.</summary>
public static class PostgreSqlOrganizationDatabase
{
    public static async Task<bool> ExistsAsync(string applicationConnection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(applicationConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)";
        command.Parameters.AddWithValue("name", "pdn_content_" + tenantId.ToString("N"));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
    public static async Task<string> ProvisionAsync(string applicationConnection, string provisioningConnection, Guid tenantId, CancellationToken cancellationToken)
    {
        var target = new NpgsqlConnectionStringBuilder(applicationConnection) { Database = "pdn_content_" + tenantId.ToString("N") };
        await using var connection = new NpgsqlConnection(provisioningConnection);
        await connection.OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)";
        exists.Parameters.AddWithValue("name", target.Database);
        if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
        {
            using var quotes = new NpgsqlCommandBuilder();
            await using var create = connection.CreateCommand();
            create.CommandText = "CREATE DATABASE " + quotes.QuoteIdentifier(target.Database) + " OWNER " + quotes.QuoteIdentifier(target.Username!);
            try { await create.ExecuteNonQueryAsync(cancellationToken); }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase) { }
        }
        return target.ConnectionString;
    }
}
