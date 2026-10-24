using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Persistence.PostgreSql.Content;
using PaperDotNet.Persistence.Sqlite.Content;
using Xunit;

namespace PaperDotNet.Content.Tests;

public sealed class SchemaContractTests(ContentFixture fixture) : IClassFixture<ContentFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Initialization_matches_the_pinned_schema_has_all_64_EF_mappings_and_is_repeatable()
    {
        await using var db = await fixture.OpenAsync();
        await InitializeAsync(db);
        using var referenceStream = typeof(SchemaContractTests).Assembly.GetManifestResourceStream("PaperDotNet.Content.Tests.reference.json")!;
        using var reference = await JsonDocument.ParseAsync(referenceStream, cancellationToken: Ct);
        Assert.Equal(ContentDbContext.ReferenceCommit, reference.RootElement.GetProperty("commit").GetString());
        var key = ContentFixture.IsPostgres ? "postgresql" : "sqlite";
        var expected = reference.RootElement.GetProperty("schemas").GetProperty(key);
        Assert.Equal(expected.GetProperty("sha256Base64").GetString(), (await db.Set<SchemaMetadataRow>().SingleAsync(Ct)).Hash);
        Assert.Equal(64, db.Model.GetEntityTypes().Count());
        Assert.All(db.Model.GetEntityTypes(), entity =>
        {
            Assert.Null(entity.GetSchema());
            Assert.DoesNotContain(entity.GetProperties(), x => x.Name == "TenantId");
        });
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = ContentFixture.IsPostgres
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY table_name"
            : "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct)) tables.Add(reader.GetString(0));
        Assert.Equal(db.Model.GetEntityTypes().Select(x => x.GetTableName()!).Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("__EFMigrationsHistory", tables);
        Assert.Equal(1, await db.Set<TermGroupsRow>().CountAsync(Ct));
        Assert.Equal(1, await db.Set<TermSetsRow>().CountAsync(Ct));
        if (!ContentFixture.IsPostgres)
        {
            command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
            var objects = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(Ct))
                while (await reader.ReadAsync(Ct)) objects.Add(string.Join('|', Enumerable.Range(0, 4).Select(i => reader.IsDBNull(i) ? "" : reader.GetString(i))));
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', objects))));
            Assert.Equal(expected.GetProperty("catalogSha256Base64").GetString(), hash);
            command.CommandText = "PRAGMA foreign_key_check";
            await using var violations = await command.ExecuteReaderAsync(Ct);
            Assert.False(await violations.ReadAsync(Ct));
        }
        // Read seeded timestamps through the actual provider converters.
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), (await db.Set<TermGroupsRow>().SingleAsync(Ct)).CreatedAt);
    }

    [Fact]
    public async Task An_unrecognized_fingerprint_or_extra_module_table_is_rejected_without_changing_data()
    {
        await using var db = await fixture.OpenAsync();
        var metadata = await db.Set<SchemaMetadataRow>().SingleAsync(Ct);
        var original = metadata.Hash;
        metadata.Hash = "unrecognized";
        await db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitializeAsync(db));
        Assert.Equal("unrecognized", (await db.Set<SchemaMetadataRow>().AsNoTracking().SingleAsync(Ct)).Hash);
        metadata.Hash = original;
        await db.SaveChangesAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE legacy_module_marker (id INTEGER PRIMARY KEY)", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitializeAsync(db));
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM legacy_module_marker";
        Assert.Equal(0, Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture));
        Assert.Equal(1, await db.Set<TermGroupsRow>().CountAsync(Ct));
    }

    private static Task InitializeAsync(ContentDbContext db) => db is SqliteContentDbContext sqlite
        ? sqlite.InitializeAsync(Ct) : ((PostgreSqlContentDbContext)db).InitializeAsync(Ct);
}
