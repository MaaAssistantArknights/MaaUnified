using System.Text.RegularExpressions;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.Tests;

public sealed class SettingsViewStructureContractTests
{
    private static readonly Regex RootTextsQuotedKeyPattern = new("RootTexts\\[\"(Settings\\.[^\"]+)\"\\]", RegexOptions.Compiled);
    private static readonly Regex RootTextsBindingKeyPattern = new("RootTexts\\[(Settings\\.[^\\]]+)\\]", RegexOptions.Compiled);

    [Fact]
    public void PrimarySettingsViews_ShouldUseSharedSettingsFormLayoutClasses()
    {
        var root = GetMaaUnifiedRoot();
        var files =
            new[]
            {
                "App/Features/Settings/GuiSettingsView.axaml",
                "App/Features/Settings/BackgroundSettingsView.axaml",
                "App/Features/Settings/PerformanceSettingsView.axaml",
                "App/Features/Settings/StartSettingsView.axaml",
                "App/Features/Settings/RemoteControlSettingsView.axaml",
                "App/Features/Settings/ExternalNotificationSettingsView.axaml",
                "App/Features/Settings/HotKeySettingsView.axaml",
                "App/Features/Settings/TimerSettingsView.axaml",
                "App/Features/Settings/GameSettingsView.axaml",
                "App/Features/Settings/ConnectSettingsView.axaml",
            };

        foreach (var relative in files)
        {
            var text = File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("settings-form", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DependentSettingsOptions_ShouldUseConditionalVisibilityOrEnablement()
    {
        var root = GetMaaUnifiedRoot();

        var gui = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));
        Assert.Contains("IsVisible=\"{Binding CanMinimizeToTray}\"", gui, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.GUI.UseNotify]", gui, StringComparison.Ordinal);

        var start = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "StartSettingsView.axaml"));
        Assert.Contains("IsVisible=\"{Binding CanEditEmulatorLaunchSettings}\"", start, StringComparison.Ordinal);

