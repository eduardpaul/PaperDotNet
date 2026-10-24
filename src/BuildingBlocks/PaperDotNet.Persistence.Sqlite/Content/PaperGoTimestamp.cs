using System.Globalization;
using System.Text.RegularExpressions;

namespace PaperDotNet.Persistence.Sqlite.Content;

/// <summary>modernc SQLite's time_format=sqlite interchange format, including nanosecond input.</summary>
internal static partial class PaperGoTimestamp
{
    public static string Format(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00+00:00";

    public static DateTime Parse(string value) => DateTimeOffset.Parse(
        Nanoseconds().Replace(value, "$1"), CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal).UtcDateTime;

    [GeneratedRegex(@"(\.\d{7})\d+")]
    private static partial Regex Nanoseconds();
}
