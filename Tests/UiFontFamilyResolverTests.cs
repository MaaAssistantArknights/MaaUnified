using Avalonia.Controls;
using Avalonia.Media;
using MAAUnified.App.Services;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class UiFontFamilyResolverTests
{
    [Fact]
    public void Resolve_ZhCn_WhenSimplifiedChineseFontExists_UsesFullSimplifiedChineseChain()
    {
        var resolver = new UiFontFamilyResolver(["Noto Sans CJK SC"]);

        var resolution = resolver.Resolve("zh-cn");

        Assert.Equal("zh-cn", resolution.Language);
        Assert.Equal("Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Noto Sans SC, Source Han Sans SC, WenQuanYi Micro Hei, sans-serif", resolution.Actual);
        Assert.False(resolution.RequiresDiagnostics);
    }

    [Fact]
    public void Resolve_ZhCn_WhenSimplifiedChineseFontMissing_FallsBackToTraditionalChineseThenJapanese()
    {
        var traditionalResolver = new UiFontFamilyResolver(["Microsoft JhengHei"]);
        var japaneseResolver = new UiFontFamilyResolver(["Meiryo"]);

        var traditional = traditionalResolver.Resolve("zh-cn");
        var japanese = japaneseResolver.Resolve("zh-cn");

        Assert.Equal("Microsoft JhengHei UI, Microsoft JhengHei, Noto Sans CJK TC, Noto Sans TC, Source Han Sans TC, sans-serif", traditional.Actual);
        Assert.Equal("target-language-font-missing; fallback=zh-tw", traditional.Reason);
        Assert.True(traditional.RequiresDiagnostics);

        Assert.Equal("Yu Gothic UI, Yu Gothic, Meiryo, Noto Sans CJK JP, Noto Sans JP, Source Han Sans JP, sans-serif", japanese.Actual);
        Assert.Equal("target-language-font-missing; fallback=ja-jp", japanese.Reason);
        Assert.True(japanese.RequiresDiagnostics);
    }

    [Fact]
    public void Resolve_ZhTw_WhenTraditionalChineseFontMissing_FallsBackToSimplifiedChineseThenJapanese()
    {
        var simplifiedResolver = new UiFontFamilyResolver(["Source Han Sans SC"]);
        var japaneseResolver = new UiFontFamilyResolver(["Yu Gothic"]);

        var simplified = simplifiedResolver.Resolve("zh-tw");
        var japanese = japaneseResolver.Resolve("zh-tw");

        Assert.Equal("Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Noto Sans SC, Source Han Sans SC, WenQuanYi Micro Hei, sans-serif", simplified.Actual);
        Assert.Equal("target-language-font-missing; fallback=zh-cn", simplified.Reason);

        Assert.Equal("Yu Gothic UI, Yu Gothic, Meiryo, Noto Sans CJK JP, Noto Sans JP, Source Han Sans JP, sans-serif", japanese.Actual);
        Assert.Equal("target-language-font-missing; fallback=ja-jp", japanese.Reason);
    }

    [Fact]
    public void Resolve_JapaneseAndKorean_WhenLanguageFontMissing_UseCommonCjkFallbackBeforeSansSerif()
    {
        var resolver = new UiFontFamilyResolver(["Source Han Sans TC"]);
        var missingResolver = new UiFontFamilyResolver([]);

        var japanese = resolver.Resolve("ja-jp");
        var korean = resolver.Resolve("ko-kr");
        var missing = missingResolver.Resolve("ko-kr");

        Assert.Equal("target-language-font-missing; fallback=common-cjk", japanese.Reason);
        Assert.Equal("target-language-font-missing; fallback=common-cjk", korean.Reason);
        Assert.Equal("Noto Sans CJK SC, Noto Sans CJK TC, Noto Sans CJK JP, Noto Sans CJK KR, Noto Sans SC, Noto Sans TC, Noto Sans JP, Noto Sans KR, Source Han Sans SC, Source Han Sans TC, Source Han Sans JP, Source Han Sans KR, sans-serif", japanese.Actual);

        Assert.Equal("sans-serif", missing.Actual);
        Assert.Equal("target-language-font-missing; fallback=sans-serif", missing.Reason);
    }

    [Fact]
    public void Resolve_EnglishAndPallas_UseLatinChain()
    {
        var resolver = new UiFontFamilyResolver(["Inter"]);

        var english = resolver.Resolve("en-us");
        var pallas = resolver.Resolve("pallas");

        Assert.Equal("Inter, Segoe UI, Arial, Noto Sans, sans-serif", english.Actual);
        Assert.Equal("Inter, Segoe UI, Arial, Noto Sans, sans-serif", pallas.Actual);
        Assert.False(english.RequiresDiagnostics);
        Assert.False(pallas.RequiresDiagnostics);
    }

    [Fact]
    public void ResourceUpdater_WritesStartupLanguageAndUpdatesSameResourceOnLanguageChanged()
    {
        var resources = new ResourceDictionary();
        var coordinator = new FakeUiLanguageCoordinator("zh-cn");
        var resolver = new UiFontFamilyResolver(["Noto Sans CJK SC", "Inter"]);
        var diagnostics = new List<UiFontFamilyResolution>();
        using var updater = new UiFontFamilyResourceUpdater(resources, coordinator, resolver, diagnostics.Add);

        updater.ApplyLanguage("zh-cn");
        var startupFont = Assert.IsType<FontFamily>(resources[UiFontFamilyResolver.ResourceKey]);

        coordinator.RaiseLanguageChanged("en-us");
        var switchedFont = Assert.IsType<FontFamily>(resources[UiFontFamilyResolver.ResourceKey]);

        Assert.EndsWith(
            "Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Noto Sans SC, Source Han Sans SC, WenQuanYi Micro Hei, sans-serif",
            startupFont.ToString(),
            StringComparison.Ordinal);
        Assert.EndsWith(
            "Inter, Segoe UI, Arial, Noto Sans, sans-serif",
            switchedFont.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ResourceUpdater_RecordsEachFallbackSignatureOnlyOnce()
    {
        var resources = new ResourceDictionary();
        var coordinator = new FakeUiLanguageCoordinator("zh-cn");
        var resolver = new UiFontFamilyResolver([]);
        var diagnostics = new List<UiFontFamilyResolution>();
        using var updater = new UiFontFamilyResourceUpdater(resources, coordinator, resolver, diagnostics.Add);

        updater.ApplyLanguage("zh-cn");
        updater.ApplyLanguage("zh-cn");
        coordinator.RaiseLanguageChanged("zh-cn");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("zh-cn", diagnostic.Language);
        Assert.Contains("Microsoft YaHei UI", diagnostic.Expected, StringComparison.Ordinal);
        Assert.Equal("sans-serif", diagnostic.Actual);
        Assert.Equal("target-language-font-missing; fallback=sans-serif", diagnostic.Reason);
    }

    private sealed class FakeUiLanguageCoordinator : IUiLanguageCoordinator
    {
        public FakeUiLanguageCoordinator(string currentLanguage)
        {
            CurrentLanguage = UiLanguageCatalog.Normalize(currentLanguage);
        }

        public string CurrentLanguage { get; private set; }

        public event EventHandler<UiLanguageChangedEventArgs>? LanguageChanged;

        public Task<UiOperationResult<string>> ChangeLanguageAsync(string targetLanguage, CancellationToken cancellationToken = default)
        {
            var previous = CurrentLanguage;
            CurrentLanguage = UiLanguageCatalog.Normalize(targetLanguage);
            LanguageChanged?.Invoke(this, new UiLanguageChangedEventArgs(previous, CurrentLanguage));
            return Task.FromResult(UiOperationResult<string>.Ok(CurrentLanguage, "changed"));
        }

        public void RaiseLanguageChanged(string language)
        {
            var previous = CurrentLanguage;
            CurrentLanguage = UiLanguageCatalog.Normalize(language);
            LanguageChanged?.Invoke(this, new UiLanguageChangedEventArgs(previous, CurrentLanguage));
        }
    }
}
