using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.Extensions;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.ExtensionHost.Runtime;

/// <summary>
/// Per-tenant enablement and settings of extensions. Read once per scope and tenant (one small indexed query), so
/// changes apply at once on every node. Without a stored row the manifest's <c>autoEnable</c> applies.
/// </summary>
internal sealed class ExtensionState(ExtensionsDbContext db, ExtensionCatalog catalog) : IExtensionState, IFieldTypeAvailability, IExtensionAvailability
{
    private readonly Dictionary<Guid, Dictionary<string, TenantExtension>> _rows = [];

    public async ValueTask<bool> IsEnabledAsync(Guid tenantId, string extensionId, CancellationToken cancellationToken)
    {
        if (catalog.Find(extensionId) is not { } extension)
        {
            return false;
        }

        var rows = await RowsAsync(tenantId, cancellationToken);
        return rows.TryGetValue(extensionId, out var row) ? row.Enabled : extension.Manifest.AutoEnable;
    }

    public async ValueTask<JsonObject> GetSettingsAsync(Guid tenantId, string extensionId, CancellationToken cancellationToken)
    {
        var extension = catalog.Find(extensionId) ?? throw new ArgumentException($"Unknown extension '{extensionId}'.", nameof(extensionId));
        var rows = await RowsAsync(tenantId, cancellationToken);
        return Effective(extension.Manifest, rows.TryGetValue(extensionId, out var row) ? row.Settings : "{}");
    }

    public async ValueTask<bool> IsAvailableAsync(Guid tenantId, string fieldType, CancellationToken cancellationToken) =>
        catalog.FieldTypeOwner(fieldType) is not { } owner || await IsEnabledAsync(tenantId, owner, cancellationToken);

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

    /// <summary>Forgets what was read in this scope (after a change).</summary>
    internal void Reset() => _rows.Clear();

    private async ValueTask<Dictionary<string, TenantExtension>> RowsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!_rows.TryGetValue(tenantId, out var rows))
        {
            var context = db;
            var tenant = tenantId;
            var ct = cancellationToken;
            rows = (await context.TenantExtensions.AsNoTracking().Where(e => e.TenantId == tenant).ToListAsync(ct))
                .ToDictionary(e => e.ExtensionId, StringComparer.Ordinal);
            _rows[tenantId] = rows;
        }

        return rows;
    }
}
