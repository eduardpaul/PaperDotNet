using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Persistence.PostgreSql;
using PaperDotNet.Tenancy.Data;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Migrations.PostgreSql;

/// <summary>Creates module DbContexts for <c>dotnet ef</c>. No tenant: design time only builds the model.</summary>
internal static class DesignTime
{
    public static DbContextOptions<T> Options<T>(string schema)
        where T : DbContext
    {
        var connection = Environment.GetEnvironmentVariable("PAPERDOTNET_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paperdotnet_design;Username=postgres;Password=postgres";
        var builder = new DbContextOptionsBuilder<T>();
        PostgreSqlServiceCollectionExtensions.ConfigureForDesignTime(builder, connection, schema);
        return builder.Options;
    }

    public static readonly ITenantContext NoTenant = new NoTenantContext();

    private sealed class NoTenantContext : ITenantContext
    {
        public Guid? TenantId => null;

        public string? TenantIdentifier => null;
    }
}

internal sealed class TenancyDesignTimeFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<TenancyDbContext>(TenancyDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<IdentityDbContext>(IdentityDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class WorkspacesDesignTimeFactory : IDesignTimeDbContextFactory<WorkspacesDbContext>
{
    public WorkspacesDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<WorkspacesDbContext>(WorkspacesDbContext.Schema), DesignTime.NoTenant);
}
