using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Extensions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;

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

/// <summary>Installed extensions of this build and their state in the caller's organization (EXT-03).</summary>
internal static class ExtensionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/extensions").WithTags("Extensions");
        group.MapGet("", ListAsync).RequireScope(ExtensionScopes.Read).WithName("ListExtensions");
        group.MapGet("/{id}", GetAsync).RequireScope(ExtensionScopes.Read).WithName("GetExtension");
        group.MapPost("/{id}/enable", EnableAsync).RequireScope(ExtensionScopes.Manage).WithName("EnableExtension")
            .WithDescription("Provisions the extension's content types and grants its member scopes to the Member role.");
        group.MapPost("/{id}/disable", DisableAsync).RequireScope(ExtensionScopes.Manage).WithName("DisableExtension")
            .WithDescription("Its endpoints, subscribers, mutators and jobs stop in this organization; data stays.");
        group.MapGet("/{id}/settings", GetSettingsAsync).RequireScope(ExtensionScopes.Manage).WithName("GetExtensionSettings");
        group.MapPut("/{id}/settings", ReplaceSettingsAsync).RequireScope(ExtensionScopes.Manage).WithName("ReplaceExtensionSettings");
    }

    private static async Task<Ok<List<ExtensionResponse>>> ListAsync(Caller caller, ExtensionCatalog catalog, IExtensionState state, CancellationToken cancellationToken)
    {
        var result = new List<ExtensionResponse>();
        foreach (var extension in catalog.All.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            result.Add(ToResponse(extension, await state.IsEnabledAsync(caller.TenantId, extension.Id, cancellationToken)));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ExtensionResponse>, ProblemHttpResult>> GetAsync(
        string id, Caller caller, ExtensionCatalog catalog, IExtensionState state, CancellationToken cancellationToken) =>
        catalog.Find(id) is { } extension
            ? TypedResults.Ok(ToResponse(extension, await state.IsEnabledAsync(caller.TenantId, id, cancellationToken)))
            : ApiErrors.NotFound();

    private static async Task<Results<Ok<ExtensionResponse>, ProblemHttpResult>> EnableAsync(
        string id, Caller caller, ExtensionCatalog catalog, ExtensionsDbContext db, IRoleProvisioning roles, IContentTypeProvisioning contentTypes,
        ITermSetProvisioning termSets, ExtensionState state, CancellationToken cancellationToken)
    {
        if (catalog.Find(id) is not { } extension)
        {
            return ApiErrors.NotFound();
        }

        var row = await RowAsync(db, caller.TenantId, id, enabledIfNew: false, cancellationToken);
        row.Enabled = true;
        await SaveAsync(db, state, cancellationToken);
        await contentTypes.ProvisionExtensionAsync(caller.TenantId, id, cancellationToken);
        await termSets.ProvisionExtensionAsync(caller.TenantId, id, cancellationToken);
        await roles.GrantToMembersAsync(caller.TenantId, [.. extension.Manifest.Scopes.Where(s => s.GrantedToMembers).Select(s => s.Name)], cancellationToken);
        return TypedResults.Ok(ToResponse(extension, enabled: true));
    }

    private static async Task<Results<Ok<ExtensionResponse>, ProblemHttpResult>> DisableAsync(
        string id, Caller caller, ExtensionCatalog catalog, ExtensionsDbContext db, ExtensionState state, CancellationToken cancellationToken)
    {
        if (catalog.Find(id) is not { } extension)
        {
            return ApiErrors.NotFound();
        }

        var row = await RowAsync(db, caller.TenantId, id, enabledIfNew: false, cancellationToken);
        row.Enabled = false;
        await SaveAsync(db, state, cancellationToken);
        return TypedResults.Ok(ToResponse(extension, enabled: false));
    }

    private static async Task<Results<Ok<JsonObject>, ProblemHttpResult>> GetSettingsAsync(
        string id, Caller caller, ExtensionCatalog catalog, IExtensionState state, CancellationToken cancellationToken) =>
        catalog.Find(id) is null ? ApiErrors.NotFound() : TypedResults.Ok(await state.GetSettingsAsync(caller.TenantId, id, cancellationToken));

    /// <summary>Replaces the settings (a JSON object); values are checked against the manifest.</summary>
    private static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> ReplaceSettingsAsync(
        string id, JsonElement body, Caller caller, ExtensionCatalog catalog, ExtensionsDbContext db, ExtensionState state, CancellationToken cancellationToken)
    {
        if (catalog.Find(id) is not { } extension)
        {
            return ApiErrors.NotFound();
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return ApiErrors.Validation("settings", "A JSON object is expected.");
        }

        var (stored, effective, errors) = CheckSettings(extension, body);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var row = await RowAsync(db, caller.TenantId, id, enabledIfNew: extension.Manifest.AutoEnable, cancellationToken);
        row.Settings = stored.ToJsonString();
        await SaveAsync(db, state, cancellationToken);
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

    private static async Task<TenantExtension> RowAsync(ExtensionsDbContext database, Guid tenantId, string id, bool enabledIfNew, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var extensionId = id;
        var ct = cancellationToken;
        var row = await context.TenantExtensions.Where(e => e.TenantId == tenant && e.ExtensionId == extensionId).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new TenantExtension { Id = Ids.New(), TenantId = tenantId, ExtensionId = id, Enabled = enabledIfNew };
            context.TenantExtensions.Add(row);
        }

        return row;
    }

    private static async Task SaveAsync(ExtensionsDbContext db, ExtensionState state, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        state.Reset();
    }

    private static ExtensionResponse ToResponse(LoadedExtension e, bool enabled) =>
        new(e.Id, e.Manifest.Name, e.Manifest.Version, e.Manifest.Publisher, e.Manifest.Description, enabled,
            e.Manifest.Scopes, e.Manifest.Settings, e.Contributions);
}
