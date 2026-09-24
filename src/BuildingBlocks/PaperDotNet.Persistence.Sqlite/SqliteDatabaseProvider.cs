using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PaperDotNet.Persistence.Sqlite;

internal sealed class SqliteDatabaseProvider(SqliteDatabaseSettings settings) : IDatabaseProvider
{
    public const string ProviderName = "Sqlite";

    /// <summary>Assembly holding the SQLite migrations of every module.</summary>
    public const string MigrationsAssembly = "PaperDotNet.Migrations.Sqlite";

    public string Name => ProviderName;

    public void Configure(DbContextOptionsBuilder options, string schema) =>
        Configure(options, settings.ConnectionString, schema);

    public static void Configure(DbContextOptionsBuilder options, string connectionString, string schema)
    {
        options
            .UseSqlite(connectionString, sqlite => sqlite
                .MigrationsAssembly(MigrationsAssembly)
                .MigrationsHistoryTable($"__ef_migrations_history_{schema}"))
            .UseSnakeCaseNamingConvention()
            .AddMethodTranslator<SqliteJsonTranslatorPlugin>()
            .AddInterceptors(SqliteConnectionSetup.Instance)
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>();
    }
}

/// <summary>
/// SQLite adjustments of the provider-neutral model:
/// - no schemas: module tables get the schema as prefix (<c>lists_items</c>);
/// - <see cref="DateTimeOffset"/> stored as sortable integers (UTC ticks), so
///   dates can be compared and ordered;
/// - JSON containment indexes are dropped (no GIN equivalent).
/// </summary>
internal sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        var schema = modelBuilder.Model.GetDefaultSchema();
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList())
        {
            if (entityType.GetTableName() is { } table && (entityType.GetSchema() ?? schema) is { } entitySchema)
            {
                entityType.SetTableName($"{entitySchema}_{table}");
                entityType.SetSchema(null);
            }

            foreach (var property in entityType.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }

            foreach (var index in entityType.GetIndexes().Where(i => i.FindAnnotation(JsonDocumentExtensions.ContainmentIndexAnnotation) is not null).ToList())
            {
                entityType.RemoveIndex(index);
            }
        }

        modelBuilder.Model.SetDefaultSchema(null);
    }
}
