using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.Jobs.Data;

/// <summary>A long-running operation (Graph-style <c>/operations/{id}</c>).</summary>
[NotAudited]
public sealed class Operation : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Type { get; set; }

    public OperationStatus Status { get; set; }

    public int PercentComplete { get; set; }

    /// <summary>Payload as JSON (input of the handler).</summary>
    public required string Payload { get; set; }

    /// <summary>Result as JSON, when succeeded.</summary>
    public string? Result { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>Scheduling state of a recurring job (platform-level, one row per job).</summary>
public sealed class RecurringJobState : IVersioned
{
    public required string Name { get; set; }

    public DateTimeOffset NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastStatus { get; set; }

    public string? LastError { get; set; }

    public uint Version { get; set; }
}

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "jobs";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Operation> Operations => Set<Operation>();

    public DbSet<RecurringJobState> RecurringJobs => Set<RecurringJobState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Operation>(b =>
        {
            b.Property(o => o.Type).HasMaxLength(200);
            b.Property(o => o.Payload).IsJsonDocument();
            b.Property(o => o.Result).IsJsonDocument();
            b.HasIndex(o => new { o.Status, o.CompletedAt });
        });
        modelBuilder.Entity<RecurringJobState>(b =>
        {
            b.HasKey(j => j.Name);
            b.Property(j => j.Name).HasMaxLength(200);
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
