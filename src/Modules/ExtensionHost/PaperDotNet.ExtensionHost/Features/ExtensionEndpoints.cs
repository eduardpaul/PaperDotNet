using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Extensions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.ExtensionHost.Features;

public sealed record ExtensionResponse(
    string Id,
    string Name,
    string Version,
    string? Publisher,
    string? Description,
    bool Enabled,
    IReadOnlyList<ExtensionScope> Scopes,
    IReadOnlyList<ExtensionSetting> Settings,
    ExtensionContributions Contributions);

/// <summary>Installed extensions of this build and their state in the current tenant (EXT-03).</summary>
internal static class ExtensionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("extensions", "Extensions");
        group.MapGet("", ListAsync).RequireScope(ExtensionScopes.Read).WithName("ListExtensions");
        group.MapGet("/{id}", GetAsync).RequireScope(ExtensionScopes.Read).WithName("GetExtension");
        group.MapPost("/{id}/enable", EnableAsync).RequireScope(ExtensionScopes.Manage).WithName("EnableExtension");
        group.MapPost("/{id}/disable", DisableAsync).RequireScope(ExtensionScopes.Manage).WithName("DisableExtension");
        group.MapGet("/{id}/settings", GetSettingsAsync).RequireScope(ExtensionScopes.Manage).WithName("GetExtensionSettings");
        group.MapPut("/{id}/settings", ReplaceSettingsAsync).RequireScope(ExtensionScopes.Manage).WithName("ReplaceExtensionSettings").WithRequestBodySchema<System.Text.Json.Nodes.JsonObject>();
    }

    private static async Task<Ok<List<ExtensionResponse>>> ListAsync(ExtensionCatalog catalog, IExtensionState state, CancellationToken ct)
    {
        var result = new List<ExtensionResponse>();
        foreach (var extension in catalog.All.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            result.Add(ToResponse(extension, await state.IsEnabledAsync(extension.Id, ct)));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ExtensionResponse>, ProblemHttpResult>> GetAsync(string id, ExtensionCatalog catalog, IExtensionState state, CancellationToken ct) =>
        catalog.Find(id) is { } extension
            ? TypedResults.Ok(ToResponse(extension, await state.IsEnabledAsync(id, ct)))
            : ApiErrors.NotFound();

    /// <summary>
    /// Enables the extension: its content types are provisioned and its member-default scopes are
    /// added to the built-in Member role.
    /// </summary>
    private static async Task<Results<Ok<ExtensionResponse>, ProblemHttpResult>> EnableAsync(
        string id, ExtensionCatalog catalog, ExtensionsDbContext db, IRoleProvisioning roles, IContentTypeProvisioning contentTypes,
        Taxonomy.Contracts.ITermSetProvisioning termSets, ExtensionState state, CancellationToken ct)
    {
        if (catalog.Find(id) is not { } extension)
        {
            return ApiErrors.NotFound();
        }

        await EnableExtensionAsync(extension, db, roles, contentTypes, termSets, state, ct);
        return TypedResults.Ok(ToResponse(extension, enabled: true));
    }

    internal static async Task EnableExtensionAsync(
        LoadedExtension extension, ExtensionsDbContext db, IRoleProvisioning roles, IContentTypeProvisioning contentTypes,
        Taxonomy.Contracts.ITermSetProvisioning termSets, ExtensionState state, CancellationToken ct)
    {
        var row = await RowAsync(db, extension.Id, ct);
        row.Enabled = true;
        await SaveAsync(db, state, ct);
        await contentTypes.ProvisionExtensionAsync(extension.Id, ct);
        await termSets.ProvisionExtensionAsync(extension.Id, ct);
        await roles.GrantToMembersAsync([.. extension.Manifest.Scopes.Where(s => s.GrantedToMembers).Select(s => s.Name)], ct);
    }

    /// <summary>Disables the extension: its endpoints, handlers and jobs stop in this tenant; data stays.</summary>
    private static async Task<Results<Ok<ExtensionResponse>, ProblemHttpResult>> DisableAsync(
        string id, ExtensionCatalog catalog, ExtensionsDbContext db, ExtensionState state, CancellationToken ct)
    {
        if (catalog.Find(id) is not { } extension)
        {
            return ApiErrors.NotFound();
        }

        var row = await RowAsync(db, id, ct);
        row.Enabled = false;
        await SaveAsync(db, state, ct);
        return TypedResults.Ok(ToResponse(extension, enabled: false));
    }

    private static async Task<Results<Ok<JsonObject>, ProblemHttpResult>> GetSettingsAsync(string id, ExtensionCatalog catalog, IExtensionState state, CancellationToken ct) =>
        catalog.Find(id) is null ? ApiErrors.NotFound() : TypedResults.Ok(await state.GetSettingsAsync(id, ct));

    /// <summary>Replaces the settings (a JSON object); values are checked against the manifest.</summary>
    private static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> ReplaceSettingsAsync(
        string id, JsonElement body, ExtensionCatalog catalog, ExtensionsDbContext db, ExtensionState state, CancellationToken ct)
    {
        if (catalog.Find(id) is not { } extension)
        {
            return ApiErrors.NotFound();
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["settings"] = ["A JSON object is expected."] });
        }

        var (stored, effective, errors) = CheckSettings(extension, body);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var row = await RowAsync(db, id, ct, enabledIfNew: extension.Manifest.AutoEnable);
        row.Settings = stored.ToJsonString();
        await SaveAsync(db, state, ct);
        return TypedResults.Ok(effective);
    }

    /// <summary>Checks settings values (a JSON object) against the manifest: the values to store, the effective settings and problems.</summary>
    internal static (JsonObject Stored, JsonObject Effective, Dictionary<string, string[]> Errors) CheckSettings(LoadedExtension extension, JsonElement body)
    {
        var definitions = extension.Manifest.Settings.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>();
        var stored = new JsonObject();
        foreach (var property in body.EnumerateObject())
        {
            if (!definitions.TryGetValue(property.Name, out var definition))
            {
                errors[property.Name] = ["Unknown setting."];
            }
            else if (property.Value.ValueKind != JsonValueKind.Null)
            {
                if (definition.CheckValue(property.Value) is { } error)
                {
                    errors[property.Name] = [$"The value {error}"];
                }
                else
                {
                    stored[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }

        var effective = ExtensionState.Effective(extension.Manifest, stored.ToJsonString());
        foreach (var missing in extension.Manifest.Settings.Where(s => s.Required && effective[s.Name] is null))
        {
            errors.TryAdd(missing.Name, ["This setting is required."]);
        }

        return (stored, effective, errors);
    }

    internal static async Task<TenantExtension> RowAsync(ExtensionsDbContext db, string id, CancellationToken ct, bool enabledIfNew = false)
    {
        var row = await db.TenantExtensions.FirstOrDefaultAsync(e => e.ExtensionId == id, ct);
        if (row is null)
        {
            row = new TenantExtension { Id = Ids.New(), ExtensionId = id, Enabled = enabledIfNew };
            db.TenantExtensions.Add(row);
        }

        return row;
    }

    internal static async Task SaveAsync(ExtensionsDbContext db, ExtensionState state, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        state.Reset();
    }

    private static ExtensionResponse ToResponse(LoadedExtension e, bool enabled) =>
        new(e.Id, e.Manifest.Name, e.Manifest.Version, e.Manifest.Publisher, e.Manifest.Description, enabled,
            e.Manifest.Scopes, e.Manifest.Settings, e.Contributions);
}
