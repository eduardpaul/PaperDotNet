using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using PaperDotNet.Api;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>Page images and thumbnails (DOC-04) as the library's workflows made them (ADR-0038); without them a library has none (404).</summary>
internal static class PageImageEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var file = endpoints.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/file").WithTags("Documents");
        file.MapGet("/pages/{page:int}/image", PageImageAsync).RequireScope(DocumentScopes.Read).WithName("GetPageImage")
            .WithDescription("A page as JPEG as the library's \"Render pages\" workflow made it: width 800 (default) or 1600 pixels; version selects an older file version.");
        file.MapGet("/thumbnail", ThumbnailAsync).RequireScope(DocumentScopes.Read).WithName("GetThumbnail")
            .WithDescription("The thumbnail of the first page, as the library's \"Make thumbnails\" workflow made it.");
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> PageImageAsync(
        Guid workspaceId, Guid listId, Guid itemId, int page, int? width, int? version, Caller caller, IListItemStore items, DocumentsDbContext db,
        PageRenderer renderer, CancellationToken cancellationToken)
    {
        if (await FileVersionAsync(workspaceId, listId, itemId, version, caller, items, db, cancellationToken) is not { } fileVersion)
        {
            return ApiErrors.NotFound("The item has no file.");
        }

        var wanted = PageRenderer.PageWidths.FirstOrDefault(w => w >= (width ?? 0), PageRenderer.PageWidths[^1]);
        foreach (var size in PageRenderer.PageWidths.OrderBy(w => w == wanted ? 0 : 1))
        {
            if (await renderer.OpenStoredAsync(fileVersion, page, size, cancellationToken) is { } image)
            {
                return TypedResults.File(image, "image/jpeg", entityTag: new EntityTagHeaderValue($"\"{fileVersion.Sha256}-{page}-{size}\""));
            }
        }

        return ApiErrors.NotFound("The page has no image: the library's \"Render pages\" workflow has not made it (or is off).");
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> ThumbnailAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, DocumentsDbContext db, PageRenderer renderer,
        CancellationToken cancellationToken)
    {
        if (await FileVersionAsync(workspaceId, listId, itemId, null, caller, items, db, cancellationToken) is not { } fileVersion)
        {
            return ApiErrors.NotFound("The item has no file.");
        }

        return await renderer.OpenStoredAsync(fileVersion, 1, PageRenderer.Widths[0], cancellationToken) is { } image
            ? TypedResults.File(image, "image/jpeg", entityTag: new EntityTagHeaderValue($"\"{fileVersion.Sha256}-thumbnail\""))
            : ApiErrors.NotFound("The document has no thumbnail: the library's \"Make thumbnails\" workflow has not made it (or is off).");
    }

    private static async Task<FileVersion?> FileVersionAsync(
        Guid workspaceId, Guid listId, Guid itemId, int? version, Caller caller, IListItemStore items, DocumentsDbContext db, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null)
        {
            return null;
        }

        return version is { } number
            ? await DocumentQueries.VersionAsync(db, caller.TenantId, itemId, number, ct)
            : await DocumentQueries.CurrentAsync(db, caller.TenantId, itemId, ct);
    }
}
