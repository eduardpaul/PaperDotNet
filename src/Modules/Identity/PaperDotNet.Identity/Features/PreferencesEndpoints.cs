using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>
/// Effective values, and which of them are inherited (from the organization's or the built-in defaults).
/// </summary>
public sealed record PreferencesResponse(
    string Language,
    string TimeZone,
    string DateFormat,
    string TimeFormat,
    string NumberFormat,
    string Theme,
    string DocumentLanguages,
    IReadOnlyList<string> Inherited)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

/// <summary>PATCH body (JSON merge patch): set a value, or null to inherit it again.</summary>
public sealed record PreferencesPatch(
    string? Language, string? TimeZone, string? DateFormat, string? TimeFormat, string? NumberFormat, string? Theme, string? DocumentLanguages);

/// <summary>Reads effective preferences: user values over the organization's defaults over the built-in defaults.</summary>
internal sealed class UserPreferences(IdentityDbContext db) : IUserPreferences
{
    public async Task<PreferenceValues> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        (await GetAsync([userId], cancellationToken))[userId];

    public async Task<IReadOnlyDictionary<Guid, PreferenceValues>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var rows = await db.Preferences.AsNoTracking()
            .Where(p => p.UserId == Guid.Empty || userIds.Contains(p.UserId))
            .ToListAsync(cancellationToken);
        var defaults = Merge(rows.FirstOrDefault(p => p.UserId == Guid.Empty), PreferenceValues.BuiltIn);
        return userIds.Distinct().ToDictionary(id => id, id => Merge(rows.FirstOrDefault(p => p.UserId == id && id != Guid.Empty), defaults));
    }

    public async Task<PreferenceValues> GetDefaultsAsync(CancellationToken cancellationToken) =>
        Merge(await db.Preferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == Guid.Empty, cancellationToken), PreferenceValues.BuiltIn);

    internal static PreferenceValues Merge(Preferences? own, PreferenceValues inherited) =>
        own is null
            ? inherited
            : new PreferenceValues(
                own.Language ?? inherited.Language,
                own.TimeZone ?? inherited.TimeZone,
                own.DateFormat ?? inherited.DateFormat,
                own.TimeFormat ?? inherited.TimeFormat,
                own.NumberFormat ?? inherited.NumberFormat,
                own.Theme ?? inherited.Theme,
                own.DocumentLanguages ?? inherited.DocumentLanguages);
}

/// <summary>
/// PLT-17 <c>/v1.0/me/preferences</c> and PLT-18 <c>/v1.0/organization/preferences</c>. Both are changed with a
/// JSON merge patch: a property set to null goes back to the inherited value.
/// </summary>
internal static partial class PreferencesEndpoints
{
    private static readonly string[] Names = ["language", "timeZone", "dateFormat", "timeFormat", "numberFormat", "theme", "documentLanguages"];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapV1Group("me", "Me");
        me.MapGet("/preferences", GetMineAsync).WithName("GetMyPreferences");
        me.MapPatch("/preferences", PatchMineAsync).WithName("UpdateMyPreferences").WithRequestBodySchema<PreferencesPatch>();

