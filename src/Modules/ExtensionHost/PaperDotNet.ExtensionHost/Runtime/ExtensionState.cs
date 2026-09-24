using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.Extensions;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.ExtensionHost.Runtime;

/// <summary>
/// Per-tenant enablement and settings of extensions. Read once per request (one small
/// indexed query) and kept for that request, so changes apply at once on every node.
/// Without a stored row the manifest's <c>autoEnable</c> applies.
/// </summary>
internal sealed class ExtensionState(ExtensionsDbContext db, ExtensionCatalog catalog, ITenantContext tenant)
    : IExtensionState, IFieldTypeAvailability, IExtensionAvailability
{
    private Dictionary<string, StateRow>? _rows;

    public async ValueTask<bool> IsEnabledAsync(string extensionId, CancellationToken cancellationToken)
    {
        if (catalog.Find(extensionId) is not { } extension || tenant.TenantId is null)
        {
            return false;
        }

        var rows = await RowsAsync(cancellationToken);
        return rows.TryGetValue(extensionId, out var row) ? row.Enabled : extension.Manifest.AutoEnable;
    }

    public async ValueTask<JsonObject> GetSettingsAsync(string extensionId, CancellationToken cancellationToken)
    {
        var extension = catalog.Find(extensionId) ?? throw new ArgumentException($"Unknown extension '{extensionId}'.", nameof(extensionId));
        var rows = tenant.TenantId is null ? new Dictionary<string, StateRow>() : await RowsAsync(cancellationToken);
        return Effective(extension.Manifest, rows.TryGetValue(extensionId, out var row) ? row.Settings : "{}");
    }

    public async ValueTask<bool> IsAvailableAsync(string fieldType, CancellationToken cancellationToken) =>
        catalog.FieldTypeOwner(fieldType) is not { } owner || await IsEnabledAsync(owner, cancellationToken);

    /// <summary>Manifest defaults overlaid with the stored values.</summary>
    internal static JsonObject Effective(ExtensionManifest manifest, string stored)
    {
        var values = JsonNode.Parse(stored) as JsonObject ?? [];
        var result = new JsonObject();
        foreach (var setting in manifest.Settings)
        {
            if (values[setting.Name] is { } value)
            {
                result[setting.Name] = value.DeepClone();
            }
            else if (setting.Default is { ValueKind: not JsonValueKind.Undefined } fallback)
            {
                result[setting.Name] = JsonNode.Parse(fallback.GetRawText());
            }
        }

        return result;
    }

    private async ValueTask<Dictionary<string, StateRow>> RowsAsync(CancellationToken ct) =>
        _rows ??= await db.TenantExtensions.AsNoTracking()
            .Select(e => new StateRow(e.ExtensionId, e.Enabled, e.Settings))
            .ToDictionaryAsync(r => r.ExtensionId, StringComparer.Ordinal, ct);

    /// <summary>Forgets the state read in this request (after a change).</summary>
    internal void Reset() => _rows = null;

    internal sealed record StateRow(string ExtensionId, bool Enabled, string Settings);
}
