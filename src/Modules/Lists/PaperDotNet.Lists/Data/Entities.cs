using System.ComponentModel.DataAnnotations;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists.Data;

#pragma warning disable CA1852 // Not sealed: EF Core precompiled materializers test entities for IInjectableService (ADR-0039).

/// <summary>A list (or library): a named set of fields its items fill in.</summary>
public class ListDefinition : ITenantOwned, IVersioned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The field definitions as JSON (<see cref="Fields.FieldDefinition"/>[]).</summary>
    public string Fields { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>An item of a list: its title in a column, the other field values as one JSON object.</summary>
public class ListItem : ITenantOwned, IVersioned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Field values except the title, as a JSON object in storage form (see <see cref="Fields.FieldValues"/>).</summary>
    public string Fields { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

#pragma warning restore CA1852
