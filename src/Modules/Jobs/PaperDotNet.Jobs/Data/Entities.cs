using System.ComponentModel.DataAnnotations;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Jobs.Data;

#pragma warning disable CA1852 // Not sealed: EF Core precompiled materializers (ADR-0039).

/// <summary>A long-running operation (Graph-style <c>/operations/{id}</c>), readable by the user who started it.</summary>
public class Operation : ITenantOwned, INotAudited
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Type { get; set; } = "";

    /// <summary>An <c>OperationStatus</c> value (Jobs.Contracts).</summary>
    public string Status { get; set; } = "";

    public int PercentComplete { get; set; }

    /// <summary>Payload as JSON (input of the handler).</summary>
    public string? Payload { get; set; }

    /// <summary>Result as JSON, when succeeded.</summary>
    public string? Result { get; set; }

    public string? Error { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>When an operation finished (a query projection; here because generated queries import only this namespace).</summary>
public sealed record OperationCompletion(Guid Id, DateTimeOffset? CompletedAt);

/// <summary>Scheduling state of a recurring job (platform-level, one row per job).</summary>
public class RecurringJobState : IVersioned
{
    public string Name { get; set; } = "";

    public DateTimeOffset NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastStatus { get; set; }

    public string? LastError { get; set; }

    /// <summary>Claims a run: two servers that both see a job due cannot both save the next run time.</summary>
    [ConcurrencyCheck]
    public uint Version { get; set; }
}
