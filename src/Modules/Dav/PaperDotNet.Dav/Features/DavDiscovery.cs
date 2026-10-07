using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Api;

namespace PaperDotNet.Dav.Features;

/// <summary>Where a library is in the WebDAV tree, e.g. <c>https://host/dav/Projects/Contracts/</c>.</summary>
public sealed record WebDavLocationResponse(string Url);

/// <summary>
/// <c>GET …/lists/{listId}/webDav</c>: the WebDAV address of a library, built with the same names as the WebDAV tree
/// (duplicates and Windows-safe names included), so clients never compute them.
/// </summary>
internal static class DavDiscovery
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}", "Documents")
            .MapGet("/webDav", GetAsync)
            .RequireScope(DavModule.ReadScopes[0])
            .WithName("GetLibraryWebDav");

    /// <summary>The library's WebDAV address. 404 when the list is not a library the caller can read, or WebDAV is off.</summary>
    private static async Task<Results<Ok<WebDavLocationResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid listId, HttpContext http, IOptions<DavOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return ApiErrors.Problem(StatusCodes.Status404NotFound, "webDavDisabled", "WebDAV is turned off on this server.");
        }

        var fileSystem = ActivatorUtilities.CreateInstance<DavFileSystem>(http.RequestServices, true);
        var workspace = (await fileSystem.RootCollection.GetChildrenAsync(ct)).OfType<DavWorkspace>()
            .FirstOrDefault(w => w.WorkspaceId == workspaceId);
        var library = workspace is null
            ? null
            : (await workspace.GetChildrenAsync(ct)).OfType<DavFolder>().FirstOrDefault(f => f.List.Id == listId);
        if (library is null)
        {
            return ApiErrors.NotFound("The library was not found.");
        }

        var request = http.Request;
        return TypedResults.Ok(new WebDavLocationResponse(
            $"{request.Scheme}://{request.Host}{request.PathBase}{ApiRoutes.Dav}/{library.Path.OriginalString}"));
    }
}
