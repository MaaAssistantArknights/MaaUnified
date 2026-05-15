using System.Globalization;
using Avalonia.Media;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Services;

public sealed class UiFontFamilyResolver
{
    public const string ResourceKey = "MAA.FontFamily.UI";
    public const string SansSerif = "sans-serif";

    private static readonly string[] ZhCnFamilies =
    [
        "PingFang SC",
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "Noto Sans CJK SC",
        "Noto Sans SC",
        "Source Han Sans SC",
        "WenQuanYi Micro Hei",
        SansSerif,
    ];

    private static readonly string[] ZhTwFamilies =
    [
        "PingFang TC",
        "Microsoft JhengHei UI",
        "Microsoft JhengHei",
        "Noto Sans CJK TC",
        "Noto Sans TC",
        "Source Han Sans TC",
        SansSerif,
    ];

    private static readonly string[] JaJpFamilies =
    [
        "Yu Gothic UI",
        "Yu Gothic",
        "Meiryo",
        "Noto Sans CJK JP",
        "Noto Sans JP",
        "Source Han Sans JP",
        SansSerif,
    ];

    private static readonly string[] KoKrFamilies =
    [
        "Malgun Gothic",
        "Noto Sans CJK KR",
        "Noto Sans KR",
        "Source Han Sans KR",
        SansSerif,
    ];

    private static readonly string[] EnUsFamilies =
    [
        "Inter",
        "Segoe UI",
        "Arial",
        "Noto Sans",
        SansSerif,
    ];

    private static readonly string[] CommonCjkFallbackFamilies =
    [
        "Noto Sans CJK SC",
        "Noto Sans CJK TC",
        "Noto Sans CJK JP",
        "Noto Sans CJK KR",
        "Noto Sans SC",
        "Noto Sans TC",
        "Noto Sans JP",
        "Noto Sans KR",
        "Source Han Sans SC",
        "Source Han Sans TC",
        "Source Han Sans JP",
        "Source Han Sans KR",
        SansSerif,
    ];

    private readonly HashSet<string> _installedFamilies;
    private readonly bool _probeAvaloniaFontManager;

