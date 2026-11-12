using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class LocalizationOptionChainTests
{
    private const string Scope = "Tests.LocalizationOptionChain";

    [Fact]
    public void Entry_ShouldResolveTipAndEnabledState()
    {
        var list = new LocalizedObservableList<string>(
            new UiLocalizer("zh-cn"),
            Scope,
            new LocalizedEntry<string>("Phantom", "RoguelikeThemePhantom", "傀影").WithTip("Roguelike.CoreCharTip"),
            new LocalizedEntry<string>("Mizuki", "RoguelikeThemeMizuki", "水月").WithEnabled(false));

        Assert.Equal("傀影", list[0].Display);
        Assert.Equal("不填写则使用默认策略。", list[0].Tip);
        Assert.True(list[0].IsEnabled);
        Assert.Equal("水月", list[1].Display);
        Assert.Null(list[1].Tip);
        Assert.False(list[1].IsEnabled);
    }

    [Fact]
    public void SecondaryKey_ShouldAppendNotAdaptedAndOnlyReevaluateOnRefresh()
    {
        var notAdapted = false;
        var list = new LocalizedObservableList<string>(
            new UiLocalizer("zh-cn"),
            Scope,
            new LocalizedEntry<string>("BlackFlow", "RoguelikeThemeJieGarden", "界园")
                .WithSecondaryKey("Common.NotAdapted", () => notAdapted));

        Assert.Equal("界园", list[0].Display);

        notAdapted = true;
        Assert.Equal("界园", list[0].Display);

        list.RefreshLocalization();
        Assert.Equal("界园 (未适配)", list[0].Display);
    }

    [Fact]
    public void LanguageHotSwitch_ShouldRefreshTextAndKeepSecondaryKeyCondition()
    {
        var notAdapted = true;
        var localizer = new UiLocalizer("zh-cn");
        var list = new LocalizedObservableList<string>(
            localizer,
            Scope,
            new LocalizedEntry<string>("BlackFlow", "RoguelikeThemeJieGarden", "界园")
                .WithSecondaryKey("Common.NotAdapted", () => notAdapted));

        Assert.Equal("界园 (未适配)", list[0].Display);

        localizer.Language = "en-us";
        list.RefreshLocalization();
        Assert.Equal("Sui's Garden (Not yet adapted)", list[0].Display);

        localizer.Language = "ja-jp";
        list.RefreshLocalization();
        Assert.Equal("界園 (未対応)", list[0].Display);
    }

    [Fact]
    public void Suffix_ShouldSurviveLanguageSwitch()
    {
        var localizer = new UiLocalizer("zh-cn");
        var list = new LocalizedObservableList<int>(
            localizer,
            Scope,
            new LocalizedEntry<int>(-1, "NotSwitch", "不切换").WithSuffix(" (-1)"),
            new LocalizedEntry<int>(int.MaxValue, null, "MAX (15)"));

        Assert.Equal("不切换 (-1)", list[0].Display);
        Assert.Equal("MAX (15)", list[1].Display);

        localizer.Language = "en-us";
        list.RefreshLocalization();
        Assert.Equal("Don't Switch (-1)", list[0].Display);
        Assert.Equal("MAX (15)", list[1].Display);
    }

    [Fact]
    public void AddInsertRemove_ShouldKeepEntriesAndItemsInSync()
    {
        var localizer = new UiLocalizer("zh-cn");
        var list = new LocalizedObservableList<string>(localizer, Scope);

        list.Add("Sami", "RoguelikeThemeSami");
        list.Insert(0, new LocalizedEntry<string>("Phantom", "RoguelikeThemePhantom", "傀影"));
        Assert.Equal(2, list.Count);
        Assert.Equal("傀影", list[0].Display);

        localizer.Language = "en-us";
        list.RefreshLocalization();
        Assert.Equal("Phantom", list[0].Display);
        Assert.Equal("Sami", list[1].Display);

        Assert.True(list.Remove("Phantom"));
        Assert.Equal("Sami", Assert.Single(list).Display);
        Assert.False(list.Remove("Phantom"));
    }
}
