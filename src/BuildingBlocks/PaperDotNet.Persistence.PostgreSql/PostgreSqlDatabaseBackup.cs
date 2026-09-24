using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>The connection string as configured (the data source hides the password).</summary>
internal sealed record PostgreSqlConnection(string ConnectionString);

/// <summary>
/// Snapshots with <c>pg_dump</c> (custom format, no owners or privileges, so a backup restores into
/// another database and role) and <c>pg_restore</c>. The client tools must match the server's major
/// version or be newer (<c>Database:PgDumpPath</c>, <c>Database:PgRestorePath</c>).
/// </summary>
internal sealed class PostgreSqlDatabaseBackup(PostgreSqlConnection connection, IConfiguration configuration) : IDatabaseBackup
{
    public string FileName => "database.pgdump";

    public Task BackupAsync(string file, CancellationToken cancellationToken) =>
        RunAsync(configuration["Database:PgDumpPath"] ?? "pg_dump",
            ["--format=custom", "--no-owner", "--no-privileges", "--enable-row-security", $"--file={file}"], cancellationToken);

    public Task RestoreAsync(string file, CancellationToken cancellationToken) =>
        RunAsync(configuration["Database:PgRestorePath"] ?? "pg_restore",
            ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--enable-row-security", "--single-transaction", "--exit-on-error", $"--dbname={Database()}", file],
            cancellationToken);

    private string Database() => new NpgsqlConnectionStringBuilder(connection.ConnectionString).Database
        ?? throw new InvalidOperationException("The PostgreSQL connection string has no database.");

    /// <summary>Runs a client tool with the connection in libpq environment variables (no password on the command line).</summary>
    private async Task RunAsync(string tool, string[] arguments, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(tool)
                .WithArguments(arguments)
                .WithEnvironmentVariables(e => e
                    .Set("PGHOST", builder.Host)
                    .Set("PGPORT", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Set("PGUSER", builder.Username)
                    .Set("PGPASSWORD", builder.Password)
                    .Set("PGDATABASE", builder.Database)
                    // Row-level security is forced for the owner too; maintenance sessions see all tenants.
                    .Set("PGOPTIONS", $"-c {PostgreSqlRowLevelSecurity.MaintenanceSetting}=on"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"'{tool}' was not found. Install the PostgreSQL client tools (same major version as the server or newer).", ex);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} failed: {result.StandardError.Trim()}");
        }
    }
}
