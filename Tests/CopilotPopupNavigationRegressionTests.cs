using System.Text.RegularExpressions;
using Avalonia.Input;
using MAAUnified.App.Controls;
using MAAUnified.App.Features.Settings;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

public sealed class CopilotPopupNavigationRegressionTests
{
    [Theory]
    [InlineData(Key.Up, false, false, false, false, "MovePrevious")]
    [InlineData(Key.Down, false, false, false, false, "MoveNext")]
    [InlineData(Key.Home, false, false, false, false, "MoveToFirst")]
    [InlineData(Key.End, false, false, false, false, "MoveToLast")]
    [InlineData(Key.Enter, false, false, false, false, "Invoke")]
    [InlineData(Key.Space, false, false, false, false, "Invoke")]
    [InlineData(Key.Escape, false, false, false, false, "Dismiss")]
    [InlineData(Key.Right, true, false, true, false, "Expand")]
    [InlineData(Key.Right, true, true, true, false, "MoveNext")]
    [InlineData(Key.Left, true, true, true, true, "Collapse")]
    [InlineData(Key.Left, false, false, false, true, "MoveToParent")]
    public void CopilotPathPopup_ShouldMapTreeKeyboardNavigation(
        Key key,
        bool isFolder,
        bool isExpanded,
        bool hasChildren,
        bool hasParent,
        string expected)
    {
        Assert.Equal(
            expected,
            AppCopilotPathDropdown.GetKeyboardAction(key, isFolder, isExpanded, hasChildren, hasParent).ToString());
    }

    [Fact]
    public void CopilotPathPopup_ShouldFocusRowsAndRestoreEditorFocus()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppCopilotPathDropdown.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppCopilotPathDropdown.axaml.cs"));

