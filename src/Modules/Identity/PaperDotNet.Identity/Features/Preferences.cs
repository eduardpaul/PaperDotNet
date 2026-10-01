using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>Effective values, and which of them are inherited (from the organization's or the built-in defaults).</summary>
public sealed record PreferencesResponse(
    string Language,
    string TimeZone,
    string DateFormat,
    string TimeFormat,
    string NumberFormat,
    string Theme,
    string DocumentLanguages,
    IReadOnlyList<string> Inherited,
    [property: JsonPropertyName("@odata.etag")] string ETag);

/// <summary>The caller's organization (tenant) with the custom host names mapped to it.</summary>
public sealed record OrganizationResponse(Guid Id, string Identifier, string DisplayName, IReadOnlyList<string> Hosts);

/// <summary>Reads effective preferences: user values over the organization's defaults over the built-in defaults.</summary>
internal sealed class UserPreferences(IdentityDbContext db) : IUserPreferences
{
    public async Task<PreferenceValues> GetAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        (await GetAsync(tenantId, [userId], cancellationToken))[userId];

    public async Task<IReadOnlyDictionary<Guid, PreferenceValues>> GetAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(db, tenantId, cancellationToken);
        var defaults = Merge(rows.FirstOrDefault(p => p.UserId == Guid.Empty), PreferenceValues.BuiltIn);
        return userIds.Distinct().ToDictionary(id => id, id => Merge(rows.FirstOrDefault(p => p.UserId == id && id != Guid.Empty), defaults));
    }

    public async Task<PreferenceValues> GetDefaultsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Merge((await RowsAsync(db, tenantId, cancellationToken)).FirstOrDefault(p => p.UserId == Guid.Empty), PreferenceValues.BuiltIn);

    /// <summary>The tenant's preference rows: only users who changed something have one.</summary>
    internal static Task<List<Preferences>> RowsAsync(IdentityDbContext database, Guid tenantId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        return context.Preferences.AsNoTracking().Where(p => p.TenantId == tenant).ToListAsync(ct);
    }

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
/// PLT-17 <c>/v1.0/me/preferences</c>, PLT-18 <c>/v1.0/organization/preferences</c> and the caller's organization.
/// Preferences change with a JSON merge patch: a property set to null goes back to the inherited value.
/// </summary>
internal static partial class PreferencesEndpoints
{
    private static readonly string[] Names = ["language", "timeZone", "dateFormat", "timeFormat", "numberFormat", "theme", "documentLanguages"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/v1.0/me").WithTags("Me").RequireAuthorization();
        me.MapGet("/preferences", GetMineAsync).WithName("GetMyPreferences");
        me.MapPatch("/preferences", PatchMineAsync).WithName("UpdateMyPreferences");

        var organization = app.MapGroup("/v1.0/organization").WithTags("Organization");
        organization.MapGet("", GetOrganizationAsync).RequireAuthorization().WithName("GetOrganization");
        organization.MapGet("/preferences", GetDefaultsAsync).RequireAuthorization().WithName("GetOrganizationPreferences");
        organization.MapPatch("/preferences", PatchDefaultsAsync).RequireScope(IdentityScopes.OrganizationManage).WithName("UpdateOrganizationPreferences");
    }

    private static async Task<Results<Ok<OrganizationResponse>, ProblemHttpResult>> GetOrganizationAsync(Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var entity = await db.Tenants.Where(t => t.Id == tenant).FirstOrDefaultAsync(ct);
        return entity is null
            ? ApiErrors.NotFound()
            : TypedResults.Ok(new OrganizationResponse(entity.Id, entity.Identifier, entity.Name, await TenantHostQueries.OfTenantAsync(database, entity.Id, cancellationToken)));
    }

    private static async Task<Ok<PreferencesResponse>> GetMineAsync(Caller caller, IdentityDbContext db, HttpResponse response, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ReadAsync(db, caller.TenantId, caller.UserId, response, cancellationToken));

    private static async Task<Ok<PreferencesResponse>> GetDefaultsAsync(Caller caller, IdentityDbContext db, HttpResponse response, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ReadAsync(db, caller.TenantId, Guid.Empty, response, cancellationToken));

    private static Task<Results<Ok<PreferencesResponse>, ValidationProblem, ProblemHttpResult>> PatchMineAsync(
        JsonElement patch, Caller caller, IdentityDbContext db, HttpRequest request, HttpResponse response, CancellationToken cancellationToken) =>
        PatchAsync(db, caller.TenantId, caller.UserId, patch, request, response, cancellationToken);

    private static Task<Results<Ok<PreferencesResponse>, ValidationProblem, ProblemHttpResult>> PatchDefaultsAsync(
        JsonElement patch, Caller caller, IdentityDbContext db, HttpRequest request, HttpResponse response, CancellationToken cancellationToken) =>
        PatchAsync(db, caller.TenantId, Guid.Empty, patch, request, response, cancellationToken);

    private static async Task<PreferencesResponse> ReadAsync(IdentityDbContext db, Guid tenantId, Guid userId, HttpResponse response, CancellationToken cancellationToken)
    {
        var rows = await UserPreferences.RowsAsync(db, tenantId, cancellationToken);
        var own = rows.FirstOrDefault(p => p.UserId == userId);
        var inherited = userId == Guid.Empty ? PreferenceValues.BuiltIn : UserPreferences.Merge(rows.FirstOrDefault(p => p.UserId == Guid.Empty), PreferenceValues.BuiltIn);
        ETags.Set(response, own?.Version ?? 0);
        return ToResponse(own, inherited);
    }

    private static async Task<Results<Ok<PreferencesResponse>, ValidationProblem, ProblemHttpResult>> PatchAsync(
        IdentityDbContext database, Guid tenantId, Guid userId, JsonElement patch, HttpRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            return ApiErrors.Validation("body", "A JSON object is expected.");
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

        var db = database;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        var own = await db.Preferences.Where(p => p.TenantId == tenant && p.UserId == user).FirstOrDefaultAsync(ct);
        if (ETags.TryGetIfMatch(request, out var expected) && expected != (own?.Version ?? 0))
        {
            return ApiErrors.PreconditionFailed();
        }

        if (own is null)
        {
            own = new Preferences { Id = Ids.New(), TenantId = tenant, UserId = user };
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

        return TypedResults.Ok(await ReadAsync(db, tenant, user, response, ct));
    }

    /// <summary>
    /// Why a preference value is not valid, or null. Culture names are checked by their shape: the server runs with
    /// invariant globalization (no ICU), and the web UI formats with the browser's data.
    /// </summary>
    internal static string? Validate(string name, string value) => name switch
    {
        "language" or "numberFormat" => value.Length <= 35 && CultureName().IsMatch(value) ? null : "A culture name is expected, e.g. en, de-DE.",
        "timeZone" => value == "UTC" || TimeZoneInfo.TryFindSystemTimeZoneById(value, out _) ? null : "An IANA time zone is expected, e.g. Europe/Berlin.",
        "dateFormat" => value.Length <= 32 && DatePattern().IsMatch(value) ? null : "A date pattern of d, M and y with separators is expected, e.g. dd.MM.yyyy.",
        "timeFormat" => value is "24h" or "12h" ? null : "24h or 12h is expected.",
        "theme" => value is "system" or "light" or "dark" ? null : "system, light or dark is expected.",
        "documentLanguages" => value.Length <= 100 && LanguageList().IsMatch(value) ? null : "Tesseract language codes joined with '+' are expected, e.g. deu+eng.",
        _ => "Unknown preference.",
    };

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
            effective.Theme, effective.DocumentLanguages, from, ETags.From(own?.Version ?? 0));
    }

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z]{4})?(-([A-Z]{2}|[0-9]{3}))?$")]
    private static partial Regex CultureName();

    [GeneratedRegex("^(?=.*d)(?=.*M)(?=.*y)[dMy]+([./ -][dMy]+)*$")]
    private static partial Regex DatePattern();

    [GeneratedRegex("^[a-z][a-z_]{1,31}(\\+[a-z][a-z_]{1,31})*$")]
    private static partial Regex LanguageList();
}
