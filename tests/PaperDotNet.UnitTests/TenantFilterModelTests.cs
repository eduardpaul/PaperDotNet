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

    public static TheoryData<string> Contexts => ["tenancy", "identity", "workspaces", "lists"];

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Tenant_owned_entities_have_the_tenant_filter(string module)
    {
        using var context = Create(module);

        var tenantOwned = context.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)).ToList();

        Assert.All(tenantOwned, t => Assert.Contains(t.GetDeclaredQueryFilters(), f => f.Key == QueryFilters.Tenant));
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Soft_deletable_entities_have_the_soft_delete_filter(string module)
    {
        using var context = Create(module);

        var deletable = context.Model.GetEntityTypes().Where(t => typeof(ISoftDeletable).IsAssignableFrom(t.ClrType)).ToList();

        Assert.All(deletable, t => Assert.Contains(t.GetDeclaredQueryFilters(), f => f.Key == QueryFilters.SoftDelete));
    }

    private static DbContext Create(string module) => module switch
    {
        "tenancy" => new TenancyDbContext(Options<TenancyDbContext>(TenancyDbContext.Schema), NoTenant.Instance),
        "identity" => new IdentityDbContext(Options<IdentityDbContext>(IdentityDbContext.Schema), NoTenant.Instance),
        "workspaces" => new WorkspacesDbContext(Options<WorkspacesDbContext>(WorkspacesDbContext.Schema), NoTenant.Instance),
        "lists" => new Lists.Data.ListsDbContext(Options<Lists.Data.ListsDbContext>(Lists.Data.ListsDbContext.Schema), NoTenant.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(module)),
    };

    private static DbContextOptions<T> Options<T>(string schema)
        where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        PostgreSqlServiceCollectionExtensions.ConfigureForDesignTime(builder, Connection, schema);
        return builder.Options;
    }

    private sealed class NoTenant : ITenantContext
    {
        public static readonly NoTenant Instance = new();

        public Guid? TenantId => null;

        public string? TenantIdentifier => null;
    }
}
