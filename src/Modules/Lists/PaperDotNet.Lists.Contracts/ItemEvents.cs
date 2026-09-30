using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists.Contracts;

public sealed record ListCreated(Guid ListId, string Name) : IntegrationEvent;

public sealed record ListDeleted(Guid ListId, string Name) : IntegrationEvent;

public sealed record ItemCreated(Guid ListId, Guid ItemId, string Title) : IntegrationEvent;

public sealed record ItemUpdated(Guid ListId, Guid ItemId, string Title, IReadOnlyList<string> ChangedFields) : IntegrationEvent;

public sealed record ItemDeleted(Guid ListId, Guid ItemId, string Title) : IntegrationEvent;
