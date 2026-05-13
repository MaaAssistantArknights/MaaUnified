using System.Text.RegularExpressions;
using Avalonia.Media;
using MAAUnified.App.Controls;
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
            Assert.True(
                text.Contains("settings-form", StringComparison.Ordinal)
                || text.Contains("settings-page-flow", StringComparison.Ordinal)
                || text.Contains("settings-page-two-column", StringComparison.Ordinal),
                $"{relative} should use a shared settings layout class.");
        }
    }

    [Fact]
    public void DependentSettingsOptions_ShouldUseConditionalVisibilityOrEnablement()
    {
        var root = GetMaaUnifiedRoot();

        var gui = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));
        Assert.Contains("IsVisible=\"{Binding CanMinimizeToTray}\"", gui, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.GUI.UseNotify]", gui, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.GUI.UiScalePercent]", gui, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.GUI.UiScalePercentTip]", gui, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding UiScalePercent}\"", gui, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"70\"", gui, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"140\"", gui, StringComparison.Ordinal);
        Assert.Contains("CommitValueOnLostFocus=\"True\"", gui, StringComparison.Ordinal);

        var start = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "StartSettingsView.axaml"));
        Assert.Contains("IsVisible=\"{Binding CanEditEmulatorLaunchSettings}\"", start, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-caption settings-note start-settings-note\"", start, StringComparison.Ordinal);
        Assert.Contains("Classes=\"start-settings-note-pair\"", start, StringComparison.Ordinal);
        Assert.Contains("start-settings-toggle-stack", start, StringComparison.Ordinal);
        Assert.Contains("AfterSupplementaryTextGap", start, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TextBlock.start-settings-note\"", start, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"28,-3,0,0\" />", start, StringComparison.Ordinal);
        Assert.Contains("MAA.Brush.App.Action.DisabledForeground", start, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-page-indented-note", start, StringComparison.Ordinal);

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
        Assert.Contains("ExternalNotificationCustomWebhook", external, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationProviderParametersText", external, StringComparison.Ordinal);

        var issue = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "IssueReportView.axaml"));
        Assert.Contains("Click=\"OnOpenRuntimeLogWindowClick\"", issue, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanOpenRuntimeLogWindow}\"", issue, StringComparison.Ordinal);
        Assert.Contains("Settings.IssueReport.DeveloperMode", issue, StringComparison.Ordinal);
        Assert.Contains("Settings.IssueReport.DeveloperModeNote", issue, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanUseDeveloperMode}\"", issue, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanUseIssueReportMaintenanceTools}\"", issue, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding DeveloperModeEnabled}\"", issue, StringComparison.Ordinal);
        Assert.Contains("IssueReportClearImageCacheTip", issue, StringComparison.Ordinal);
        Assert.Contains("PendingResourceUpdateSummary", issue, StringComparison.Ordinal);
        Assert.Contains("IssueReportVersionUpdateSummary", issue, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.IssueReport.CheckUpdateBeforeReportingIssue]", issue, StringComparison.Ordinal);
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
    public void SettingsShellStyles_ShouldKeepTightNotesVerticalOnly_AndProvideScrollTailSpacer()
    {
        var root = GetMaaUnifiedRoot();
        var styles = File.ReadAllText(Path.Combine(root, "App", "Styles", "SettingsShellStyles.axaml"));
        var issueReport = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "IssueReportView.axaml"));
        var settingsVm = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "Settings", "SettingsPageViewModel.cs"));

        Assert.DoesNotContain("settings-page-tight-note-stack", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.settings-page-labeled-row\">\n    <Setter Property=\"HorizontalAlignment\" Value=\"Left\" />\n    <Setter Property=\"MinHeight\"", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.settings-page-labeled-action-row\">\n    <Setter Property=\"HorizontalAlignment\" Value=\"Left\" />\n    <Setter Property=\"MinHeight\"", styles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"UserControl.settings-page CheckBox.app-checkbox\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"20\" />", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-page-indented-note", styles, StringComparison.Ordinal);

        Assert.Contains("Text=\"{Binding IssueReportPath}\"", issueReport, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasIssueReportPath}\"", issueReport, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding IssueReportStatusMessage}\"", issueReport, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasIssueReportStatusMessage}\"", issueReport, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding IssueReportErrorMessage}\"", issueReport, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasIssueReportErrorMessage}\"", issueReport, StringComparison.Ordinal);

        Assert.Contains("public bool HasIssueReportPath => !string.IsNullOrWhiteSpace(IssueReportPath);", settingsVm, StringComparison.Ordinal);
        Assert.Contains("public bool HasIssueReportStatusMessage => !string.IsNullOrWhiteSpace(IssueReportStatusMessage);", settingsVm, StringComparison.Ordinal);
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
        Assert.Contains("Text=\"{Binding ConfigurationManagerSaveAsNewSucceededText}\"", configManager, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ConfigurationManagerSaveAsNewFailedText}\"", configManager, StringComparison.Ordinal);
        Assert.Contains("HasConfigurationManagerSaveAsNewFailed", configManager, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-caption state-error\"", configManager, StringComparison.Ordinal);
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
        Assert.Contains("ItemDeleted=\"OnConnectAddressItemDeleted\"", connect, StringComparison.Ordinal);
        Assert.Contains("SelectionCommitted=\"OnConnectAddressSelectionCommitted\"", connect, StringComparison.Ordinal);
        Assert.Contains("EditorCommitted=\"OnConnectAddressEditorCommitted\"", connect, StringComparison.Ordinal);
        Assert.Contains("<controls:AppHistoryInput", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:CheckComboBox", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("<AutoCompleteBox", connect, StringComparison.Ordinal);
        Assert.Contains("OnMuMuExtrasChecked", connect, StringComparison.Ordinal);
        Assert.Contains("OnLdPlayerExtrasChecked", connect, StringComparison.Ordinal);
        Assert.Contains("OnMuMuEmulatorPathLostFocus", connect, StringComparison.Ordinal);
        Assert.Contains("OnLdPlayerEmulatorPathLostFocus", connect, StringComparison.Ordinal);

        var game = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GameSettingsView.axaml"));
        Assert.Contains("ColumnDefinitions=\"Auto,12,Auto\"", game, StringComparison.Ordinal);
        Assert.Contains("OnOpenYoStarResolutionGuideClick", game, StringComparison.Ordinal);
        Assert.Contains("OnOpenOverseasAdaptationGuideClick", game, StringComparison.Ordinal);
        Assert.Contains("OnScriptPathDragOver", game, StringComparison.Ordinal);
        Assert.Contains("OnStartsWithScriptDrop", game, StringComparison.Ordinal);
        Assert.Contains("OnEndsWithScriptDrop", game, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsViews_ShouldExposeWpfParityTooltipHints()
    {
        var root = GetMaaUnifiedRoot();

        var connect = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConnectSettingsView.axaml"));
        Assert.Contains("AlwaysAutoDetectConnectionTip", connect, StringComparison.Ordinal);

        var performance = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "PerformanceSettingsView.axaml"));
        Assert.Contains("UseGpuForInferenceTip", performance, StringComparison.Ordinal);

        var timer = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "TimerSettingsView.axaml"));
        Assert.Contains("ForceScheduledStartTip", timer, StringComparison.Ordinal);
        Assert.Contains("TimerCustomConfigTip", timer, StringComparison.Ordinal);

        var hotKey = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "HotKeySettingsView.axaml"));
        Assert.Contains("HotKeyChangingTip", hotKey, StringComparison.Ordinal);

        var gui = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));
        Assert.Contains("SystemNotificationInfo", gui, StringComparison.Ordinal);
        Assert.Contains("BadModules.UseSoftwareRenderingTip", gui, StringComparison.Ordinal);

        var versionUpdate = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "VersionUpdateSettingsView.axaml"));
        Assert.Contains("UpdateAutoCheckTip", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("UpdateCheckTip", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("UpdateSourceTip", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("ForceGithubGlobalSourceTip", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("ResourceUpdateTip", versionUpdate, StringComparison.Ordinal);

        var external = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ExternalNotificationSettingsView.axaml"));
        Assert.Contains("ExternalNotificationCustomWebhookPlaceholders", external, StringComparison.Ordinal);
        Assert.Contains("SettingsLabel", external, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsViews_ShouldKeepWpfInspiredLayoutOrder_ForKeySections()
    {
        var root = GetMaaUnifiedRoot();

        var configManager = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml"));
        Assert.Contains("OnDeleteProfileEntryClick", configManager, StringComparison.Ordinal);
        Assert.True(
            configManager.IndexOf("SelectionChanged=\"OnConfigurationProfileSelectionChanged\"", StringComparison.Ordinal)
            < configManager.IndexOf("OnCreateProfileClick", StringComparison.Ordinal),
            "Configuration manager should place profile switching before create/save-as-new.");

        var start = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "StartSettingsView.axaml"));
        Assert.Contains("Classes=\"settings-page-two-column\"", start, StringComparison.Ordinal);
        Assert.Matches("ColumnDefinitions=\"(?:Auto|\\d+),24,(?:Auto|\\d+)\"", start);

        var gui = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));
        Assert.Contains("Classes=\"settings-page-two-column\"", gui, StringComparison.Ordinal);
        Assert.True(
            gui.IndexOf("RootTexts[Settings.GUI.UseTray]", StringComparison.Ordinal)
            < gui.IndexOf("RootTexts[Settings.GUI.Language]", StringComparison.Ordinal),
            "GUI settings should render behavior toggles before selector controls.");

        var background = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "BackgroundSettingsView.axaml"));
        Assert.DoesNotContain("<WrapPanel", background, StringComparison.Ordinal);
        Assert.True(
            background.IndexOf("RootTexts[Settings.Background.ImagePath]", StringComparison.Ordinal)
            < background.IndexOf("RootTexts[Settings.Background.Opacity]", StringComparison.Ordinal)
            && background.IndexOf("RootTexts[Settings.Background.Opacity]", StringComparison.Ordinal)
            < background.IndexOf("RootTexts[Settings.Background.BlurRadius]", StringComparison.Ordinal)
            && background.IndexOf("RootTexts[Settings.Background.BlurRadius]", StringComparison.Ordinal)
            < background.IndexOf("RootTexts[Settings.Background.StretchMode]", StringComparison.Ordinal),
            "Background settings should follow the WPF-inspired top-down order.");

        var hotkey = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "HotKeySettingsView.axaml"));
        Assert.Contains("Settings.Action.RegisterHotkeys", hotkey, StringComparison.Ordinal);
        Assert.True(
            hotkey.IndexOf("ShowGuiHotkeyState.Title", StringComparison.Ordinal)
            < hotkey.IndexOf("LinkStartHotkeyState.Title", StringComparison.Ordinal),
            "Hotkey settings should keep Show GUI before Link Start.");

        var remote = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "RemoteControlSettingsView.axaml"));
        Assert.Contains("Classes=\"settings-page-inline-field-group\"", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"Auto,*,Auto\"", remote, StringComparison.Ordinal);
        Assert.True(
            remote.IndexOf("Settings.RemoteControl.GetTaskEndpoint", StringComparison.Ordinal)
            < remote.IndexOf("Settings.RemoteControl.ReportTaskEndpoint", StringComparison.Ordinal)
            && remote.IndexOf("Settings.RemoteControl.ReportTaskEndpoint", StringComparison.Ordinal)
            < remote.IndexOf("Settings.RemoteControl.PollIntervalMs", StringComparison.Ordinal),
            "Remote control fields should follow the WPF label-input row order.");

        var issueReport = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "IssueReportView.axaml"));
        Assert.Contains("ColumnDefinitions=\"*,24,*\"", issueReport, StringComparison.Ordinal);
        Assert.True(
            issueReport.IndexOf("Settings.IssueReport.Faq", StringComparison.Ordinal)
            < issueReport.IndexOf("Settings.IssueReport.IssueEntry", StringComparison.Ordinal)
            && issueReport.IndexOf("Settings.Action.BuildIssueReport", StringComparison.Ordinal)
            < issueReport.IndexOf("Settings.IssueReport.OpenRuntimeLogWindow", StringComparison.Ordinal),
            "Issue report actions should keep the WPF-inspired left/right action order.");

        var versionUpdate = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "VersionUpdateSettingsView.axaml"));
        Assert.Contains("Classes=\"settings-page-two-column\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("<Border Classes=\"grouped-card-frame\"", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("Style Selector=\"Border.version-update-card\"", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"300,24,*\"", versionUpdate, StringComparison.Ordinal);
        Assert.True(
            versionUpdate.IndexOf("Settings.VersionUpdate.ProxyAddress", StringComparison.Ordinal)
            < versionUpdate.IndexOf("<Border Classes=\"grouped-card-frame\"", StringComparison.Ordinal)
            && versionUpdate.IndexOf("Settings.VersionUpdate.ResourceApi", StringComparison.Ordinal)
            < versionUpdate.IndexOf("<Border Classes=\"grouped-card-frame\"", StringComparison.Ordinal),
            "Version update inputs should stay in the left column before the right-side version card.");
        Assert.True(
            versionUpdate.IndexOf("Settings.VersionUpdate.PanelUiVersion", StringComparison.Ordinal)
            < versionUpdate.IndexOf("Settings.VersionUpdate.SoftwareUpdate", StringComparison.Ordinal),
            "Version info should remain above the right-side action buttons.");
        Assert.True(
            versionUpdate.IndexOf("Settings.VersionUpdate.SoftwareUpdate", StringComparison.Ordinal)
            < versionUpdate.IndexOf("Settings.VersionUpdate.ResourceUpdate", StringComparison.Ordinal),
            "Version update actions should keep software update before resource update.");
        Assert.Contains("RowDefinitions=\"Auto,10,Auto\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"Auto,12,Auto,24,Auto\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("Tip=\"{DynamicResource ResourceUpdateTip}\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding VersionUpdateInlineMessage}\"", versionUpdate, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasVersionUpdateInlineMessage}\"", versionUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding VersionUpdateActivityMessage}\"", versionUpdate, StringComparison.Ordinal);
        Assert.True(
            versionUpdate.IndexOf("Settings.VersionUpdate.ResourceRepository", StringComparison.Ordinal)
            < versionUpdate.IndexOf("VersionUpdateInlineMessage", StringComparison.Ordinal),
            "Version update status should remain in the same right-side column below the action buttons.");
    }

    [Fact]
    public void MainWindowSettingsWarmup_ShouldTriggerDeferredDataAndBackgroundSectionPrewarmWhenSettingsRootLoads()
    {
        var root = GetMaaUnifiedRoot();

        var mainWindow = File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml.cs"));
        Assert.Contains("if (_settingsWarmupRootPage?.IsLoaded != true)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("if (!_settingsSectionWarmupStarted)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("MAAUnified.App.Features.Root.SettingsView.StartBackgroundSectionWarmup();", mainWindow, StringComparison.Ordinal);
        Assert.Contains("App.ForgetTask(WarmupSettingsPageAsync(vm), \"MainWindow.Settings.Warmup\");", mainWindow, StringComparison.Ordinal);
        Assert.Contains("await vm.SettingsPage.WarmupDeferredSectionDataAsync();", mainWindow, StringComparison.Ordinal);
        Assert.True(
            mainWindow.IndexOf("MAAUnified.App.Features.Root.SettingsView.StartBackgroundSectionWarmup();", StringComparison.Ordinal)
            < mainWindow.IndexOf("if (_settingsWarmupRootPage?.IsLoaded != true)", StringComparison.Ordinal),
            "Settings section control warmup should start after first screen, before waiting for the settings root page to load.");

        var settingsView = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml.cs"));
        Assert.Contains("if (_backgroundSectionWarmupTimer is not null || PrewarmedSectionKeys.Count >= SectionOrder.Length)", settingsView, StringComparison.Ordinal);
        Assert.Contains("PrewarmedSectionContentCache[sectionKey] = content;", settingsView, StringComparison.Ordinal);
        Assert.Contains("PrewarmedSectionKeys.Add(sectionKey);", settingsView, StringComparison.Ordinal);
        Assert.Contains("private static IEnumerable<string> ResolveProgressiveMaterializationTargets()", settingsView, StringComparison.Ordinal);
        Assert.Contains("return SectionOrder;", settingsView, StringComparison.Ordinal);
        Assert.Contains("MaterializeSectionsSequentiallyAsync(", settingsView, StringComparison.Ordinal);
        Assert.Contains("QueueScrollToSelectedSectionAfterLayout()", settingsView, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ShouldApplyUiScaleThroughLayoutTransformControl()
    {
        var root = GetMaaUnifiedRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml"));

        Assert.Contains("<LayoutTransformControl>", mainWindow, StringComparison.Ordinal);
        Assert.Contains("<LayoutTransformControl.LayoutTransform>", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ScaleX=\"{Binding EffectiveUiScaleFactor}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ScaleY=\"{Binding EffectiveUiScaleFactor}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ChromeScaleFactor=\"{Binding EffectiveUiScaleFactor}\"", mainWindow, StringComparison.Ordinal);

        var appFoundationStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));
        Assert.Contains("LayoutTransform=\"{Binding ChromeLayoutTransform, RelativeSource={RelativeSource TemplatedParent}}\"", appFoundationStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void AppWindowFrame_ChromeScaleFactor_ShouldUpdateChromeLayoutTransform()
    {
        var frame = new AppWindowFrame
        {
            ChromeScaleFactor = 1.6,
        };

        var transform = Assert.IsType<ScaleTransform>(frame.ChromeLayoutTransform);
        Assert.Equal(1.6, transform.ScaleX, precision: 6);
        Assert.Equal(1.6, transform.ScaleY, precision: 6);
    }

    [Fact]
    public void MacOSPackageScript_ShouldSignDotnetHostWithEntitlements()
    {
        var root = GetMaaUnifiedRoot();
        var script = File.ReadAllText(Path.Combine(root, "CI", "create-macos-app-dmg.sh"));

        Assert.Contains("com.apple.security.cs.allow-jit", script, StringComparison.Ordinal);
        Assert.Contains("com.apple.security.cs.allow-unsigned-executable-memory", script, StringComparison.Ordinal);
        Assert.Contains("com.apple.security.cs.disable-executable-page-protection", script, StringComparison.Ordinal);
        Assert.Contains("--entitlements \"$entitlements_path\"", script, StringComparison.Ordinal);
        Assert.Contains("grep -Eq 'Mach-O.*executable'", script, StringComparison.Ordinal);
        Assert.Contains("dmg_tmp_path=\"$release_dir/.$package_name.tmp.dmg\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".$package_name.dmg.tmp", script, StringComparison.Ordinal);
        Assert.Contains("create_verified_dmg()", script, StringComparison.Ordinal);
        Assert.Contains("hdiutil create -volname \"$app_name\"", script, StringComparison.Ordinal);
        Assert.Contains("hdiutil verify \"$dmg_path\"", script, StringComparison.Ordinal);
        Assert.Contains("retrying in ${delay}s", script, StringComparison.Ordinal);
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
