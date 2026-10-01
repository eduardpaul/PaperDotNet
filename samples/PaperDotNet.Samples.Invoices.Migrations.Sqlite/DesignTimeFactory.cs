using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Samples.Invoices.Migrations.Sqlite;

/// <summary>Creates the sample's DbContext for <c>dotnet ef</c> (migrations of its tables).</summary>
internal sealed class InvoicesDesignTimeFactory : IDesignTimeDbContextFactory<InvoicesDbContext>
{
    public InvoicesDbContext CreateDbContext(string[] args) =>
        new(SqliteDesignTime.Options<InvoicesDbContext>(typeof(InvoicesDesignTimeFactory).Assembly.GetName().Name!));
}
