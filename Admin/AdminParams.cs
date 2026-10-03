using System.Globalization;
using System.Text.RegularExpressions;

// Small parsers shared by the read-only list endpoints (notes, activity). Anything that does not parse is a 400,
// never guessed.
internal static partial class AdminParams
{
    public const int MaxTextLength = 50;

    [GeneratedRegex("^[A-Za-z0-9_]{1,14}$")]
    private static partial Regex NameShape();

    public static bool IsName(string text) => NameShape().IsMatch(text);

    // A positive whole number (ticket number, keyset cursor). Empty = not given.
    public static string? TryId(string raw, string label, out long? value)
    {
        value = null;
        if (raw.Length == 0) return null;
        if (raw.Length > 18 || !raw.All(char.IsAsciiDigit) || !long.TryParse(raw, out var n) || n < 1)
            return $"{label} must be a positive number.";
        value = n;
        return null;
    }

    // An instant such as 2026-10-03T00:00:00.000Z (the page sends the viewer's local midnight as UTC).
    // `from` is inclusive, `to` is exclusive. Empty = not given.
    public static string? TryInstant(string raw, string label, out DateTime? value)
    {
        value = null;
        if (raw.Length == 0) return null;
        if (raw.Length > 40 ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ||
            parsed.Year < 2000 || parsed.Year > 2200)
            return $"{label} must be a date and time such as 2026-10-03T00:00:00Z.";
        value = parsed.UtcDateTime;
        return null;
    }

    // limit: 1..max, default when not given
    public static string? TryLimit(string raw, int fallback, int max, out int limit)
    {
        limit = fallback;
        if (raw.Length == 0) return null;
        if (!long.TryParse(raw, out var l)) return "Limit must be a number.";
        limit = (int)Math.Clamp(l, 1, max);
        return null;
    }
}
