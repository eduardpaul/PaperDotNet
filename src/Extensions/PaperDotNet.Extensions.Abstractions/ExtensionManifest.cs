using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PaperDotNet.Extensions;

/// <summary>
/// The language-neutral extension manifest (EXT-01): <c>extension.json</c>, embedded in
/// .NET extensions as <see cref="ExtensionSdk.ManifestResource"/>. Its JSON Schema is
/// published at <c>docs/schemas/extension-manifest.schema.json</c>.
/// </summary>
public sealed partial record ExtensionManifest
{
    /// <summary>Reverse-DNS style id, e.g. <c>acme.invoices</c>; prefixes its scopes, field types and routes.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>SemVer of the extension, e.g. <c>1.2.0</c>.</summary>
    public required string Version { get; init; }

    public string? Publisher { get; init; }

    public string? Description { get; init; }

    /// <summary>SDK version the extension was built against (<c>major.minor</c>).</summary>
    public required string SdkVersion { get; init; }

    /// <summary>Enable automatically in new tenants.</summary>
    public bool AutoEnable { get; init; }

    /// <summary>Permission scopes the extension adds (IAM-13); names start with <c>{id}.</c>.</summary>
    public IReadOnlyList<ExtensionScope> Scopes { get; init; } = [];

    /// <summary>Settings tenant admins can change.</summary>
    public IReadOnlyList<ExtensionSetting> Settings { get; init; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ExtensionManifest Parse(Stream json) =>
        JsonSerializer.Deserialize<ExtensionManifest>(json, JsonOptions) ?? throw new JsonException("The manifest is empty.");

    /// <summary>Checks the manifest against the rules and the host's SDK version; returns all problems.</summary>
    public IReadOnlyList<string> Validate(Version hostSdk)
    {
        var errors = new List<string>();
        if (!IdPattern().IsMatch(Id ?? string.Empty))
        {
            errors.Add($"id '{Id}' must be lowercase, dot-separated, with at least two parts (e.g. acme.invoices).");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("name is required.");
        }

        if (!SemVerPattern().IsMatch(Version ?? string.Empty))
        {
            errors.Add($"version '{Version}' must be a semantic version (e.g. 1.0.0).");
        }

        if (!System.Version.TryParse(SdkVersion, out var sdk))
        {
            errors.Add($"sdkVersion '{SdkVersion}' must be major.minor.");
        }
        else if (sdk.Major != hostSdk.Major || sdk.Minor > hostSdk.Minor)
        {
            errors.Add($"sdkVersion {SdkVersion} is not supported by this host (SDK {hostSdk.ToString(2)}).");
        }

        foreach (var scope in Scopes)
        {
            if (!scope.Name.StartsWith(Id + ".", StringComparison.Ordinal) || !ScopePattern().IsMatch(scope.Name))
            {
                errors.Add($"scope '{scope.Name}' must start with '{Id}.' and use lowercase letters, digits and dots.");
            }
        }

        errors.AddRange(Scopes.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"scope '{g.Key}' is declared twice."));
        foreach (var setting in Settings)
        {
            errors.AddRange(setting.Validate());
        }

        errors.AddRange(Settings.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"setting '{g.Key}' is declared twice."));
        return errors;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+(-[0-9A-Za-z.-]+)?(\\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVerPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)+$")]
    private static partial Regex ScopePattern();
}

public sealed record ExtensionScope(string Name, string Description, bool GrantedToMembers = false);

public enum ExtensionSettingType
{
    Text,
    Number,
    Boolean,
    Choice,
}

/// <summary>A tenant-level setting of an extension.</summary>
public sealed partial record ExtensionSetting
{
    public required string Name { get; init; }

    public ExtensionSettingType Type { get; init; } = ExtensionSettingType.Text;

    public string? Description { get; init; }

    public bool Required { get; init; }

    public JsonElement? Default { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = [];

    internal IEnumerable<string> Validate()
    {
        if (!NamePattern().IsMatch(Name ?? string.Empty))
        {
            yield return $"setting '{Name}' must be camelCase letters and digits.";
        }

        if (Type == ExtensionSettingType.Choice && Choices.Count == 0)
        {
            yield return $"setting '{Name}': choice settings need choices.";
        }

        if (Default is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value && CheckValue(value) is { } error)
        {
            yield return $"setting '{Name}': default {error}";
        }
    }

    /// <summary>Null when <paramref name="value"/> fits the setting's type; otherwise the problem.</summary>
    public string? CheckValue(JsonElement value) => Type switch
    {
        ExtensionSettingType.Text when value.ValueKind != JsonValueKind.String => "must be text.",
        ExtensionSettingType.Number when value.ValueKind != JsonValueKind.Number => "must be a number.",
        ExtensionSettingType.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) => "must be true or false.",
        ExtensionSettingType.Choice when value.ValueKind != JsonValueKind.String || !Choices.Contains(value.GetString()!) =>
            string.Create(CultureInfo.InvariantCulture, $"must be one of: {string.Join(", ", Choices)}."),
        _ => null,
    };

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,62}$")]
    private static partial Regex NamePattern();
}
