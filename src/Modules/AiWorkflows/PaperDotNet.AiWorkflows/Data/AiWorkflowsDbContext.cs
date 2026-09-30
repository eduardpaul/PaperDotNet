using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.AiWorkflows.Data;

/// <summary>
/// A call of an AI activity to the chat model (AI-06): what was asked (as a hash, never the text), by which workflow run
/// and model, the tokens used, and the answer, which is reused for the same model and input (the cache).
/// </summary>
[NotAudited]
public sealed class AiCall : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The activity (<c>ai.extract</c>, …).</summary>
    public required string Activity { get; set; }

    /// <summary>Where it ran, e.g. <c>workflow:File receipts</c>.</summary>
    public required string Source { get; set; }

    public Guid? RunId { get; set; }

    public required string Model { get; set; }

    /// <summary>SHA-256 of the model, the instructions, the input and the response format.</summary>
    public required string InputHash { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>Whether the answer was reused (from the cache or a batch: no tokens used by this call).</summary>
    public bool Cached { get; set; }

    /// <summary>The model's answer (null when the call failed).</summary>
    public string? Response { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The tables of AI in workflows (schema <c>ai</c>): the record of AI calls.</summary>
public sealed class AiWorkflowsDbContext(DbContextOptions<AiWorkflowsDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "ai";

    public DbSet<AiCall> AiCalls => Set<AiCall>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiCall>(b =>
        {
            b.ToTable("ai_calls");
            b.Property(c => c.Activity).HasMaxLength(200);
            b.Property(c => c.Source).HasMaxLength(300);
            b.Property(c => c.Model).HasMaxLength(200);
            b.Property(c => c.InputHash).HasMaxLength(64);
            b.Property(c => c.Error).HasMaxLength(2000);
            b.HasIndex(c => new { c.TenantId, c.InputHash, c.CreatedAt });
            b.HasIndex(c => new { c.TenantId, c.CreatedAt });
        });
    }
}
