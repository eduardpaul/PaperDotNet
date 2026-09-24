using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Extensions;

/// <summary>
/// Base class of an extension's own tables (EXT-07), registered with
/// <see cref="IExtensionBuilder.AddDbContext{TContext}"/>. The host places them in the schema
/// <c>ext_{id}</c> and applies the platform conventions: every entity must implement
/// <see cref="ITenantOwned"/> (tenant filter, PostgreSQL row-level security), and
/// <see cref="ISoftDeletable"/>, <see cref="IVersioned"/> and <see cref="IAuditable"/> work as in modules.
/// Migrations live in companion assemblies <c>{extension assembly}.Migrations.Sqlite</c> and
/// <c>.PostgreSql</c>, which the host references next to the extension.
/// </summary>
public abstract class ExtensionDbContext : DbContext, ITenantScopedDbContext
{
    private readonly DbContextOptions _options;
    private readonly ITenantContext _tenant;

    protected ExtensionDbContext(DbContextOptions options, ITenantContext tenant)
        : base(options)
    {
        _options = options;
        _tenant = tenant;
    }

    public Guid? CurrentTenantId => _tenant.TenantId;

    /// <summary>The schema of an extension's tables: <c>ext_</c> and the id with <c>.</c> and <c>-</c> replaced by <c>_</c>.</summary>
    public static string SchemaFor(string extensionId) =>
        "ext_" + extensionId.Replace('.', '_').Replace('-', '_');

    /// <summary>Configure the extension's entities here (tables get the extension's schema).</summary>
    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(ModuleSchema.Find(_options)
            ?? throw new InvalidOperationException($"{GetType().Name} has no schema: register it with IExtensionBuilder.AddDbContext."));
        ConfigureModel(modelBuilder);

        var shared = modelBuilder.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.BaseType is null && !typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .Select(t => t.ClrType.Name)
            .ToList();
        if (shared.Count > 0)
        {
            throw new InvalidOperationException(
                $"{GetType().Name}: extension entities must implement ITenantOwned ({string.Join(", ", shared)}).");
        }

        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
