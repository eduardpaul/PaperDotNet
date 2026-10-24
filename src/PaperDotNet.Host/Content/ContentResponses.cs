using System.Text.Json;
using PaperDotNet.Content;

namespace PaperDotNet.Host.Content;

public sealed record ContentResourceResponse(string Id, string WorkspaceId, string? ParentId, string? ContainerId, string Name,
    string Kind, long Version, bool PublishingEnabled, string Versioning, long MaxVersions, DateTime? ArchivedAt, string? ArchivedBy)
{
    public static ContentResourceResponse From(ResourcesRow row) => new(row.Id, row.WorkspaceId, row.ParentId, row.ContainerId,
        row.Name, row.Kind, row.Version, row.PublishingEnabled, row.Versioning, row.MaxVersions, row.ArchivedAt, row.ArchivedBy);
}

public sealed record ContentItemResponse(ContentResourceResponse Resource, string RevisionId, long RevisionNumber,
    string SchemaRevisionId, string ContentTypeId, JsonElement Values, IReadOnlyList<string> Keywords, string Filename,
    bool HasFile, bool IsDraft, EffectiveSchema Schema)
{
    public static ContentItemResponse From(ContentView view, string surface) => new(ContentResourceResponse.From(view.Resource),
        view.Revision.Id, view.Revision.RevisionNumber, view.Revision.SchemaRevisionId, view.Revision.ContentTypeId,
        ContentWire.Values(JsonSerializer.Deserialize<JsonElement>(view.Revision.Payload), JsonSerializer.Deserialize<EffectiveSchema>(view.Schema.Definition)!, false), JsonSerializer.Deserialize<string[]>(view.Revision.Keywords)!,
        view.Revision.Filename, view.Revision.BlobId is not null, surface == "head" && view.Resource.PublishedRevisionId != view.Revision.Id,
        ContentWire.Schema(JsonSerializer.Deserialize<EffectiveSchema>(view.Schema.Definition)!, false));
}

public sealed record ContentCollectionResponse(ContentResourceResponse Resource, EffectiveSchema? Schema, string? SchemaRevisionId,
    bool CanWrite, bool CanPublish, bool CanReadDraft, bool CanManage);
public sealed record CreateContentWorkspace(string Name);
public sealed record CreateContentCollectionRequest(string Name, bool Library = false, bool PublishingEnabled = false);
public sealed record ContentSettings(bool PublishingEnabled, string Versioning = "off", long MaxVersions = 50);
public sealed record ContentArchiveRequest(bool Archived);
public sealed record ContentGrantsRequest(IReadOnlyList<ContentGrant> Grants, bool Inherit = false);
public sealed record RestoreContentRequest(long Number);
public sealed record CreateContentItem(ContentInput Content, bool Folder = false);