        var timer = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "TimerSettingsView.axaml"));
        Assert.Contains("IsVisible=\"{Binding ForceScheduledStart}\"", timer, StringComparison.Ordinal);

        var achievement = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "AchievementSettingsView.axaml"));
        Assert.Contains("IsVisible=\"{Binding CanEditAchievementPopupAutoClose}\"", achievement, StringComparison.Ordinal);
        Assert.Contains("OnBackupAchievementClick", achievement, StringComparison.Ordinal);
        Assert.Contains("OnRestoreAchievementClick", achievement, StringComparison.Ordinal);
        Assert.Contains("OnAchievementDebugPointerPressed", achievement, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanUseAchievementDebugActions}\"", achievement, StringComparison.Ordinal);
        Assert.DoesNotContain("OnRefreshAchievementClick", achievement, StringComparison.Ordinal);
        Assert.DoesNotContain("OnOpenAchievementGuideClick", achievement, StringComparison.Ordinal);

        var external = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ExternalNotificationSettingsView.axaml"));
        Assert.Contains("IsEnabled=\"{Binding CanEditExternalNotification}\"", external, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanEditExternalNotificationDetails}\"", external, StringComparison.Ordinal);
        Assert.Contains("Text=\"Server Chan\"", external, StringComparison.Ordinal);
        Assert.Contains("Text=\"Telegram bot\"", external, StringComparison.Ordinal);
        Assert.Contains("Text=\"Discord WebHook\"", external, StringComparison.Ordinal);
        Assert.Contains("Text=\"SMTP\"", external, StringComparison.Ordinal);
        Assert.Contains("Text=\"Custom Webhook\"", external, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationProviderParametersText", external, StringComparison.Ordinal);

        var issue = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "IssueReportView.axaml"));
        Assert.DoesNotContain("OnOpenRuntimeLogWindowClick", issue, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.IssueReport.DeveloperMode", issue, StringComparison.Ordinal);
        Assert.Contains("IssueReportClearImageCacheTip", issue, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetSettingsViews_ShouldBindVisibleCopyToRootTextsWithoutHardcodedChinese()
    {
        var root = GetMaaUnifiedRoot();

        var versionUpdate = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "VersionUpdateSettingsView.axaml"));
        Assert.Contains("RootTexts[Settings.VersionUpdate.StartupCheck]", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.VersionUpdate.ResourceRepository]", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("启动时检查更新", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("资源仓库", versionUpdate, StringComparison.Ordinal);

        var background = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "BackgroundSettingsView.axaml"));
        Assert.Contains("RootTexts[Settings.Background.ImagePath]", background, StringComparison.Ordinal);
        Assert.DoesNotContain("背景图片", background, StringComparison.Ordinal);

        var about = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "AboutSettingsView.axaml"));
        Assert.Contains("RootTexts[Settings.Action.OpenCommunity]", about, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.Action.OpenDownload]", about, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"社区\"", about, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"下载页\"", about, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizedSettingsCombos_ShouldKeepStableSelectionsAcrossLocalizedOptionReloads()
    {
        var root = GetMaaUnifiedRoot();

        var gui = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));
        Assert.Contains("SelectedItem=\"{Binding SelectedLanguageOption, Mode=TwoWay}\"", gui, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedOperNameLanguageOption, Mode=TwoWay}\"", gui, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedThemeOption, Mode=TwoWay}\"", gui, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedInverseClearModeOption, Mode=TwoWay}\"", gui, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding SelectedLanguageValue, Mode=TwoWay}\"", gui, StringComparison.Ordinal);

        var game = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GameSettingsView.axaml"));
        Assert.Contains("SelectedItem=\"{Binding ConnectionGameSharedState.SelectedClientTypeOption, Mode=TwoWay}\"", game, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding ConnectionGameSharedState.ClientType}\"", game, StringComparison.Ordinal);

        var background = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "BackgroundSettingsView.axaml"));
        Assert.Contains("SelectedItem=\"{Binding SelectedBackgroundStretchModeOption, Mode=TwoWay}\"", background, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding BackgroundStretchMode}\"", background, StringComparison.Ordinal);

        var versionUpdate = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "VersionUpdateSettingsView.axaml"));
        Assert.Contains("SelectedItem=\"{Binding SelectedVersionUpdateVersionTypeOption, Mode=TwoWay}\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedVersionUpdateResourceSourceOption, Mode=TwoWay}\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedVersionUpdateProxyTypeOption, Mode=TwoWay}\"", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding VersionUpdateVersionType}\"", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding VersionUpdateResourceSource}\"", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding VersionUpdateProxyType}\"", versionUpdate, StringComparison.Ordinal);

        var connect = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConnectSettingsView.axaml"));
        Assert.Contains("SelectedItem=\"{Binding SelectedConnectConfigOption, Mode=TwoWay}\"", connect, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedAttachWindowScreencapOption, Mode=TwoWay}\"", connect, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedAttachWindowMouseOption, Mode=TwoWay}\"", connect, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedAttachWindowKeyboardOption, Mode=TwoWay}\"", connect, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedTouchModeOption, Mode=TwoWay}\"", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding SelectedConnectConfigValue}\"", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding SelectedAttachWindowScreencapValue}\"", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding SelectedAttachWindowMouseValue}\"", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding SelectedAttachWindowKeyboardValue}\"", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding SelectedTouchModeValue}\"", connect, StringComparison.Ordinal);

        var timer = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "TimerSettingsView.axaml"));
        Assert.Contains("SelectedItem=\"{Binding Profile, Mode=TwoWay}\"", timer, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValue=\"{Binding Profile}\"", timer, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValueBinding=\"{Binding .}\"", timer, StringComparison.Ordinal);

        var configManager = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml"));
        Assert.Contains("SelectedValueBinding=\"{Binding .}\"", configManager, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding ConfigurationManagerSelectedProfile}\"", configManager, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem=\"{Binding ConfigurationManagerSelectedProfile}\"", configManager, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsLocalizationMap_ShouldProvideEnUsForCurrentSettingsBindingsWithoutZhCnFallback()
    {
        var root = GetMaaUnifiedRoot();
        var rootTexts = new RootLocalizationTextMap("Root.Localization.Settings")
        {
            Language = "en-us",
        };
        var fallbackKeys = new List<string>();
        rootTexts.FallbackReported += info =>
        {
            if (string.Equals(info.Language, "en-us", StringComparison.OrdinalIgnoreCase)
                && info.Key.StartsWith("Settings.", StringComparison.Ordinal))
            {
                fallbackKeys.Add(info.Key);
            }
        };

        var settingsKeys = EnumerateCurrentSettingsLocalizationKeys(root);
        foreach (var key in settingsKeys)
        {
            var value = rootTexts[key];
            Assert.False(string.IsNullOrWhiteSpace(value), $"Expected a non-empty en-us value for {key}.");
            Assert.NotEqual(key, value);
        }

        var dynamicSectionValue = rootTexts["Settings.Section.{key}"];
        Assert.Equal("Settings Section: {key}", dynamicSectionValue);

        Assert.DoesNotContain(fallbackKeys, key => settingsKeys.Contains(key, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("Settings.Section.{key}", fallbackKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TimerSettingsView_ShouldRenderValidationMessageAfterTimerSlots()
    {
        var root = GetMaaUnifiedRoot();
        var timer = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "TimerSettingsView.axaml"));

        var itemsControlIndex = timer.IndexOf("<ItemsControl", StringComparison.Ordinal);
        var validationMessageIndex = timer.IndexOf("TimerValidationMessage", StringComparison.Ordinal);

        Assert.True(itemsControlIndex >= 0, "Timer settings view should contain the timer slots list.");
        Assert.True(validationMessageIndex >= 0, "Timer settings view should contain the timer validation message binding.");
        Assert.True(
            itemsControlIndex < validationMessageIndex,
            "Timer validation message should be rendered after the timer slots list.");
    }

    [Fact]
    public void ConnectAndGameSettingsViews_ShouldExposeWpfParityEntryPoints()
    {
        var root = GetMaaUnifiedRoot();

        var connect = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConnectSettingsView.axaml"));
        Assert.Contains("OnRemoveAddressHistoryClick", connect, StringComparison.Ordinal);
        Assert.Contains("SelectionCommitted=\"OnConnectAddressSelectionCommitted\"", connect, StringComparison.Ordinal);
        Assert.Contains("<controls:CheckComboBox", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("<AutoCompleteBox", connect, StringComparison.Ordinal);
        Assert.Contains("OnMuMuExtrasChecked", connect, StringComparison.Ordinal);
        Assert.Contains("OnLdPlayerExtrasChecked", connect, StringComparison.Ordinal);
        Assert.Contains("OnMuMuEmulatorPathLostFocus", connect, StringComparison.Ordinal);
        Assert.Contains("OnLdPlayerEmulatorPathLostFocus", connect, StringComparison.Ordinal);

        var game = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GameSettingsView.axaml"));
        Assert.Contains("OnOpenYoStarResolutionGuideClick", game, StringComparison.Ordinal);
        Assert.Contains("OnOpenOverseasAdaptationGuideClick", game, StringComparison.Ordinal);
        Assert.Contains("OnScriptPathDragOver", game, StringComparison.Ordinal);
        Assert.Contains("OnStartsWithScriptDrop", game, StringComparison.Ordinal);
        Assert.Contains("OnEndsWithScriptDrop", game, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlStyles_ShouldExposeSharedSettingsFormResources()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));

        Assert.Contains("x:Key=\"MAA.Size.Settings.FormMaxWidth\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"MAA.Layout.Settings.RowLabelField\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Grid.settings-form\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"WrapPanel.settings-wrap\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TextBlock.settings-label\"", text, StringComparison.Ordinal);
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }

    private static HashSet<string> EnumerateCurrentSettingsLocalizationKeys(string root)
    {
        var files =
            new[]
            {
                "App/Features/Root/SettingsView.axaml",
                "App/ViewModels/Settings/SettingsPageViewModel.cs",
            }
            .Concat(Directory.GetFiles(Path.Combine(root, "App", "Features", "Settings"), "*.axaml").Select(Path.GetFileName)!)
            .Select(relative => relative!.Replace('/', Path.DirectorySeparatorChar))
            .ToArray();

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in files)
        {
            var fullPath = relative.Contains(Path.DirectorySeparatorChar)
                ? Path.Combine(root, relative)
                : Path.Combine(root, "App", "Features", "Settings", relative);
            var text = File.ReadAllText(fullPath);

            foreach (Match match in RootTextsQuotedKeyPattern.Matches(text))
            {
                keys.Add(match.Groups[1].Value);
            }

            foreach (Match match in RootTextsBindingKeyPattern.Matches(text))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys;
    }
}
