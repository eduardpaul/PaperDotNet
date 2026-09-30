using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;
using Wolverine.EntityFrameworkCore;

namespace PaperDotNet.Lists.Data;

/// <summary>
/// Lists and items. At run time the compiled model and the precompiled queries generated at publish are used (ADR-0039).
/// Query rules: one LINQ expression from a DbSet property to the terminal operator; the DbContext and every captured
/// value copied into locals first (dotnet/efcore#35887); an explicit <c>TenantId</c> filter (no global query filters
/// under AOT). Dynamic item queries go through <see cref="Querying.IItemQueries"/>.
/// </summary>
public class ListsDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public ListsDbContext(DbContextOptions<ListsDbContext> options)
        : base(options)
    {
    }

    public DbSet<ListDefinition> Lists { get; set; } = null!;

    public DbSet<ListItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Wolverine's inbox/outbox tables, written in the same transaction; Wolverine creates them itself.
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<ListDefinition>(list =>
        {
            list.ToTable("lists");
            list.Property(l => l.Name).HasMaxLength(255);
            list.HasIndex(l => new { l.TenantId, l.Name }).IsUnique();
        });

        modelBuilder.Entity<ListItem>(item =>
        {
            item.ToTable("list_items");
            item.Property(i => i.Title).HasMaxLength(255);
            item.HasIndex(i => new { i.TenantId, i.ListId, i.Id });
            item.HasIndex(i => new { i.TenantId, i.ListId, i.Title });
            item.HasOne<ListDefinition>().WithMany().HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class ListsDesignTimeFactory : IDesignTimeDbContextFactory<ListsDbContext>
{
    public ListsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<ListsDbContext>());
}
