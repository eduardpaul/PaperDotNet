using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Content;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.Host.Content;

internal static class ContentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("content", "Content").AddEndpointFilter<ContentErrorFilter>();
        group.MapGet("/workspaces", WorkspacesAsync).RequireScope(ContentScopes.Read).WithName("ContentWorkspaces");
        group.MapGet("/items", GlobalItemsAsync).RequireScope(ContentScopes.Read).WithQueryEnum<ArchiveFilter>("archive").WithName("GlobalContentItems");
        group.MapPost("/workspaces", CreateWorkspaceAsync).RequireScope(ContentScopes.Manage).WithName("CreateContentWorkspace");
        group.MapGet("/workspaces/{id}/collections", CollectionsAsync).RequireScope(ContentScopes.Read).WithName("ContentCollections");
        group.MapPost("/workspaces/{id}/collections", CreateCollectionAsync).RequireScope(ContentScopes.Manage).WithName("CreateContentCollection");
        group.MapGet("/collections/{id}", CollectionAsync).RequireScope(ContentScopes.Read).WithName("ContentCollection");
        group.MapPut("/collections/{id}/schema", SchemaAsync).RequireScope(ContentScopes.Manage).WithName("SetContentSchema");
        group.MapPut("/collections/{id}/settings", SettingsAsync).RequireScope(ContentScopes.Manage).WithName("SetContentSettings");
        group.MapGet("/collections/{id}/items", ItemsAsync).RequireScope(ContentScopes.Read).WithQueryEnum<ArchiveFilter>("archive").WithName("ContentItems");
        group.MapPost("/resources/{id}/items", CreateItemAsync).RequireScope(ContentScopes.Write).WithName("CreateContentItem");
        group.MapGet("/items/{id}", ItemAsync).RequireScope(ContentScopes.Read).WithName("ContentItem");
        group.MapPut("/items/{id}", EditAsync).RequireScope(ContentScopes.Write).WithName("EditContentItem");
        group.MapPost("/items/{id}/publish", PublishAsync).RequireScope(ContentScopes.Publish).WithName("PublishContentItem");
        group.MapPost("/items/{id}/unpublish", UnpublishAsync).RequireScope(ContentScopes.Publish).WithName("UnpublishContentItem");
        group.MapPut("/items/{id}/archive", ArchiveAsync).RequireScope(ContentScopes.Write).WithName("ArchiveContentItem");
        group.MapGet("/items/{id}/history", HistoryAsync).RequireScope(ContentScopes.Read).WithName("ContentHistory");
        group.MapPost("/items/{id}/restore", RestoreAsync).RequireScope(ContentScopes.Write).WithName("RestoreContentRevision");
        group.MapGet("/resources/{id}/grants", GrantsAsync).RequireScope(ContentScopes.Manage).WithName("ContentGrants");
        group.MapPut("/resources/{id}/grants", SetGrantsAsync).RequireScope(ContentScopes.Manage).WithName("SetContentGrants");
        group.MapPut("/items/{id}/file", UploadAsync).RequireScope(ContentScopes.Write).WithName("UploadContentFile").Accepts<IFormFile>("multipart/form-data").DisableAntiforgery();
        group.MapGet("/items/{id}/file", DownloadAsync).RequireScope(ContentScopes.Read).WithName("DownloadContentFile");
        group.MapGet("/migrations/{id:guid}/preview", MigrationPreviewAsync).RequireScope("organization.manage").WithName("PreviewContentMigration");
        group.MapPost("/migrations/{id:guid}", MigrateAsync).RequireScope("organization.manage").WithName("MigrateContentWorkspace");
    }

    private static async Task<Ok<IReadOnlyList<ContentResourceResponse>>> WorkspacesAsync(ContentSession session, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var rows = await session.Db.Resources.AsNoTracking().Where(x => x.Kind == "workspace" && x.DeletedAt == null).OrderBy(x => x.Name).ToListAsync(ct);
        var visible = new List<ContentResourceResponse>();
        foreach (var row in rows) if (await session.Access.AllowedAsync(session.Actor, row, "read", ct)) visible.Add(ContentResourceResponse.From(row));
        return TypedResults.Ok<IReadOnlyList<ContentResourceResponse>>(visible);
    }

    private static async Task<Ok<IReadOnlyList<ContentItemResponse>>> GlobalItemsAsync(string? surface, string? archive, string? search, string? afterId,
        int? limit, ContentSession session, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        if (!EnumQuery.TryParse<ArchiveFilter>(archive, out var selectedArchive)) throw new ArgumentException("Archive must be active, archived or all.");
        var count = limit ?? 100;
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var selected = surface ?? "published";
        var collections = await session.Db.Resources.AsNoTracking().Where(x => (x.Kind == "list" || x.Kind == "library") && x.DeletedAt == null).ToListAsync(ct);
        var visible = new List<ContentItemResponse>();
        foreach (var collection in collections)
        {
            if (!await session.Access.AllowedAsync(session.Actor, collection, "read", ct)) continue;
            if (selected == "head" && collection.PublishingEnabled && !await session.Access.AllowedAsync(session.Actor, collection, "read_draft", ct)) continue;
            visible.AddRange((await session.Store.BrowseAsync(session.Actor, collection.Id, selected, selectedArchive ?? ArchiveFilter.Active,
                count, afterId, search, cancellationToken: ct)).Select(x => ContentItemResponse.From(x, selected)));
        }
        return TypedResults.Ok<IReadOnlyList<ContentItemResponse>>(visible.OrderBy(x => x.Resource.Id, StringComparer.Ordinal).Take(count).ToArray());
    }

    private static async Task<Ok<ContentResourceResponse>> CreateWorkspaceAsync(CreateContentWorkspace body, ContentSession session, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await session.Store.CreateWorkspaceAsync(session.Actor, body.Name, ct);
        Stamp(response, row.Version);
        return TypedResults.Ok(ContentResourceResponse.From(row));
    }

    private static async Task<Ok<IReadOnlyList<ContentResourceResponse>>> CollectionsAsync(string id, ContentSession session, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var workspace = await ResourceAsync(session, id, "read", ct);
        var rows = await session.Db.Resources.AsNoTracking().Where(x => x.WorkspaceId == workspace.Id && (x.Kind == "list" || x.Kind == "library") && x.DeletedAt == null).OrderBy(x => x.Name).ToListAsync(ct);
        var visible = new List<ContentResourceResponse>();
        foreach (var row in rows) if (await session.Access.AllowedAsync(session.Actor, row, "read", ct)) visible.Add(ContentResourceResponse.From(row));
        return TypedResults.Ok<IReadOnlyList<ContentResourceResponse>>(visible);
    }

    private static async Task<Ok<ContentResourceResponse>> CreateCollectionAsync(string id, CreateContentCollectionRequest body, ContentSession session, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var typeId = Guid.CreateVersion7().ToString();
        var row = await session.Store.CreateCollectionAsync(session.Actor, id, body.Name,
            new EffectiveSchema([], [new SchemaContentType(typeId, "item", "Item", [], true)]), body.Library, body.PublishingEnabled, ct);
        Stamp(response, row.Version);
        return TypedResults.Ok(ContentResourceResponse.From(row));
    }

    private static async Task<Ok<ContentCollectionResponse>> CollectionAsync(string id, ContentSession session, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await ResourceAsync(session, id, "read", ct);
        if (row.Kind is not ("list" or "library")) throw new KeyNotFoundException("Collection not found.");
        var schema = await session.Db.SchemaRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.SchemaHeadId, ct);
        Stamp(response, row.Version);
        return TypedResults.Ok(new ContentCollectionResponse(ContentResourceResponse.From(row), schema is null ? null : ContentWire.Schema(System.Text.Json.JsonSerializer.Deserialize<EffectiveSchema>(schema.Definition)!, false), schema?.Id,
            await session.Access.AllowedAsync(session.Actor, row, "write", ct), await session.Access.AllowedAsync(session.Actor, row, "publish", ct),
            await session.Access.AllowedAsync(session.Actor, row, "read_draft", ct), await session.Access.AllowedAsync(session.Actor, row, "manage", ct)));
    }

    private static async Task<Ok<SchemaRevisionsRow>> SchemaAsync(string id, EffectiveSchema body, ContentSession session, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var schema = await session.Store.RecordSchemaAsync(session.Actor, id, Version(request), ContentWire.Schema(body, true), ct);
        Stamp(response, (await session.Db.Resources.AsNoTracking().SingleAsync(x => x.Id == id, ct)).Version);
        return TypedResults.Ok(schema);
    }

    private static async Task<Ok<ContentResourceResponse>> SettingsAsync(string id, ContentSettings body, ContentSession session, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await session.Store.ConfigureCollectionAsync(session.Actor, id, Version(request), body.PublishingEnabled, body.Versioning, body.MaxVersions, ct);
        Stamp(response, row.Version); return TypedResults.Ok(ContentResourceResponse.From(row));
    }

    private static async Task<Ok<IReadOnlyList<ContentItemResponse>>> ItemsAsync(string id, string? surface, string? archive, string? search, string? parentId,
        string? afterId, int? limit, ContentSession session, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        if (!EnumQuery.TryParse<ArchiveFilter>(archive, out var selectedArchive)) throw new ArgumentException("Archive must be active, archived or all.");
        var selected = surface ?? "published";
        var rows = await session.Store.BrowseAsync(session.Actor, id, selected, selectedArchive ?? ArchiveFilter.Active, limit ?? 100, afterId, search, parentId, ct);
        return TypedResults.Ok<IReadOnlyList<ContentItemResponse>>(rows.Select(x => ContentItemResponse.From(x, selected)).ToArray());
    }

    private static async Task<Ok<ContentItemResponse>> CreateItemAsync(string id, CreateContentItem body, ContentSession session, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await session.Store.CreateItemAsync(session.Actor, id, await ContentWire.InputAsync(session, id, body.Content, ct), body.Folder, ct);
        Stamp(response, row.Version);
        // A writer can create without read_draft; return the submitted snapshot without broadening draft permissions.
        return TypedResults.Ok(await WrittenAsync(session, row, ct));
    }

    private static async Task<Ok<ContentItemResponse>> ItemAsync(string id, string? surface, ContentSession session, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var selected = surface ?? "published";
        var view = await session.Store.ReadAsync(session.Actor, id, selected, ct);
        Stamp(response, view.Resource.Version); return TypedResults.Ok(ContentItemResponse.From(view, selected));
    }

    private static async Task<Ok<ContentItemResponse>> EditAsync(string id, ContentInput body, ContentSession session, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await session.Store.EditAsync(session.Actor, id, Version(request), await ContentWire.InputAsync(session, id, body, ct), ct);
        Stamp(response, row.Version); return TypedResults.Ok(await WrittenAsync(session, row, ct));
    }

    private static async Task<Ok<ContentResourceResponse>> PublishAsync(string id, ContentSession session, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        await session.Store.PublishAsync(session.Actor, id, Version(request), ct);
        var row = await session.Db.Resources.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        Stamp(response, row.Version); return TypedResults.Ok(ContentResourceResponse.From(row));
    }

    private static async Task<Ok<ContentResourceResponse>> UnpublishAsync(string id, ContentSession session, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await session.Store.UnpublishAsync(session.Actor, id, Version(request), ct);
        Stamp(response, row.Version); return TypedResults.Ok(ContentResourceResponse.From(row));
    }

    private static async Task<Ok<ContentResourceResponse>> ArchiveAsync(string id, ContentArchiveRequest body, ContentSession session, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await session.Store.SetArchivedAsync(session.Actor, id, Version(request), body.Archived, ct);
        Stamp(response, row.Version); return TypedResults.Ok(ContentResourceResponse.From(row));
    }

    private static async Task<Ok<IReadOnlyList<ContentHistoryView>>> HistoryAsync(string id, ContentSession session, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var history = await session.Store.HistoryAsync(session.Actor, id, cancellationToken: ct);
        foreach (var entry in history)
            entry.Revision.Payload = ContentWire.Values(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(entry.Revision.Payload),
                System.Text.Json.JsonSerializer.Deserialize<EffectiveSchema>(entry.Schema.Definition)!, false).GetRawText();
        return TypedResults.Ok(history);
    }

    private static async Task<Ok<ContentItemResponse>> RestoreAsync(string id, RestoreContentRequest body, ContentSession session, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var row = await session.Store.RestoreHistoryAsync(session.Actor, id, Version(request), body.Number, ct);
        Stamp(response, row.Version); return TypedResults.Ok(await WrittenAsync(session, row, ct));
    }

    private static async Task<Ok<ContentGrantsRequest>> GrantsAsync(string id, ContentSession session, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var resource = await ResourceAsync(session, id, "manage", ct);
        var grants = await session.Db.Set<GrantsRow>().AsNoTracking().Where(x => x.ResourceId == id)
            .Select(x => new ContentGrant(x.PrincipalType, x.PrincipalId, x.Action)).ToListAsync(ct);
        Stamp(response, resource.Version); return TypedResults.Ok(new ContentGrantsRequest(grants, resource.InheritPermissions));
    }

    private static async Task<Ok<ContentResourceResponse>> SetGrantsAsync(string id, ContentGrantsRequest body, ContentSession session, IUserDirectory directory, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        foreach (var grant in body.Grants)
        {
            if (!Guid.TryParse(grant.PrincipalId, out var principal) || (grant.PrincipalType == "group"
                ? !await directory.GroupExistsAsync(principal, ct) : !await directory.IsActiveAsync(principal, ct)))
                throw new ArgumentException("Permission principal does not exist in this organization.", nameof(body));
        }
        var row = await session.Store.SetGrantsAsync(session.Actor, id, Version(request), body.Grants, body.Inherit, ct);
        Stamp(response, row.Version); return TypedResults.Ok(ContentResourceResponse.From(row));
    }

    private static async Task<Ok<ContentItemResponse>> UploadAsync(string id, ContentSession session, IBlobStore blobs, ITenantContext tenant,
        HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        await ResourceAsync(session, id, "write", ct);
        var version = Version(request);
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? throw new ArgumentException("A file is required.");
        var maximum = request.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<long>("Documents:MaxFileSize", 64 * 1024 * 1024);
        if (file.Length > maximum) throw new ArgumentException("The file exceeds the configured document size limit.");
        var key = tenant.TenantId!.Value.ToString("N") + "/content/" + Guid.CreateVersion7().ToString("N");
        await using var content = file.OpenReadStream();
        var header = new byte[PaperDotNet.Documents.Features.FileTypes.HeaderLength];
        var headerLength = await content.ReadAsync(header, ct);
        var mediaType = PaperDotNet.Documents.Features.FileTypes.Detect(header.AsSpan(0, headerLength))
            ?? throw new ArgumentException("Only PDF, TIFF, JPEG, PNG and WebP files are supported. The file content must match the format.");
        content.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct));
        content.Position = 0;
        await blobs.WriteAsync(key, content, ct);
        try
        {
            var row = await session.Store.AttachBlobAsync(session.Actor, id, version,
                new StoredContentBlob(key, Path.GetFileName(file.FileName.Replace('\\', '/')), mediaType, file.Length, hash), ct);
            Stamp(response, row.Version); return TypedResults.Ok(await WrittenAsync(session, row, ct));
        }
        catch { await blobs.DeleteAsync(key, CancellationToken.None); throw; }
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> DownloadAsync(string id, string? surface, ContentSession session, IBlobStore blobs, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        var view = await session.Store.ReadAsync(session.Actor, id, surface ?? "published", ct);
        if (view.Revision.BlobId is null) return TypedResults.NotFound();
        var blob = await session.Db.Set<BlobsRow>().AsNoTracking().SingleAsync(x => x.Id == view.Revision.BlobId, ct);
        var stream = await blobs.OpenReadAsync(blob.ObjectKey, ct);
        return stream is null ? TypedResults.NotFound() : TypedResults.File(stream, blob.ContentType, view.Revision.Filename, enableRangeProcessing: true);
    }

    private static async Task<ContentItemResponse> WrittenAsync(ContentSession session, ResourcesRow row, CancellationToken ct)
    {
        var revision = await session.Db.ItemRevisions.AsNoTracking().SingleAsync(x => x.Id == row.HeadRevisionId, ct);
        var schema = await session.Db.SchemaRevisions.AsNoTracking().SingleAsync(x => x.Id == revision.SchemaRevisionId, ct);
        return ContentItemResponse.From(new ContentView(row, revision, schema), "head");
    }

    internal static async Task<ResourcesRow> ResourceAsync(ContentSession session, string id, string action, CancellationToken ct)
    {
        var resource = await session.Db.Resources.SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct) ?? throw new KeyNotFoundException("Resource not found.");
        await session.Access.AuthorizeAsync(session.Actor, resource, action, ct); return resource;
    }

    internal static long Version(HttpRequest request)
    {
        var value = request.Headers.IfMatch.ToString().Trim('"');
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1) throw new ContentPreconditionException();
        return version;
    }
    private static void Stamp(HttpResponse response, long version) => response.Headers.ETag = "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    private static async Task<Ok<ContentMigrationPreview>> MigrationPreviewAsync(Guid id, ContentSession session, LegacyContentMigration migration, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        return TypedResults.Ok((await migration.ReadAsync(id, session, ct)).Preview);
    }

    private static async Task<Ok<ContentResourceResponse>> MigrateAsync(Guid id, ContentMigrationRequest body, ContentSession session, LegacyContentMigration migration, CancellationToken ct)
    {
        await session.OpenAsync(ct);
        await using var locked = await migration.LockAsync(ct);
        var snapshot = await migration.ReadAsync(id, session, ct);
        if (snapshot.Preview.SourceHash != body.SourceHash) throw new DbUpdateConcurrencyException("Refresh the migration preview.");
        if (snapshot.Preview.AlreadyImported) throw new InvalidOperationException("The workspace has already been imported.");
        if (snapshot.Batch is null) throw new ArgumentException(string.Join(" ", snapshot.Preview.Blockers));
        var row = await session.Store.ImportAsync(session.Actor, snapshot.Batch,
            async token => (await migration.ReadAsync(id, session, token)).Preview.SourceHash == body.SourceHash, ct);
        return TypedResults.Ok(ContentResourceResponse.From(row));
    }
}

internal sealed class ContentPreconditionException : Exception;
internal sealed class ContentErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (ContentPreconditionException) { return TypedResults.Problem(statusCode: 428, detail: "A valid If-Match header is required."); }
        catch (DbUpdateConcurrencyException) { return TypedResults.Problem(statusCode: 412, detail: "The resource changed. Reload before retrying."); }
        catch (UnauthorizedAccessException) { return TypedResults.Problem(statusCode: 403, detail: "The action is not permitted."); }
        catch (KeyNotFoundException) { return TypedResults.Problem(statusCode: 404, detail: "Resource not found."); }
        catch (DbUpdateException) { return TypedResults.Problem(statusCode: 409, detail: "The change conflicts with existing content."); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or FormatException)
        { return TypedResults.Problem(statusCode: 400, detail: exception.Message); }
    }
}