    public UiFontFamilyResolver(IEnumerable<string>? installedFontFamilyNames = null)
    {
        if (installedFontFamilyNames is null)
        {
            _installedFamilies = LoadAvaloniaFontFamilyNames();
            _probeAvaloniaFontManager = true;
        }
        else
        {
            _installedFamilies = new HashSet<string>(
                installedFontFamilyNames.Where(static name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            _probeAvaloniaFontManager = false;
        }
    }

    public UiFontFamilyResolution Resolve(string? language)
    {
        var normalizedLanguage = UiLanguageCatalog.Normalize(language);
        var expected = GetPreferredFamilies(normalizedLanguage);

        return normalizedLanguage switch
        {
            "zh-cn" => ResolveZhCn(expected),
            "zh-tw" => ResolveZhTw(expected),
            "ja-jp" => ResolveWithCommonCjkFallback(normalizedLanguage, expected),
            "ko-kr" => ResolveWithCommonCjkFallback(normalizedLanguage, expected),
            "en-us" or "pallas" => ResolveSimple(normalizedLanguage, expected),
            _ => ResolveSimple(normalizedLanguage, expected),
        };
    }

    public static IReadOnlyList<string> GetPreferredFamilies(string? language)
    {
        var normalizedLanguage = UiLanguageCatalog.Normalize(language);
        return normalizedLanguage switch
        {
            "zh-cn" => ZhCnFamilies,
            "zh-tw" => ZhTwFamilies,
            "ja-jp" => JaJpFamilies,
            "ko-kr" => KoKrFamilies,
            "en-us" or "pallas" => EnUsFamilies,
            _ => ZhCnFamilies,
        };
    }

    private UiFontFamilyResolution ResolveZhCn(IReadOnlyList<string> expected)
    {
        if (ContainsAvailableLanguageFont(ZhCnFamilies))
        {
            return Create("zh-cn", expected, ZhCnFamilies, "target-language-candidate", requiresDiagnostics: false);
        }

        if (ContainsAvailableLanguageFont(ZhTwFamilies))
        {
            return Create("zh-cn", expected, ZhTwFamilies, "target-language-font-missing; fallback=zh-tw");
        }

        if (ContainsAvailableLanguageFont(JaJpFamilies))
        {
            return Create("zh-cn", expected, JaJpFamilies, "target-language-font-missing; fallback=ja-jp");
        }

        return CreateSansSerif("zh-cn", expected, "target-language-font-missing; fallback=sans-serif");
    }

    private UiFontFamilyResolution ResolveZhTw(IReadOnlyList<string> expected)
    {
        if (ContainsAvailableLanguageFont(ZhTwFamilies))
        {
            return Create("zh-tw", expected, ZhTwFamilies, "target-language-candidate", requiresDiagnostics: false);
        }

        if (ContainsAvailableLanguageFont(ZhCnFamilies))
        {
            return Create("zh-tw", expected, ZhCnFamilies, "target-language-font-missing; fallback=zh-cn");
        }

        if (ContainsAvailableLanguageFont(JaJpFamilies))
        {
            return Create("zh-tw", expected, JaJpFamilies, "target-language-font-missing; fallback=ja-jp");
        }

        return CreateSansSerif("zh-tw", expected, "target-language-font-missing; fallback=sans-serif");
    }

    private UiFontFamilyResolution ResolveWithCommonCjkFallback(string language, IReadOnlyList<string> expected)
    {
        var preferred = GetPreferredFamilies(language);
        if (ContainsAvailableLanguageFont(preferred))
        {
            return Create(language, expected, preferred, "target-language-candidate", requiresDiagnostics: false);
        }

        if (ContainsAvailableLanguageFont(CommonCjkFallbackFamilies))
        {
            return Create(language, expected, CommonCjkFallbackFamilies, "target-language-font-missing; fallback=common-cjk");
        }

        return CreateSansSerif(language, expected, "target-language-font-missing; fallback=sans-serif");
    }

    private UiFontFamilyResolution ResolveSimple(string language, IReadOnlyList<string> expected)
    {
        if (ContainsAvailableLanguageFont(expected))
        {
            return Create(language, expected, expected, "target-language-candidate", requiresDiagnostics: false);
        }

        return CreateSansSerif(language, expected, "target-language-font-missing; fallback=sans-serif");
    }

    private bool ContainsAvailableLanguageFont(IEnumerable<string> familyChain)
    {
        return familyChain
            .Where(static family => !IsGenericFamily(family))
            .Any(IsFamilyAvailable);
    }

    private bool IsFamilyAvailable(string family)
    {
        if (_installedFamilies.Contains(family))
        {
            return true;
        }

        if (!_probeAvaloniaFontManager)
        {
            return false;
        }

        try
        {
            var typeface = new Typeface(new FontFamily(family));
            return FontManager.Current.TryGetGlyphTypeface(typeface, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGenericFamily(string family)
    {
        return string.Equals(family, SansSerif, StringComparison.OrdinalIgnoreCase);
    }

    private static UiFontFamilyResolution CreateSansSerif(string language, IReadOnlyList<string> expected, string reason)
    {
        return Create(language, expected, [SansSerif], reason);
    }

    private static UiFontFamilyResolution Create(
        string language,
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        string reason,
        bool requiresDiagnostics = true)
    {
        var expectedText = JoinFamilies(expected);
        var actualText = JoinFamilies(actual);
        return new UiFontFamilyResolution(
            language,
            expectedText,
            actualText,
            reason,
            new FontFamily(actualText),
            requiresDiagnostics);
    }

    private static string JoinFamilies(IEnumerable<string> families)
    {
        return string.Join(", ", families);
    }

    private static HashSet<string> LoadAvaloniaFontFamilyNames()
    {
        try
        {
            return new HashSet<string>(
                FontManager.Current.SystemFonts
                    .SelectMany(static family => family.FamilyNames.DefaultIfEmpty(family.Name))
                    .Where(static name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

public sealed record UiFontFamilyResolution(
    string Language,
    string Expected,
    string Actual,
    string Reason,
    FontFamily FontFamily,
    bool RequiresDiagnostics)
{
    public string DiagnosticsSignature => string.Create(
        CultureInfo.InvariantCulture,
        $"{Language}|{Expected}|{Actual}|{Reason}");
}
