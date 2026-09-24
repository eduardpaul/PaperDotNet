using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Persistence;
using PaperDotNet.Persistence.PostgreSql;
using PaperDotNet.Tenancy.Data;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.UnitTests;

/// <summary>Every tenant-owned entity in every module must carry the named Tenant filter.</summary>
public sealed class TenantFilterModelTests
{
    private const string Connection = "Host=localhost;Database=model_only";

    public static TheoryData<string, string> Contexts => new()
    {
        { "tenancy", "postgresql" }, { "identity", "postgresql" }, { "workspaces", "postgresql" }, { "lists", "postgresql" }, { "jobs", "postgresql" }, { "taxonomy", "postgresql" },
        { "tenancy", "sqlite" }, { "identity", "sqlite" }, { "workspaces", "sqlite" }, { "lists", "sqlite" }, { "jobs", "sqlite" }, { "taxonomy", "sqlite" },
    };

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Tenant_owned_entities_have_the_tenant_filter(string module, string provider)
    {
        using var context = Create(module, provider);

        var tenantOwned = context.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)).ToList();

        Assert.All(tenantOwned, t => Assert.Contains(t.GetDeclaredQueryFilters(), f => f.Key == QueryFilters.Tenant));
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Soft_deletable_entities_have_the_soft_delete_filter(string module, string provider)
    {
        using var context = Create(module, provider);

        var deletable = context.Model.GetEntityTypes().Where(t => typeof(ISoftDeletable).IsAssignableFrom(t.ClrType)).ToList();

        Assert.All(deletable, t => Assert.Contains(t.GetDeclaredQueryFilters(), f => f.Key == QueryFilters.SoftDelete));
    }

    private static DbContext Create(string module, string provider) => module switch
    {
        "tenancy" => new TenancyDbContext(Options<TenancyDbContext>(TenancyDbContext.Schema, provider), NoTenant.Instance),
        "identity" => new IdentityDbContext(Options<IdentityDbContext>(IdentityDbContext.Schema, provider), NoTenant.Instance),
        "workspaces" => new WorkspacesDbContext(Options<WorkspacesDbContext>(WorkspacesDbContext.Schema, provider), NoTenant.Instance),
        "jobs" => new Jobs.Data.JobsDbContext(Options<Jobs.Data.JobsDbContext>(Jobs.Data.JobsDbContext.Schema, provider), NoTenant.Instance),
        "taxonomy" => new Taxonomy.Data.TaxonomyDbContext(Options<Taxonomy.Data.TaxonomyDbContext>(Taxonomy.Data.TaxonomyDbContext.Schema, provider), NoTenant.Instance),
        "lists" => new Lists.Data.ListsDbContext(Options<Lists.Data.ListsDbContext>(Lists.Data.ListsDbContext.Schema, provider), NoTenant.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(module)),
    };

    private static DbContextOptions<T> Options<T>(string schema, string provider)
        where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        if (provider == "sqlite")
        {
            Persistence.Sqlite.SqliteServiceCollectionExtensions.ConfigureForDesignTime(builder, "Data Source=:memory:", schema);
        }
        else
        {
            PostgreSqlServiceCollectionExtensions.ConfigureForDesignTime(builder, Connection, schema);
        }

        return builder.Options;
    }

    private sealed class NoTenant : ITenantContext
    {
        public static readonly NoTenant Instance = new();

        public Guid? TenantId => null;

        public string? TenantIdentifier => null;
    }
}
