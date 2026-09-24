using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Lists.Templates;

public sealed record ListTemplateContentType(string Key, string Name);

public sealed record ListTemplateResponse(
    string Key,
    string Name,
    string? Description,
    bool IsLibrary,
    bool Versioning,
    IReadOnlyList<ListTemplateContentType> ContentTypes,
    IReadOnlyList<string> Views,
    string? ExtensionId);

/// <summary>List templates available in the current tenant (built-in and from enabled extensions, LST-16).</summary>
internal static class ListTemplateEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapV1Group("listTemplates", "Lists")
            .MapGet("", ListAsync)
            .RequireScope(ListScopes.Read)
            .WithName("ListListTemplates");

    private static async Task<Ok<List<ListTemplateResponse>>> ListAsync(ListTemplateRegistry registry, IExtensionAvailability extensions, CancellationToken ct)
    {
        var result = new List<ListTemplateResponse>();
        foreach (var template in registry.Lists.OrderBy(t => t.ExtensionId is not null).ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            if (template.ExtensionId is { } owner && !await extensions.IsEnabledAsync(owner, ct))
            {
                continue;
            }

            result.Add(new ListTemplateResponse(
                template.Key,
                template.Name,
                template.Description,
                template.IsLibrary,
                template.Versioning,
                template.ContentTypeKeys.Select(k => new ListTemplateContentType(k, registry.FindContentType(k)!.Name)).ToList(),
                template.Views.Select(v => v.Name).ToList(),
                template.ExtensionId));
        }

        return TypedResults.Ok(result);
    }
}
