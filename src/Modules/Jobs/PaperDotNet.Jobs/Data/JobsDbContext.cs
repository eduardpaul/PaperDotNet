using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Jobs.Data;

/// <summary>Operations and recurring job state. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class JobsDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public JobsDbContext(DbContextOptions<JobsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Operation> Operations { get; set; } = null!;

    public DbSet<RecurringJobState> RecurringJobs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Operation>(operation =>
        {
            operation.ToTable("operations");
            operation.Property(o => o.Type).HasMaxLength(200);
            operation.Property(o => o.Status).HasMaxLength(16);
            operation.HasIndex(o => new { o.TenantId, o.Status });
        });
        modelBuilder.Entity<RecurringJobState>(job =>
        {
            job.ToTable("recurring_jobs");
            job.HasKey(j => j.Name);
            job.Property(j => j.Name).HasMaxLength(200);
            job.Property(j => j.LastStatus).HasMaxLength(16);
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class JobsDesignTimeFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<JobsDbContext>());
}
