using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Persistence;

/// <summary>Helpers for database provider projects.</summary>
public static class ProviderPlumbing
{
    /// <summary>Adds an EF Core method-call translator plugin (used to translate <see cref="JsonFunctions"/>).</summary>
    public static DbContextOptionsBuilder AddMethodTranslator<TPlugin>(this DbContextOptionsBuilder options)
        where TPlugin : class, IMethodCallTranslatorPlugin
    {
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new TranslatorExtension<TPlugin>());
        return options;
    }

    private sealed class TranslatorExtension<TPlugin> : IDbContextOptionsExtension
        where TPlugin : class, IMethodCallTranslatorPlugin
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services) =>
            new EntityFrameworkRelationalServicesBuilder(services).TryAdd<IMethodCallTranslatorPlugin, TPlugin>();

        public void Validate(IDbContextOptions options)
        {
        }

        private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;

            public override string LogFragment => "using PaperDotNet JSON translations ";

            public override int GetServiceProviderHashCode() => typeof(TPlugin).GetHashCode();

            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
                debugInfo["PaperDotNet:Translator"] = typeof(TPlugin).Name;
        }
    }
}
