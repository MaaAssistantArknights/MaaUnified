using System.Reflection;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class AchievementLocalizationTextCatalogTests
{
    // WPF parity (frozen MaaHotKeyActionHandler.HandleLinkStart): the background
    // LinkStart hotkey toasts resolve these keys in every supported UI language.
    [Theory]
    [InlineData("zh-cn", "BackgroundLinkStarted", "开始执行任务")]
    [InlineData("zh-cn", "BackgroundLinkStopped", "任务终止")]
    [InlineData("zh-tw", "BackgroundLinkStarted", "開始執行任務")]
    [InlineData("zh-tw", "BackgroundLinkStopped", "任務終止")]
    [InlineData("en-us", "BackgroundLinkStarted", "Tasks started")]
    [InlineData("en-us", "BackgroundLinkStopped", "Tasks stopped")]
    [InlineData("ja-jp", "BackgroundLinkStarted", "Tasks started")]
    [InlineData("ja-jp", "BackgroundLinkStopped", "Tasks stopped")]
    [InlineData("ko-kr", "BackgroundLinkStarted", "작업 시작됨")]
    [InlineData("ko-kr", "BackgroundLinkStopped", "작업 중지됨")]
    public void BackgroundLinkStartToastKeys_ShouldResolveInAllUiLanguages(
        string language,
        string key,
        string expected)
    {
        Assert.Equal(expected, AchievementTextCatalog.GetString(key, language));
    }

    [Theory]
    [InlineData("ja-jp", new[] { "ja-jp", "en-us", "zh-cn" })]
    [InlineData("ko-kr", new[] { "ko-kr", "en-us", "zh-cn" })]
    [InlineData("zh-tw", new[] { "zh-tw", "zh-cn", "en-us" })]
    public void BuildLookupOrder_ShouldPrioritizeRequestedLanguage(string language, string[] expected)
    {
        var method = typeof(AchievementTextCatalog).GetMethod(
            "BuildLookupOrder",
            BindingFlags.NonPublic | BindingFlags.Static);

        var actual = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, [language]));
        Assert.Equal(expected, actual);
    }
}