        var organization = endpoints.MapV1Group("organization", "Organization");
        organization.MapGet("/preferences", GetDefaultsAsync).WithName("GetOrganizationPreferences");
        organization.MapPatch("/preferences", PatchDefaultsAsync).RequireScope(IdentityScopes.OrganizationManage).WithName("UpdateOrganizationPreferences").WithRequestBodySchema<PreferencesPatch>();
    }

    private static async Task<Results<Ok<PreferencesResponse>, ProblemHttpResult>> GetMineAsync(
        ICurrentUser user, IdentityDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Preferences belong to a user.");
        }

        return TypedResults.Ok(await ReadAsync(db, userId, response, ct));
    }

    private static async Task<Ok<PreferencesResponse>> GetDefaultsAsync(IdentityDbContext db, HttpResponse response, CancellationToken ct) =>
        TypedResults.Ok(await ReadAsync(db, Guid.Empty, response, ct));

    private static async Task<Results<Ok<PreferencesResponse>, ValidationProblem, ProblemHttpResult>> PatchMineAsync(
        JsonElement patch, ICurrentUser user, IdentityDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Preferences belong to a user.");
        }

        return await PatchAsync(db, userId, patch, http, response, ct);
    }

    private static Task<Results<Ok<PreferencesResponse>, ValidationProblem, ProblemHttpResult>> PatchDefaultsAsync(
        JsonElement patch, IdentityDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct) =>
        PatchAsync(db, Guid.Empty, patch, http, response, ct);

    private static async Task<PreferencesResponse> ReadAsync(IdentityDbContext db, Guid userId, HttpResponse response, CancellationToken ct)
    {
        var rows = await db.Preferences.AsNoTracking().Where(p => p.UserId == Guid.Empty || p.UserId == userId).ToListAsync(ct);
        var defaults = rows.FirstOrDefault(p => p.UserId == Guid.Empty);
        var own = rows.FirstOrDefault(p => p.UserId == userId);
        var inherited = userId == Guid.Empty ? PreferenceValues.BuiltIn : UserPreferences.Merge(defaults, PreferenceValues.BuiltIn);
        ETags.Set(response, own?.Version ?? 0);
        return ToResponse(own, inherited);
    }

    private static async Task<Results<Ok<PreferencesResponse>, ValidationProblem, ProblemHttpResult>> PatchAsync(
        IdentityDbContext db, Guid userId, JsonElement patch, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["body"] = ["A JSON object is expected."] });
        }

        var errors = new Dictionary<string, string[]>();
        var values = new Dictionary<string, string?>();
        foreach (var property in patch.EnumerateObject())
        {
            if (!Names.Contains(property.Name, StringComparer.Ordinal))
            {
                errors[property.Name] = ["Unknown preference."];
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                values[property.Name] = null;
                continue;
            }

            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()!.Trim() : null;
            if ((value is null ? "A string or null is expected." : Validate(property.Name, value)) is { } error)
            {
                errors[property.Name] = [error];
                continue;
            }

            values[property.Name] = value;
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var own = await db.Preferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (ETags.TryGetIfMatch(http, out var expected) && expected != (own?.Version ?? 0))
        {
            return ApiErrors.PreconditionFailed();
        }

        if (own is null)
        {
            own = new Preferences { Id = Ids.New(), UserId = userId };
            db.Preferences.Add(own);
        }

        foreach (var (name, value) in values)
        {
            switch (name)
            {
                case "language": own.Language = value; break;
                case "timeZone": own.TimeZone = value; break;
                case "dateFormat": own.DateFormat = value; break;
                case "timeFormat": own.TimeFormat = value; break;
                case "numberFormat": own.NumberFormat = value; break;
                case "theme": own.Theme = value; break;
                case "documentLanguages": own.DocumentLanguages = value; break;
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiErrors.PreconditionFailed();
        }

        return TypedResults.Ok(await ReadAsync(db, userId, response, ct));
    }

    internal static string? Validate(string name, string value) => name switch
    {
        "language" or "numberFormat" => IsCulture(value) ? null : "A culture name is expected, e.g. en, de-DE.",
        "timeZone" => value == "UTC" || TimeZoneInfo.TryFindSystemTimeZoneById(value, out _) ? null : "An IANA time zone is expected, e.g. Europe/Berlin.",
        "dateFormat" => value.Length <= 32 && DatePattern().IsMatch(value) ? null : "A date pattern of d, M and y with separators is expected, e.g. dd.MM.yyyy.",
        "timeFormat" => value is "24h" or "12h" ? null : "24h or 12h is expected.",
        "theme" => value is "system" or "light" or "dark" ? null : "system, light or dark is expected.",
        "documentLanguages" => value.Length <= 100 && LanguageList().IsMatch(value) ? null : "Tesseract language codes joined with '+' are expected, e.g. deu+eng.",
        _ => "Unknown preference.",
    };

    private static bool IsCulture(string name)
    {
        if (name.Length is 0 or > 35)
        {
            return false;
        }

        try
        {
            return !CultureInfo.GetCultureInfo(name, predefinedOnly: true).Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static PreferencesResponse ToResponse(Preferences? own, PreferenceValues inherited)
    {
        var effective = UserPreferences.Merge(own, inherited);
        var from = new List<string>();
        void Check(string name, string? value)
        {
            if (value is null)
            {
                from.Add(name);
            }
        }

        Check("language", own?.Language);
        Check("timeZone", own?.TimeZone);
        Check("dateFormat", own?.DateFormat);
        Check("timeFormat", own?.TimeFormat);
        Check("numberFormat", own?.NumberFormat);
        Check("theme", own?.Theme);
        Check("documentLanguages", own?.DocumentLanguages);
        return new PreferencesResponse(
            effective.Language, effective.TimeZone, effective.DateFormat, effective.TimeFormat, effective.NumberFormat,
            effective.Theme, effective.DocumentLanguages, from)
        { ETag = ETags.From(own?.Version ?? 0) };
    }

    [GeneratedRegex("^(?=.*d)(?=.*M)(?=.*y)[dMy]+([./ -][dMy]+)*$")]
    private static partial Regex DatePattern();

    [GeneratedRegex("^[a-z][a-z_]{1,31}(\\+[a-z][a-z_]{1,31})*$")]
    private static partial Regex LanguageList();
}
