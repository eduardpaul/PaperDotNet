using PaperDotNet.Abstractions;

namespace PaperDotNet.Identity.Contracts;

/// <summary>
/// Effective preferences of a user (PLT-17): the user's own values over the organization's
/// defaults (PLT-18) over the built-in defaults.
/// </summary>
/// <param name="Language">UI language (culture name, e.g. <c>de-DE</c>).</param>
/// <param name="TimeZone">IANA time zone (e.g. <c>Europe/Berlin</c>).</param>
/// <param name="DateFormat">Date pattern, e.g. <c>dd.MM.yyyy</c>.</param>
/// <param name="TimeFormat"><c>24h</c> or <c>12h</c>.</param>
/// <param name="NumberFormat">Culture name whose number format applies (e.g. <c>de-DE</c>).</param>
/// <param name="Theme"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="DocumentLanguages">Tesseract language codes joined with <c>+</c>, e.g. <c>deu+eng</c>.</param>
public sealed record PreferenceValues(
    string Language,
    string TimeZone,
    string DateFormat,
    string TimeFormat,
    string NumberFormat,
    string Theme,
    string DocumentLanguages)
{
    public static readonly PreferenceValues BuiltIn = new("en", "UTC", "yyyy-MM-dd", "24h", "en", "system", "eng");

    /// <summary>The time zone as <see cref="TimeZoneInfo"/> (UTC when unknown on this server).</summary>
    public TimeZoneInfo Zone =>
        TimeZone != "UTC" && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
}

/// <summary>Reads effective preferences in the current tenant, for modules that format, schedule or process for a user.</summary>
public interface IUserPreferences
{
    Task<PreferenceValues> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Effective preferences of several users (every requested id is in the result).</summary>
    Task<IReadOnlyDictionary<Guid, PreferenceValues>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>The organization's defaults (over the built-in defaults).</summary>
    Task<PreferenceValues> GetDefaultsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A user or group was deleted (IAM-14). Modules remove what only made sense for it, such as
/// permission grants and memberships.
/// </summary>
public sealed record PrincipalDeleted : IntegrationEvent
{
    public required Guid PrincipalId { get; init; }

    public required bool IsGroup { get; init; }
}
