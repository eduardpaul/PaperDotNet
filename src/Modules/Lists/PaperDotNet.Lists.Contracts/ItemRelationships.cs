using System.Text.Json.Nodes;

namespace PaperDotNet.Lists.Contracts;

/// <summary>A taxonomy-backed predicate; constraints apply to directed edges, including recycled endpoints.</summary>
public sealed record RelationshipTypeData(Guid Id, string Name, bool Directed, string? InverseLabel, int? MaxIncoming, int? MaxOutgoing);

/// <summary>One graph edge, viewed from an endpoint. RelatedItem is the other currently readable endpoint.</summary>
public sealed record ItemRelationshipData(Guid Id, Guid SourceItemId, Guid TargetItemId, bool Directed, RelationshipTypeData? Type, ListItemData RelatedItem, JsonObject Attributes, uint Version);

public sealed record ItemRelationshipPage(IReadOnlyList<ItemRelationshipData> Items, string? NextCursor);

public sealed record RelationshipOptions(string? Type = null, bool? Directed = null, JsonObject? Attributes = null);

public sealed record RelationshipTypeOptions(string Name, bool Directed = false, string? InverseLabel = null, int? MaxIncoming = null, int? MaxOutgoing = null);

/// <summary>A workspace graph edge; both endpoints are readable and at least one currently belongs to the workspace.</summary>
public sealed record WorkspaceRelationshipData(Guid Id, bool Directed, RelationshipTypeData? Type, JsonObject Attributes, uint Version, ListItemData SourceItem, ListItemData TargetItem);

public sealed record WorkspaceRelationshipPage(IReadOnlyList<WorkspaceRelationshipData> Items, string? NextCursor);
