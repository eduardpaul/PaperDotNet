using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.StorageOptimization.Migrations.Sqlite;

/// <summary>Creates the sample's DbContext for <c>dotnet ef</c>. No tenant: design time only builds the model.</summary>
internal sealed class PhotoConversionDesignTimeFactory : IDesignTimeDbContextFactory<PhotoConversionDbContext>
{
    public PhotoConversionDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("PAPERDOTNET_DESIGN_CONNECTION") ?? "Data Source=paperdotnet_design.db";
        var builder = new DbContextOptionsBuilder<PhotoConversionDbContext>();
        SqliteServiceCollectionExtensions.ConfigureForDesignTime(
            builder, connection, ExtensionDbContext.SchemaFor(StorageOptimizationExtension.Id), typeof(PhotoConversionDesignTimeFactory).Assembly.GetName().Name);
        return new PhotoConversionDbContext(builder.Options, new NoTenant());
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;

        public string? TenantIdentifier => null;
    }
}
