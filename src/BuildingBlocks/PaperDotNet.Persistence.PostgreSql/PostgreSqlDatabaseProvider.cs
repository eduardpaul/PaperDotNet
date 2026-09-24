using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>PostgreSQL settings from configuration (<c>Database</c> section).</summary>
internal sealed record PostgreSqlSettings(bool RowLevelSecurity);

internal sealed class PostgreSqlDatabaseProvider(NpgsqlDataSource dataSource, PostgreSqlSettings settings, ILogger<PostgreSqlDatabaseProvider> logger) : IDatabaseProvider
{
    public const string ProviderName = "PostgreSql";

    /// <summary>Assembly holding the PostgreSQL migrations of every module.</summary>
    public const string MigrationsAssembly = "PaperDotNet.Migrations.PostgreSql";

    public string Name => ProviderName;

    public void Configure(DbContextOptionsBuilder options, string schema, string? migrationsAssembly = null)
    {
        Configure(options, dataSource, schema, migrationsAssembly);
        if (settings.RowLevelSecurity)
        {
            options.AddInterceptors(TenantSessionInterceptor.Instance);
        }
    }

    public Task AfterMigrateAsync(DbContext context, CancellationToken cancellationToken) =>
        settings.RowLevelSecurity ? PostgreSqlRowLevelSecurity.ApplyAsync(context, logger, cancellationToken) : Task.CompletedTask;

    public static void Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource, string schema, string? migrationsAssembly = null)
    {
        options
            .UseModuleSchema(schema)
            .UseNpgsql(dataSource, npgsql => npgsql
                .MigrationsAssembly(migrationsAssembly ?? MigrationsAssembly)
                .MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention()
            .AddMethodTranslator<PostgreSqlJsonTranslatorPlugin>()
            .ReplaceService<IModelCustomizer, PostgreSqlModelCustomizer>();
    }
}

/// <summary>PostgreSQL mapping of provider-neutral model hints (JSON documents, containment indexes).</summary>
internal sealed class PostgreSqlModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    private static readonly string[] JsonPathOps = ["jsonb_path_ops"];

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties().Where(p => p.FindAnnotation(JsonDocumentExtensions.JsonDocumentAnnotation) is not null))
            {
                property.SetColumnType("jsonb");
            }

            foreach (var index in entityType.GetIndexes().Where(i => i.FindAnnotation(JsonDocumentExtensions.ContainmentIndexAnnotation) is not null))
            {
                index.SetAnnotation("Npgsql:IndexMethod", "gin");
                index.SetAnnotation("Npgsql:IndexOperators", JsonPathOps);
            }
        }

        PostgreSqlFullTextSearch.Customize(modelBuilder);
    }
}
