using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Core.Host.Data;

namespace PaperDotNet.Core.Migrations.Sqlite;

/// <summary>Creates <see cref="CoreDb"/> for the EF Core tools (migrations and scripts).</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CoreDb>
{
    public CoreDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CoreDb>()
            .UseSqlite("Data Source=design-time.db", sqlite => sqlite.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.GetName().Name))
            .Options);
}
