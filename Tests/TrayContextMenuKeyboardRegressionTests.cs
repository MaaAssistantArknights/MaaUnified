using Avalonia.Input;
using MAAUnified.App.Controls;
using MAAUnified.App.Features.Advanced;
using MAAUnified.App.Features.Root;
using MAAUnified.App.Features.Settings;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class TrayContextMenuKeyboardRegressionTests
{
    private static readonly string[] UiLanguages =
    [
        "zh-cn",
        "zh-tw",
        "en-us",
        "ja-jp",
        "ko-kr",
        "pallas",
    ];

    [Fact]
    public void TrayContextMenu_ShouldFocusFirstItemAndCloseThroughPresenterDismissal()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Views", "TrayContextMenuWindow.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Views", "TrayContextMenuWindow.axaml.cs"));

        Assert.Contains("DismissRequested=\"OnMenuDismissRequested\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MenuPresenter.FocusFirstEnabledItem();", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Input", code, StringComparison.Ordinal);
        Assert.Contains("private void OnMenuDismissRequested", code, StringComparison.Ordinal);
        Assert.Contains("Close();", code, StringComparison.Ordinal);
        Assert.Equal(AppPopupMenuKeyboardAction.MoveNext, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Down));
        Assert.Equal(AppPopupMenuKeyboardAction.Invoke, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Enter));
        Assert.Equal(AppPopupMenuKeyboardAction.Dismiss, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Escape));
    }

    [Fact]
    public void TaskQueueAndCopilotRows_ShouldExposeKeyboardAndContextMenuRoutes()
    {
        Assert.Equal(
            TaskQueueView.TaskQueueRowMenuKeyboardAction.OpenMenu,
            TaskQueueView.GetTaskQueueRowMenuKeyboardAction(Key.Enter, KeyModifiers.None));
        Assert.Equal(
            TaskQueueView.TaskQueueRowMenuKeyboardAction.OpenMenu,
            TaskQueueView.GetTaskQueueRowMenuKeyboardAction(Key.Space, KeyModifiers.None));
        Assert.Equal(
            TaskQueueView.TaskQueueRowMenuKeyboardAction.OpenMenu,
            TaskQueueView.GetTaskQueueRowMenuKeyboardAction(Key.F10, KeyModifiers.Shift));
        Assert.Equal(
            TaskQueueView.TaskQueueRowMenuKeyboardAction.OpenMenu,
            TaskQueueView.GetTaskQueueRowMenuKeyboardAction(Key.Apps, KeyModifiers.None));
        Assert.Equal(
            TaskQueueView.TaskQueueRowMenuKeyboardAction.None,
            TaskQueueView.GetTaskQueueRowMenuKeyboardAction(Key.F10, KeyModifiers.None));

        Assert.Equal(
            CopilotView.CopilotListItemKeyboardAction.Load,
            CopilotView.GetCopilotListItemKeyboardAction(Key.Enter, KeyModifiers.None));
        Assert.Equal(
            CopilotView.CopilotListItemKeyboardAction.Load,
            CopilotView.GetCopilotListItemKeyboardAction(Key.Space, KeyModifiers.None));
        Assert.Equal(
            CopilotView.CopilotListItemKeyboardAction.OpenMenu,
            CopilotView.GetCopilotListItemKeyboardAction(Key.F10, KeyModifiers.Shift));
        Assert.Equal(
            CopilotView.CopilotListItemKeyboardAction.OpenMenu,
            CopilotView.GetCopilotListItemKeyboardAction(Key.Apps, KeyModifiers.None));

        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var taskQueueXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));
        var taskQueueCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml.cs"));
        var copilotXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));
        var copilotCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml.cs"));
        var foundationStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));

        Assert.Contains("InputElement.KeyDownEvent", taskQueueCode, StringComparison.Ordinal);
        Assert.Contains("OnTaskListKeyDown", taskQueueCode, StringComparison.Ordinal);
        Assert.Contains("Control.ContextRequestedEvent", taskQueueCode, StringComparison.Ordinal);
        Assert.Contains("OnTaskListContextRequested", taskQueueCode, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnTaskGearKeyDown\"", taskQueueXaml, StringComparison.Ordinal);
        Assert.Contains("ContextRequested=\"OnTaskGearContextRequested\"", taskQueueXaml, StringComparison.Ordinal);
        Assert.Contains("BuildTaskMenuItems(task)", taskQueueCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Focusable\" Value=\"False", foundationStyles, StringComparison.Ordinal);

        Assert.Contains("KeyDown=\"OnCopilotListItemBodyKeyDown\"", copilotXaml, StringComparison.Ordinal);
        Assert.Contains("ContextRequested=\"OnCopilotListItemBodyContextRequested\"", copilotXaml, StringComparison.Ordinal);
        Assert.Contains("InputElement.KeyDownEvent", copilotCode, StringComparison.Ordinal);
        Assert.Contains("OnCopilotListKeyDown", copilotCode, StringComparison.Ordinal);
        Assert.Contains("Control.ContextRequestedEvent", copilotCode, StringComparison.Ordinal);
        Assert.Contains("OnCopilotListContextRequested", copilotCode, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnDeleteListItemKeyDown\"", copilotXaml, StringComparison.Ordinal);
        Assert.Contains("ContextRequested=\"OnDeleteListItemContextRequested\"", copilotXaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\"", copilotXaml, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"True\"", copilotXaml, StringComparison.Ordinal);
        Assert.Contains("BuildCopilotListMenuItems(item)", copilotCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MiniGameRunningState_ShouldLockOptionEditingUntilStopped()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        Assert.False(vm.IsMiniGameRunning);
        Assert.True(vm.CanEditMiniGameOptions);

        await vm.StartMiniGameAsync();

        Assert.True(vm.IsMiniGameRunning);
        Assert.False(vm.CanEditMiniGameOptions);

        await vm.StopActiveToolAsync();

        Assert.False(vm.IsMiniGameRunning);
        Assert.True(vm.CanEditMiniGameOptions);

        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "ToolboxMiniGameView.axaml"));
        Assert.Contains("IsEnabled=\"{Binding CanEditMiniGameOptions}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Idle}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Inited}", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LocalizationKeyGroups))]
    public void NewFunctionalKeys_ShouldResolveWithoutFallback_InAllConfiguredLanguages(
        string group,
        IReadOnlyList<string> keys)
    {
        Assert.False(string.IsNullOrWhiteSpace(group));
        var manager = UiLocalizationResourceManager.Shared;
        var english = keys.ToDictionary(
            key => key,
            key => manager.Lookup("en-us", key).Value,
            StringComparer.Ordinal);

        foreach (var language in UiLanguages)
        {
            foreach (var key in keys)
            {
                var result = manager.Lookup(language, key);
                Assert.Equal("none", result.FallbackSource);
                Assert.False(string.IsNullOrWhiteSpace(result.Value));
                Assert.NotEqual(key, result.Value);
                if (!string.Equals(language, "en-us", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.NotEqual(english[key], result.Value);
                }
            }
        }
    }

    public static TheoryData<string, IReadOnlyList<string>> LocalizationKeyGroups => new()
    {
        {
            "copilot",
            new[]
            {
                "Copilot.Status.Starting",
                "Copilot.Status.ReorderSuccess",
                "Copilot.Status.UpdateListItemSuccess",
                "Copilot.Error.SortStateOutOfSync",
                "Copilot.Error.TaskListPersistFail",
                "Copilot.Status.LoadSetPartialFailed",
            }
        },
        {
            "functional settings copy",
            new[]
            {
                "Settings.SensitiveStorage.Unavailable",
                "Settings.SensitiveStorage.Retry",
                "Settings.SensitiveStorage.DiscardUnreadable",
                "Settings.SensitiveStorage.Status.PlaintextWarning",
                "Settings.SensitiveStorage.Status.Unavailable",
                "Settings.SensitiveStorage.Status.CorruptUnreadable",
                "Settings.SensitiveStorage.Status.Available",
                "Settings.Game.YoStarResolutionGuide.Available",
                "Settings.Game.YoStarResolutionGuide.Open",
                "Settings.Game.OverseasClientHint",
                "Settings.Game.OverseasAdaptationGuide.Open",
            }
        },
        {
            "task queue one-shot status",
            new[]
            {
                "TaskQueue.Status.TaskEnabled.Once",
                "TaskQueue.Status.TaskEnabled.SkipOnce",
            }
        },
    };

    [Fact]
    public void FunctionalWindowsAndSettings_ShouldBindLocalizedCopy()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var runtimeLogXaml = File.ReadAllText(Path.Combine(root, "App", "Views", "RuntimeLogWindow.axaml"));
        var screenshotXaml = File.ReadAllText(Path.Combine(root, "App", "Views", "ScreenshotPreviewWindow.axaml"));
        var screenshotCode = File.ReadAllText(Path.Combine(root, "App", "Views", "ScreenshotPreviewWindow.axaml.cs"));
        var connectCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConnectSettingsView.axaml.cs"));
        var remoteXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "RemoteControlSettingsView.axaml"));
        var externalXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ExternalNotificationSettingsView.axaml"));
        var versionXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "VersionUpdateSettingsView.axaml"));
        var gameXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GameSettingsView.axaml"));

        Assert.Contains("RootTexts[Main.Footer.RootLogs]", runtimeLogXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.Action.Close]", runtimeLogXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime Logs", runtimeLogXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Close\"", runtimeLogXaml, StringComparison.Ordinal);

        Assert.DoesNotContain("Screenshot Preview", screenshotXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Close\"", screenshotXaml, StringComparison.Ordinal);
        Assert.Contains("closeText", screenshotCode, StringComparison.Ordinal);
        Assert.Contains("T(\"Settings.Action.Close\")", connectCode, StringComparison.Ordinal);

        Assert.Contains("RootTexts[Settings.SensitiveStorage.Retry]", remoteXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.SensitiveStorage.DiscardUnreadable]", remoteXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry secure storage", remoteXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Discard unreadable value and re-enter", remoteXaml, StringComparison.Ordinal);

        foreach (var xaml in new[] { externalXaml, versionXaml })
        {
            Assert.Contains("RootTexts[Settings.SensitiveStorage.DiscardUnreadable]", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Discard unreadable value and re-enter", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("RootTexts[Settings.Game.YoStarResolutionGuide.Available]", gameXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.Game.YoStarResolutionGuide.Open]", gameXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.Game.OverseasClientHint]", gameXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.Game.OverseasAdaptationGuide.Open]", gameXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("resolution guide is available", gameXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overseas client? See adaptation help", gameXaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PallasFunctionalKeys_ShouldPreserveFormatPlaceholders()
    {
        var manager = UiLocalizationResourceManager.Shared;

        Assert.Contains("{0}", manager.Lookup("pallas", "Copilot.Status.LoadSetPartialFailed").Value);
        Assert.Contains("{0}", manager.Lookup("pallas", "Settings.SensitiveStorage.Status.PlaintextWarning").Value);
        Assert.Contains("{1}", manager.Lookup("pallas", "Settings.SensitiveStorage.Status.Unavailable").Value);
    }

    [Theory]
    [InlineData(null, "en-us")]
    [InlineData("zh-cn", "zh-cn")]
    [InlineData("zh-tw", "zh-tw")]
    [InlineData("en-us", "en-us")]
    [InlineData("ja-jp", "ja-jp")]
    [InlineData("ko-kr", "ko-kr")]
    [InlineData("pallas", "en-us")]
    [InlineData("unsupported", "en-us")]
    public void GameGuideLanguage_ShouldNeverUsePseudoLocaleInHelpUrl(
        string? language,
        string expected)
    {
        Assert.Equal(expected, GameSettingsView.ResolveDocLanguage(language));
    }
}