        Assert.Contains("AddHandler(", code, StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Tunnel", code, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", code, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnPathRowClick\"", xaml, StringComparison.Ordinal);
        Assert.Matches("<Button Classes=\"app-copilot-path-row\"", xaml);
        Assert.Contains("DropDownPopup.Opened += OnPopupOpened", code, StringComparison.Ordinal);
        Assert.Contains("FocusFirstPathRow", code, StringComparison.Ordinal);
        Assert.Contains("ClosePopupAndRestoreEditorFocus", code, StringComparison.Ordinal);
        Assert.Contains("RestoreEditorFocus", code, StringComparison.Ordinal);
        Assert.Contains("GetVisualDescendants()", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "GUI")]
    [InlineData(1, "About")]
    [InlineData(2, "Game")]
    [InlineData(3, "Connect")]
    [InlineData(4, "Performance")]
    [InlineData(5, null)]
    [InlineData(6, "VersionUpdate")]
    public void SettingsGuide_ShouldMapEveryStepToAnOperableTarget(int step, string? expectedSection)
    {
        Assert.Equal(expectedSection, TaskQueuePageViewModel.GetGuideStepSettingsSectionKey(step));
    }

    [Fact]
    public void SettingsGuide_ShouldExposeScrollableContentAndSevenActionButtons()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var guideXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueGuideView.axaml"));
        var taskQueueXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));

        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"420\"", guideXaml, StringComparison.Ordinal);
        Assert.Equal(7, Regex.Matches(guideXaml, "Click=\"OnOpenGuideSettingsClick\"").Count);
        Assert.Contains("Click=\"OnSkipGuideClick\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsGuideSkipVisible}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsGuideNextEnabled}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnConfirmGuideAgreementClick\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnGuideDemoActionClick\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding GuideDemoSteps}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-badge guide-dot\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding Action}\"", guideXaml, StringComparison.Ordinal);
        Assert.Equal(7, Regex.Matches(guideXaml, "Classes=\"guide-step-marker\"").Count);

        Assert.Contains("IsVisible=\"{Binding CanResumeGuide}\"", taskQueueXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnResumeGuideClick\"", taskQueueXaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TaskQueue.Guide.OpenSettings")]
    [InlineData("TaskQueue.Guide.ConfigureTasks")]
    [InlineData("TaskQueue.Guide.Resume")]
    [InlineData("TaskQueue.Guide.Agreement.Tip")]
    [InlineData("TaskQueue.Guide.Demo.Title")]
    [InlineData("TaskQueue.Guide.Skip")]
    [InlineData("Settings.ConfigurationManager.Dialog.ImportUnreadableValueLine")]
    [InlineData("Settings.ConfigurationManager.Dialog.GlobalConfiguration")]
    [InlineData("Settings.Connect.Hint.MacBundledAdbNotFound")]
    public void Round10UiCopy_ShouldExistInAllSixLanguages(string key)
    {
        var manager = UiLocalizationResourceManager.Shared;
        foreach (var language in UiLanguageCatalog.Ordered)
        {
            var result = manager.Lookup(language, key);
            Assert.False(result.IsFallback, $"{language}:{key} fell back to {result.FallbackSource}.");
            if (string.Equals(language, "pallas", StringComparison.Ordinal))
            {
                Assert.StartsWith("[!! ", result.Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ConfigurationManagerEnter_ShouldSubmitOnceAndKeepLiveTextBinding()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml.cs"));

        Assert.Contains("UpdateSourceTrigger=PropertyChanged", xaml, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(xaml, "EditorKeyDown=\"OnNewProfileNameEditorKeyDown\""));
        Assert.True(ConfigurationManagerView.ShouldSubmitConfigurationProfile(Key.Enter));
        Assert.False(ConfigurationManagerView.ShouldSubmitConfigurationProfile(Key.Space));
        Assert.False(ConfigurationManagerView.ShouldSubmitConfigurationProfile(Key.F4));
        Assert.Contains("TryBeginConfigurationProfileSubmission", code, StringComparison.Ordinal);

        var isAdding = false;
        Assert.True(ConfigurationManagerView.TryBeginConfigurationProfileSubmission(ref isAdding));
        Assert.False(ConfigurationManagerView.TryBeginConfigurationProfileSubmission(ref isAdding));
        isAdding = false;
        Assert.True(ConfigurationManagerView.TryBeginConfigurationProfileSubmission(ref isAdding));
    }

    [Theory]
    [InlineData("timeout", ConnectionFailureCategory.ConnectTimeout)]
    [InlineData("address", ConnectionFailureCategory.AddressOrPortUnavailable)]
    [InlineData("adb", ConnectionFailureCategory.AdbDeviceUnavailable)]
    [InlineData("generic", ConnectionFailureCategory.Unknown)]
    public void ConnectionFailures_ShouldUseSixLanguageResources(
        string scenario,
        ConnectionFailureCategory expectedCategory)
    {
        var manager = UiLocalizationResourceManager.Shared;
        var (messageKey, suggestionKey) = scenario switch
        {
            "timeout" => ("ConnectionFailure.Timeout.Message", "ConnectionFailure.Timeout.Suggestion"),
            "address" => ("ConnectionFailure.AddressPort.Message", "ConnectionFailure.AddressPort.Suggestion"),
            "adb" => ("ConnectionFailure.AdbDevice.Message", "ConnectionFailure.AdbDevice.Suggestion"),
            _ => ("ConnectionFailure.Generic.Message", "ConnectionFailure.Generic.Suggestion"),
        };

        foreach (var language in UiLanguageCatalog.Ordered)
        {
            var result = scenario switch
            {
                "timeout" => UiOperationResult.Fail(nameof(CoreErrorCode.ConnectTimeout), "Connect timeout after 30 seconds."),
                "address" => UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "Connection address `127.0.0.1:5554` failed a quick TCP probe."),
                "adb" => UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "error: device unauthorized"),
                _ => UiOperationResult.Fail(UiErrorCode.ConnectFailed, "ConnectFailed"),
            };

            var diagnostic = ConnectionFailureDiagnosticBuilder.Build(result, state: null, language: language);

            Assert.Equal(expectedCategory, diagnostic.Category);
            Assert.Equal(manager.Lookup(language, messageKey).Value, diagnostic.Message);
            Assert.Equal(manager.Lookup(language, suggestionKey).Value, diagnostic.Suggestion);
            if (string.Equals(language, "pallas", StringComparison.Ordinal))
            {
                Assert.StartsWith("[!! ", diagnostic.Message, StringComparison.Ordinal);
                Assert.StartsWith("[!! ", diagnostic.Suggestion, StringComparison.Ordinal);
                Assert.NotEqual(manager.Lookup("en-us", messageKey).Value, diagnostic.Message);
                Assert.NotEqual(manager.Lookup("zh-cn", messageKey).Value, diagnostic.Message);
            }
        }
    }

    [Fact]
    public void ConnectionFailureResources_ShouldAllExistForSixLanguages()
    {
        var keys = new[]
        {
            "ConnectionFailure.Generic.Message",
            "ConnectionFailure.Generic.Suggestion",
            "ConnectionFailure.Timeout.Message",
            "ConnectionFailure.Timeout.Suggestion",
            "ConnectionFailure.TouchMode.Message",
            "ConnectionFailure.TouchMode.Suggestion",
            "ConnectionFailure.AddressPort.Message",
            "ConnectionFailure.AddressPort.Suggestion",
            "ConnectionFailure.AdbDevice.Message",
            "ConnectionFailure.AdbDevice.Suggestion",
            "ConnectionFailure.CoreUnavailable.Message",
            "ConnectionFailure.CoreUnavailable.Suggestion",
            "ConnectionFailure.CoreFailure.MessageFormat",
            "ConnectionFailure.CoreFailure.Suggestion",
            "ConnectionFailure.AdbTarget.Message",
            "ConnectionFailure.AdbTarget.Suggestion",
            "ConnectionFailure.AdbStart.Message",
            "ConnectionFailure.AdbStart.Suggestion",
            "ConnectionFailure.InvalidConfiguration.Message",
            "ConnectionFailure.InvalidConfiguration.Suggestion",
            "ConnectionFailure.ConnectionFailed.Message",
            "ConnectionFailure.ConnectionFailed.Suggestion",
            "ConnectionFailure.FixSettings.Suggestion",
            "ConnectionFailure.AdbCommand.Message",
            "ConnectionFailure.AdbCommand.Suggestion",
        };
        var manager = UiLocalizationResourceManager.Shared;
        foreach (var language in UiLanguageCatalog.Ordered)
        {
            foreach (var key in keys)
            {
                var result = manager.Lookup(language, key);
                Assert.False(result.IsFallback, $"{language}:{key} fell back to {result.FallbackSource}.");
                if (string.Equals(language, "pallas", StringComparison.Ordinal))
                {
                    Assert.StartsWith("[!! ", result.Value, StringComparison.Ordinal);
                }
            }
        }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("address")]
    [InlineData("adb")]
    public void SpecificConnectionFailure_ShouldSurviveDialogLocalization(string scenario)
    {
        foreach (var language in UiLanguageCatalog.Ordered)
        {
            var result = scenario switch
            {
                "timeout" => UiOperationResult.Fail(nameof(CoreErrorCode.ConnectTimeout), "Connect timeout after 30 seconds."),
                "address" => UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "Connection address failed a quick TCP probe."),
                _ => UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "error: device unauthorized"),
            };
            var diagnostic = ConnectionFailureDiagnosticBuilder.Build(result, state: null, language: language);
            var connectResult = UiOperationResult.Fail(
                UiErrorCode.ConnectFailed,
                diagnostic.BuildDialogMessage(),
                diagnostic.Details);

            Assert.Equal(diagnostic.Suggestion, DialogTextCatalog.BuildErrorSuggestion(language, connectResult));
            Assert.Equal(diagnostic.Message, DialogTextCatalog.LocalizeErrorResult(language, connectResult).Message);
        }
    }

    [Theory]
    [InlineData(
        "zh-cn",
        "夜间",
        "Bark 发送密钥",
        "夜间 配置的 Bark 发送密钥 导入失败，无法读取 Bark 发送密钥 配置。请手动修改。")]
    [InlineData(
        "zh-tw",
        "夜間",
        "Bark 傳送金鑰",
        "夜間 設定的 Bark 傳送金鑰 匯入失敗，無法讀取 Bark 傳送金鑰 設定。請手動修改。")]
    [InlineData(
        "en-us",
        "Night",
        "Bark send key",
        "Failed to import Bark send key in Night. The Bark send key configuration could not be read. Please edit it manually.")]
    [InlineData(
        "ja-jp",
        "夜間",
        "Bark 送信キー",
        "夜間 の Bark 送信キー のインポートに失敗しました。Bark 送信キー 設定を読み取れません。手動で修正してください。")]
    [InlineData(
        "ko-kr",
        "야간",
        "Bark 전송 키",
        "야간의 Bark 전송 키을(를) 가져오지 못했습니다. Bark 전송 키 설정을 읽을 수 없습니다. 직접 수정하세요.")]
    public void UnreadableSensitiveImportWarning_ShouldUseRequestedLanguage(
        string language,
        string configurationName,
        string displayName,
        string expected)
    {
        var localizer = UiLocalizer.Create(language);
        var report = new ImportReport
        {
            UnreadableValues =
            [
                new ImportUnreadableConfigValue(
                    configurationName,
                    "ExternalNotification.Bark.SendKey",
                    displayName,
                    string.Empty,
                    IsGlobal: false,
                    "unreadable"),
            ],
        };

        var message = ConfigurationManagerView.BuildUnreadableImportedValuesMessage(
            report,
            key => localizer.GetText(key, "M28Round10"));

        Assert.Equal(expected, message);
        Assert.DoesNotContain("Global", message, StringComparison.Ordinal);
        if (!string.Equals(language, "en-us", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("Failed to import", message, StringComparison.Ordinal);
        }

        if (!string.Equals(language, "zh-cn", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("导入失败", message, StringComparison.Ordinal);
            Assert.DoesNotContain("请手动修改", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnreadableSensitiveImportWarning_PallasShouldBePseudoLocalized()
    {
        var localizer = UiLocalizer.Create("pallas");
        var manager = UiLocalizationResourceManager.Shared;
        var report = new ImportReport
        {
            UnreadableValues =
            [
                new ImportUnreadableConfigValue(
                    "Night",
                    "ExternalNotification.Bark.SendKey",
                    "Bark send key",
                    string.Empty,
                    IsGlobal: false,
                    "unreadable"),
            ],
        };

        var message = ConfigurationManagerView.BuildUnreadableImportedValuesMessage(
            report,
            key => localizer.GetText(key, "M28Round10"));
        var template = manager.Lookup("pallas", "Settings.ConfigurationManager.Dialog.ImportUnreadableValueLine");

        Assert.StartsWith("[!! ", message, StringComparison.Ordinal);
        Assert.Contains("Night", message, StringComparison.Ordinal);
        Assert.Contains("Bark send key", message, StringComparison.Ordinal);
        Assert.False(template.IsFallback);
        Assert.Contains("{0}", template.Value, StringComparison.Ordinal);
        Assert.Contains("{1}", template.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableSensitiveImportWarning_GlobalScopeShouldUseLocalizedName()
    {
        var manager = UiLocalizationResourceManager.Shared;
        var report = new ImportReport
        {
            UnreadableValues =
            [
                new ImportUnreadableConfigValue(
                    string.Empty,
                    "ExternalNotification.Bark.SendKey",
                    "Bark send key",
                    string.Empty,
                    IsGlobal: true,
                    "unreadable"),
            ],
        };

        foreach (var language in UiLanguageCatalog.Ordered)
        {
            var localizer = UiLocalizer.Create(language);
            var message = ConfigurationManagerView.BuildUnreadableImportedValuesMessage(
                report,
                key => localizer.GetText(key, "M28Round10"));
            var globalName = manager.Lookup(language, "Settings.ConfigurationManager.Dialog.GlobalConfiguration").Value;

            Assert.Contains(globalName, message, StringComparison.Ordinal);
            if (string.Equals(language, "pallas", StringComparison.Ordinal))
            {
                Assert.StartsWith("[!! ", globalName, StringComparison.Ordinal);
            }
        }
    }
}
