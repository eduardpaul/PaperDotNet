namespace PaperDotNet.Lists.Contracts;

/// <summary>A taxonomy-backed predicate; constraints apply to directed edges, including recycled endpoints.</summary>
public sealed record RelationshipTypeData(Guid Id, string Name, bool Directed, string? InverseLabel, int? MaxIncoming, int? MaxOutgoing);

/// <summary>One graph edge, viewed from an endpoint. RelatedItem is the other currently readable endpoint.</summary>
public sealed record ItemRelationshipData(Guid Id, Guid SourceItemId, Guid TargetItemId, bool Directed, RelationshipTypeData? Type, ListItemData RelatedItem);

public sealed record ItemRelationshipPage(IReadOnlyList<ItemRelationshipData> Items, string? NextCursor);

public sealed record RelationshipOptions(string? Type = null, bool? Directed = null);

public sealed record RelationshipTypeOptions(string Name, bool Directed = false, string? InverseLabel = null, int? MaxIncoming = null, int? MaxOutgoing = null);
