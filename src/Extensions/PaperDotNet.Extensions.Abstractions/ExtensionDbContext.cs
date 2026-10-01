using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Extensions;

/// <summary>
/// Base class of an extension's own tables (EXT-07), registered with <see cref="IExtensionBuilder.AddDbContext{TContext}"/>.
/// Tables get the prefix <c>ext_{id}_</c> and every entity must implement <see cref="ITenantOwned"/>. The rules of the
/// Native AOT server apply as in modules (ADR-0039): queries name the tenant explicitly (<c>TenantId</c> passed in, no
/// ambient tenant) and precompile (DbContext and captured values in locals, one expression to the terminal operator,
/// entities not sealed and in the DbContext's namespace); the save guard stamps <see cref="IAuditable"/> and
/// <see cref="IVersioned"/> and refuses writes to another tenant. The extension project builds its compiled model and
/// precompiled queries at publish (<c>src/Extensions/Extension.props</c>) and embeds the SQL of its migrations as
/// <c>Schema.Sqlite.{migration}.sql</c>; the host applies them on start.
/// </summary>
public abstract class ExtensionDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    protected ExtensionDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <summary>The extension's id (its manifest's <c>id</c>): table names start with <see cref="TablePrefix"/> of it.</summary>
    protected abstract string ExtensionId { get; }

    /// <summary>The prefix of an extension's tables: <c>ext_</c>, the id with <c>.</c> and <c>-</c> replaced by <c>_</c>, and <c>_</c>.</summary>
    public static string TablePrefix(string extensionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        return "ext_" + extensionId.Replace('.', '_').Replace('-', '_') + "_";
    }

    /// <summary>Configure the extension's entities here (table names get the extension's prefix).</summary>
    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureModel(modelBuilder);

        var prefix = TablePrefix(ExtensionId);
        var shared = new List<string>();
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.BaseType is null))
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entity.ClrType))
            {
                shared.Add(entity.ClrType.Name);
            }

            if (entity.GetTableName() is { } table && !table.StartsWith(prefix, StringComparison.Ordinal))
            {
                entity.SetTableName(prefix + table);
            }
        }

        if (shared.Count > 0)
        {
            throw new InvalidOperationException($"{GetType().Name}: extension entities must implement ITenantOwned ({string.Join(", ", shared)}).");
        }
    }
}
