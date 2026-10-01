using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.AiWorkflows.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>
/// A call of an AI activity to the chat model (AI-06): what was asked (as a hash, never the text), by which workflow run
/// and model, the tokens used, and the answer, which is reused for the same model and input (the cache).
/// </summary>
public class AiCall : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The activity (<c>ai.extract</c>, …).</summary>
    public string Activity { get; set; } = "";

    /// <summary>Where it ran, e.g. <c>workflow run {id}</c>.</summary>
    public string Source { get; set; } = "";

    public Guid? RunId { get; set; }

    public string Model { get; set; } = "";

    /// <summary>SHA-256 of the model, the instructions, the input and the response format.</summary>
    public string InputHash { get; set; } = "";

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>Whether the answer was reused (from the cache or a batch: no tokens used by this call).</summary>
    public bool Cached { get; set; }

    /// <summary>The model's answer (null when the call failed).</summary>
    public string? Response { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><see cref="CreatedAt"/> in Unix milliseconds: SQLite compares numbers, not dates (ADR-0039).</summary>
    public long CreatedAtUnixMs { get; set; }
}

#pragma warning restore CA1852

/// <summary>The record of AI calls. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class AiWorkflowsDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public AiWorkflowsDbContext(DbContextOptions<AiWorkflowsDbContext> options)
        : base(options)
    {
    }

    public DbSet<AiCall> AiCalls { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AiCall>(b =>
        {
            b.ToTable("ai_calls");
            b.Property(c => c.Activity).HasMaxLength(200);
            b.Property(c => c.Source).HasMaxLength(300);
            b.Property(c => c.Model).HasMaxLength(200);
            b.Property(c => c.InputHash).HasMaxLength(64);
            b.Property(c => c.Error).HasMaxLength(2000);
            b.HasIndex(c => new { c.TenantId, c.InputHash, c.CreatedAtUnixMs });
            b.HasIndex(c => new { c.TenantId, c.CreatedAtUnixMs });
        });
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class AiWorkflowsDesignTimeFactory : IDesignTimeDbContextFactory<AiWorkflowsDbContext>
{
    public AiWorkflowsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<AiWorkflowsDbContext>());
}
