using System.Globalization;

namespace MAAUnified.App.Services;

// Mirrors WPF LocalizationHelper.FormatVersion: appends a localized date to a version
// string for the window title (UI build date and resource last-updated date).
// WPF selects the format from LocalizationHelper.CustomCultureInfo, i.e. the GUI.CustomCulture
// config value when it names a valid culture, otherwise the OS CultureInfo.CurrentCulture;
// the UI language is NOT consulted. Keep the same semantics here: the caller passes the
// GUI.CustomCulture config value, and anything that is not a valid culture (including the
// WPF default empty string) falls back to the OS culture.
internal static class VersionDisplayFormatter
{
    public static string FormatVersion(string version, DateTime dateTime, string? customCulture)
    {
        var localDateTime = dateTime.ToLocalTime();

        // Fixed UI cultures keep their WPF display order even when the runtime runs in
        // invariant globalization mode (Linux CI / minimal containers), where
        // CultureInfo.GetCultures only exposes the invariant culture.
        switch (customCulture?.Trim().ToLowerInvariant())
        {
            case "zh-cn" or "zh-tw":
                return string.Concat(version, localDateTime.ToString("'#'MMdd", CultureInfo.InvariantCulture));
            case "en-us":
                return string.Concat(localDateTime.ToString("dd'/'MM", CultureInfo.InvariantCulture), " ", version);
            case "ja-jp":
                return string.Concat(localDateTime.ToString("MM'/'dd", CultureInfo.InvariantCulture), " ", version);
            case "ko-kr":
                return string.Concat(localDateTime.ToString("M'. 'd'.'", CultureInfo.InvariantCulture), " ", version);
        }

        var culture = ResolveCulture(customCulture);
        return culture.Name.ToLowerInvariant() switch
        {
            "zh-cn" or "zh-tw" => string.Concat(version, localDateTime.ToString("'#'MMdd", CultureInfo.InvariantCulture)),
            "en-us" => string.Concat(localDateTime.ToString("dd'/'MM", CultureInfo.InvariantCulture), " ", version),
            _ => string.Concat(
                localDateTime.ToString(GetShortDateWithoutYearPattern(culture), culture),
                " ",
                version),
        };
    }

    private static string GetShortDateWithoutYearPattern(CultureInfo culture)
    {
        return culture.DateTimeFormat.ShortDatePattern
            .Replace("yyyy", string.Empty, StringComparison.Ordinal)
            .Trim('/', '.');
    }

    private static CultureInfo ResolveCulture(string? customCulture)
    {
        if (!string.IsNullOrWhiteSpace(customCulture))
        {
            var name = customCulture.Trim();

            // WPF CustomCultureInfo only honors GUI.CustomCulture when it names a culture from
            // the OS culture list; anything else (including names ICU would synthesize, e.g.
            // "not-a-culture") falls back to CultureInfo.CurrentCulture.
            if (CultureInfo.GetCultures(CultureTypes.AllCultures)
                    .Any(culture => culture.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return new CultureInfo(name);
            }
        }

        return CultureInfo.CurrentCulture;
    }
}
