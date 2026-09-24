using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Lists.Data;
using PaperDotNet.Persistence.Sqlite;
using PaperDotNet.Search.Data;
using PaperDotNet.Taxonomy.Data;
using PaperDotNet.Tenancy.Data;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Migrations.Sqlite;

/// <summary>Creates module DbContexts for <c>dotnet ef</c>. No tenant: design time only builds the model.</summary>
internal static class DesignTime
{
    public static DbContextOptions<T> Options<T>(string schema)
        where T : DbContext
    {
        var connection = Environment.GetEnvironmentVariable("PAPERDOTNET_DESIGN_CONNECTION")
            ?? "Data Source=paperdotnet_design.db";
        var builder = new DbContextOptionsBuilder<T>();
        SqliteServiceCollectionExtensions.ConfigureForDesignTime(builder, connection, schema);
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

internal sealed class ListsDesignTimeFactory : IDesignTimeDbContextFactory<ListsDbContext>
{
    public ListsDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<ListsDbContext>(ListsDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class JobsDesignTimeFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<JobsDbContext>(JobsDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class TaxonomyDesignTimeFactory : IDesignTimeDbContextFactory<TaxonomyDbContext>
{
    public TaxonomyDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<TaxonomyDbContext>(TaxonomyDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class SearchDesignTimeFactory : IDesignTimeDbContextFactory<SearchDbContext>
{
    public SearchDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<SearchDbContext>(SearchDbContext.Schema), DesignTime.NoTenant);
}
