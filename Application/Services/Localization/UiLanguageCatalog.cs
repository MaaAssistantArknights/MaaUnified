namespace MAAUnified.Application.Services.Localization;

public static class UiLanguageCatalog
{
    private static readonly string[] OrderedLanguages =
    [
        "zh-cn",
        "zh-tw",
        "en-us",
        "ja-jp",
        "ko-kr",
        "pallas",
    ];

    // Keep quick cycling on fully-maintained locales so repeated clicks stay predictable.
    private static readonly string[] QuickCycleLanguages =
    [
        "zh-cn",
        "en-us",
    ];

    public const string DefaultLanguage = "zh-cn";

    public const string PallasLanguage = "pallas";

    public const string FallbackLanguage = "en-us";

    public static IReadOnlyList<string> Ordered => OrderedLanguages;

    public static IReadOnlyList<string> QuickCycleOrdered => QuickCycleLanguages;

    public static bool IsSupported(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        return OrderedLanguages.Contains(language.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string? language)
    {
        if (!IsSupported(language))
        {
            return DefaultLanguage;
        }

        return OrderedLanguages.First(item => string.Equals(item, language, StringComparison.OrdinalIgnoreCase));
    }

    public static string NextInCycle(string? currentLanguage)
    {
        return NextInSequence(OrderedLanguages, currentLanguage);
    }

    public static string NextInQuickCycle(string? currentLanguage)
    {
        return NextInSequence(QuickCycleLanguages, currentLanguage);
    }

    private static string NextInSequence(IReadOnlyList<string> languages, string? currentLanguage)
    {
        var normalized = Normalize(currentLanguage);
        var index = -1;
        for (var i = 0; i < languages.Count; i++)
        {
            if (!string.Equals(languages[i], normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            index = i;
            break;
        }

        if (index < 0)
        {
            return languages.Count == 0 ? DefaultLanguage : languages[0];
        }

        return languages[(index + 1) % languages.Count];
    }
}

public readonly record struct LocalizationFallbackInfo(
    string Scope,
    string Language,
    string Key,
    string FallbackSource);
