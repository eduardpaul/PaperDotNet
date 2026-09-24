using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Persistence;

/// <summary>
/// The schema a context was registered with. Contexts that do not choose their own schema
/// (extension contexts, ADR-0014) read it in <c>OnModelCreating</c>.
/// </summary>
public static class ModuleSchema
{
    public static DbContextOptionsBuilder UseModuleSchema(this DbContextOptionsBuilder options, string schema)
    {
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new ModuleSchemaOptionsExtension(schema));
        return options;
    }

    /// <summary>The registered schema, or null when the options have none.</summary>
    public static string? Find(DbContextOptions options) => options.FindExtension<ModuleSchemaOptionsExtension>()?.Schema;

    private sealed class ModuleSchemaOptionsExtension(string schema) : IDbContextOptionsExtension
    {
        private DbContextOptionsExtensionInfo? _info;

        public string Schema { get; } = schema;

        public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
        {
        }

        public void Validate(IDbContextOptions options)
        {
        }

        private sealed class ExtensionInfo(ModuleSchemaOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;

            public override string LogFragment => $"Schema={extension.Schema} ";

            // The schema is part of the model, not of the internal service provider.
            public override int GetServiceProviderHashCode() => 0;

            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
                debugInfo["PaperDotNet:Schema"] = extension.Schema;
        }
    }
}
