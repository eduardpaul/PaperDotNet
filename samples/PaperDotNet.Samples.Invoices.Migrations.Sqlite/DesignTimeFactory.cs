using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Samples.Invoices.Migrations.Sqlite;

/// <summary>Creates the sample's DbContext for <c>dotnet ef</c>. No tenant: design time only builds the model.</summary>
internal sealed class InvoicesDesignTimeFactory : IDesignTimeDbContextFactory<InvoicesDbContext>
{
    public InvoicesDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("PAPERDOTNET_DESIGN_CONNECTION") ?? "Data Source=paperdotnet_design.db";
        var builder = new DbContextOptionsBuilder<InvoicesDbContext>();
        SqliteServiceCollectionExtensions.ConfigureForDesignTime(
            builder, connection, ExtensionDbContext.SchemaFor(InvoicesExtension.Id), typeof(InvoicesDesignTimeFactory).Assembly.GetName().Name);
        return new InvoicesDbContext(builder.Options, new NoTenant());
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;

        public string? TenantIdentifier => null;
    }
}
