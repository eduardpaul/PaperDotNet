using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>
/// Prepares every SQLite connection: WAL journaling and a busy timeout for
/// concurrent requests, foreign keys, and the <c>pdn_json_contains</c> function
/// used to translate <see cref="JsonFunctions.Contains"/>.
/// </summary>
internal sealed class SqliteConnectionSetup : DbConnectionInterceptor
{
    public const string ContainsFunction = "pdn_json_contains";

    public static readonly SqliteConnectionSetup Instance = new();

    private const string Pragmas = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Prepare(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Prepare(connection);
        return Task.CompletedTask;
    }

    internal static void Prepare(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        using (var command = sqlite.CreateCommand())
        {
            command.CommandText = Pragmas;
            command.ExecuteNonQuery();
        }

        sqlite.CreateFunction<string?, string?, bool>(
            ContainsFunction,
            (document, fragment) => document is not null && fragment is not null && JsonContainment.Contains(JsonNode.Parse(document), JsonNode.Parse(fragment)),
            isDeterministic: true);
    }
}

/// <summary>PostgreSQL <c>jsonb @&gt;</c> semantics in C#.</summary>
internal static class JsonContainment
{
    public static bool Contains(JsonNode? document, JsonNode? fragment) => (document, fragment) switch
    {
        (JsonObject obj, JsonObject part) => part.All(p => obj.TryGetPropertyValue(p.Key, out var value) && Contains(value, p.Value)),
        (JsonArray array, JsonArray part) => part.All(x => array.Any(y => Contains(y, x))),
        (JsonArray array, JsonValue scalar) => array.Any(y => Contains(y, scalar)),
        (JsonValue left, JsonValue right) => JsonNode.DeepEquals(left, right)
            || (left.TryGetValue<decimal>(out var a) && right.TryGetValue<decimal>(out var b) && a == b),
        (null, null) => true,
        _ => false,
    };
}
