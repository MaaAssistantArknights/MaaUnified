using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using MAAUnified.App.Features.Settings;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class SettingsGuiBackgroundFeatureTests
{
    [Fact]
    public async Task UseNotifyEnabled_WhenSystemNotificationsUnavailable_ShouldEmitErrorDiagnosticAfterInfoFallback()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var notifications = new List<SystemNotificationRequest>();
        var interactionSource = Assert.IsAssignableFrom<INotificationInteractionSource>(
            fixture.Runtime.Platform.NotificationService);
        interactionSource.InAppNotificationRequested += (_, args) => notifications.Add(args.Notification);

        vm.UseNotify = false;
        vm.UseNotify = true;

        await WaitUntilAsync(() => notifications.Count == 2);
        Assert.Equal(InAppNotificationSeverity.Information, notifications[0].InAppSeverity);
        Assert.Equal(InAppNotificationSeverity.Error, notifications[1].InAppSeverity);
        Assert.False(notifications[1].UseSystemNotification);
    }

    [Fact]
    public async Task SaveGuiSettingsAsync_WritesAllKeysInSingleBatch()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var view = new GuiSettingsView
        {
            DataContext = vm,
        };

        var path = CreateExistingFile(fixture.Root, "bg-save-ok.txt");
        vm.Theme = "Dark";
        SelectLanguageThroughView(view, vm, "en-us");
        vm.UseTray = false;
        vm.UseNotify = false;
        vm.MinimizeToTray = true;
        vm.WindowTitleScrollable = false;
        vm.UiScalePercent = 123;
        vm.BackgroundImagePath = path;
        vm.BackgroundOpacity = 61;
        vm.BackgroundBlur = 27;
        vm.BackgroundStretchMode = "Uniform";

        await WaitUntilAsync(() =>
            string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.Localization), "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(vm.Language, "en-us", StringComparison.OrdinalIgnoreCase));

        await vm.SaveGuiSettingsAsync();

        Assert.Equal("Dark", ReadGlobalString(fixture.Config, "Theme.Mode"));
        Assert.Equal("en-us", ReadGlobalString(fixture.Config, ConfigurationKeys.Localization));
        Assert.Equal("en-us", vm.Language);
        Assert.Equal("en-us", vm.SelectedLanguageValue);
        Assert.Equal("en-us", vm.SelectedLanguageOption?.Value);
        Assert.Equal("False", ReadGlobalString(fixture.Config, ConfigurationKeys.UseTray));
        Assert.Equal("False", ReadGlobalString(fixture.Config, ConfigurationKeys.UseNotify));
        Assert.Equal("False", ReadGlobalString(fixture.Config, ConfigurationKeys.MinimizeToTray));
        Assert.Equal("False", ReadGlobalString(fixture.Config, ConfigurationKeys.WindowTitleScrollable));
        Assert.Equal("123", ReadGlobalString(fixture.Config, ConfigurationKeys.UiScalePercent));
        Assert.Equal(path, ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundImagePath));
        Assert.Equal("61", ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundOpacity));
        Assert.Equal("27", ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundBlurEffectRadius));
        Assert.Equal("Uniform", ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundImageStretchMode));

        var eventLog = await File.ReadAllTextAsync(fixture.Diagnostics.EventLogPath);
        Assert.Contains("Saved settings batch:", eventLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Saved setting:", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSave_AfterLegacyImport_KeepsInverseClearModeGlobalAcrossProfileSwitches()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": { "GUI.InverseClearMode": "Inverse" },
                "Alt": { "ConnectAddress": "127.0.0.1:5555" }
              }
            }
            """);

        await using var fixture = await RuntimeFixture.CreateAsync(root, cleanupRoot: true);
        Assert.Equal(
            "Inverse",
            ReadGlobalString(fixture.Config, ConfigurationKeys.InverseClearMode));
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.InverseClearMode] = JsonValue.Create("ClearInverse");
        fixture.Config.CurrentConfig.Profiles["Alt"].Values[ConfigurationKeys.InverseClearMode] = JsonValue.Create("Inverse");

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.InverseClearMode = "Clear";

        await WaitUntilAsync(() =>
            string.Equals(
                ReadGlobalString(fixture.Config, ConfigurationKeys.InverseClearMode),
                "Clear",
                StringComparison.Ordinal)
            && fixture.Config.CurrentConfig.Profiles.Values.All(
                profile => !profile.Values.ContainsKey(ConfigurationKeys.InverseClearMode)));

        Assert.Equal("Clear", ReadGlobalString(fixture.Config, ConfigurationKeys.InverseClearMode));
        Assert.All(
            fixture.Config.CurrentConfig.Profiles.Values,
            profile => Assert.False(profile.Values.ContainsKey(ConfigurationKeys.InverseClearMode)));

        var profileService = new ConfigurationProfileFeatureService(fixture.Config);
        var switchResult = await profileService.SwitchProfileAsync("Alt");
        Assert.True(switchResult.Success);
        var switchedVm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await switchedVm.InitializeAsync();
        Assert.Equal("Clear", switchedVm.InverseClearMode);
    }

    [Fact]
    public async Task SelectedLanguageOption_UpdatesLanguageImmediately()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var languageOption = vm.SupportedLanguages.First(option => string.Equals(option.Value, "en-us", StringComparison.OrdinalIgnoreCase));

        vm.SelectedLanguageOption = languageOption;

        await WaitUntilAsync(() =>
            string.Equals(vm.Language, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(vm.SelectedLanguageValue, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(vm.SelectedLanguageOption?.Value, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.Localization), "en-us", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("en-us", vm.Language);
        Assert.Equal("en-us", vm.SelectedLanguageValue);
        Assert.Equal("en-us", vm.SelectedLanguageOption?.Value);
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData("80", 80)]
    [InlineData("40", 70)]
    [InlineData("200", 140)]
    [InlineData("not-a-number", 100)]
    public void StartupShellSnapshot_FromConfig_NormalizesUiScalePercent(string? rawValue, int expected)
    {
        var config = new UnifiedConfig();
        if (rawValue is not null)
        {
            config.GlobalValues[ConfigurationKeys.UiScalePercent] = JsonValue.Create(rawValue);
        }

        var snapshot = StartupShellSnapshot.FromConfig(config);

        Assert.Equal(expected, snapshot.UiScalePercent);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void StartupShellSnapshot_FromConfig_ReadsHideCloseButton(string? rawValue, bool expected)
    {
        // WPF parity (RootViewModel.ShowCloseButton = !GUI.HideCloseButton).
        var config = new UnifiedConfig();
        if (rawValue is not null)
        {
            config.GlobalValues[ConfigurationKeys.HideCloseButton] = JsonValue.Create(rawValue);
        }

        var snapshot = StartupShellSnapshot.FromConfig(config);

        Assert.Equal(expected, snapshot.HideCloseButton);
    }

    [Fact]
    public async Task LanguageChange_KeepsSupportedLanguagesStable_AndDoesNotFallbackToZhCn()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var originalLanguages = vm.SupportedLanguages;
        var enUs = originalLanguages.First(option => string.Equals(option.Value, "en-us", StringComparison.OrdinalIgnoreCase));
        var jaJp = originalLanguages.First(option => string.Equals(option.Value, "ja-jp", StringComparison.OrdinalIgnoreCase));

        vm.SelectedLanguageOption = enUs;
        await WaitUntilAsync(() =>
            string.Equals(vm.Language, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.Localization), "en-us", StringComparison.OrdinalIgnoreCase));
        var languagesAfterEnglish = vm.SupportedLanguages;
        Assert.Equal(originalLanguages, languagesAfterEnglish);
        Assert.Equal("en-us", vm.Language);
        Assert.Equal("en-us", vm.SelectedLanguageValue);
        Assert.Equal("en-us", vm.SelectedLanguageOption?.Value);

        vm.SelectedLanguageOption = jaJp;
        await WaitUntilAsync(() =>
            string.Equals(vm.Language, "ja-jp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.Localization), "ja-jp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(languagesAfterEnglish, vm.SupportedLanguages);
        Assert.Equal("ja-jp", vm.Language);
        Assert.Equal("ja-jp", vm.SelectedLanguageValue);
        Assert.Equal("ja-jp", vm.SelectedLanguageOption?.Value);
    }

    [Fact]
    public async Task LanguageChange_RebuildsLocalizedGuiOptions_AndKeepsSelections()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ChangeLanguageAsync("zh-cn");
        await WaitUntilAsync(() => string.Equals(vm.Language, "zh-cn", StringComparison.OrdinalIgnoreCase));

        vm.Theme = "Dark";
        vm.OperNameLanguage = "OperNameLanguageClient";
        vm.BackgroundStretchMode = "UniformToFill";
        vm.InverseClearMode = "ClearInverse";
        vm.VersionUpdateVersionType = "Beta";
        vm.VersionUpdateResourceSource = "MirrorChyan";
        vm.VersionUpdateProxyType = "socks5";

        var originalThemeOptions = vm.ThemeOptions;
        var originalOperNameLanguageOptions = vm.OperNameLanguageOptions;
        var originalBackgroundStretchModes = vm.BackgroundStretchModes;
        var originalInverseClearModeOptions = vm.InverseClearModeOptions;
        var originalVersionTypeOptions = vm.VersionUpdateVersionTypeOptions;
        var originalResourceSourceOptions = vm.VersionUpdateResourceSourceOptions;
        var originalProxyTypeOptions = vm.VersionUpdateProxyTypeOptions;

        var originalThemeLight = FindDisplay(vm.ThemeOptions, "Light");
        var originalOperNameClient = FindDisplay(vm.OperNameLanguageOptions, "OperNameLanguageClient");
        var originalStretchCover = FindDisplay(vm.BackgroundStretchModes, "UniformToFill");
        var originalInverseSwitchable = FindDisplay(vm.InverseClearModeOptions, "ClearInverse");
        var originalVersionChannelBeta = FindDisplay(vm.VersionUpdateVersionTypeOptions, "Beta");
        var originalResourceSourceMirror = FindDisplay(vm.VersionUpdateResourceSourceOptions, "MirrorChyan");
        var selectedThemeBeforeLanguageChange = vm.SelectedThemeOption;
        var selectedOperNameBeforeLanguageChange = vm.SelectedOperNameLanguageOption;
        var selectedStretchBeforeLanguageChange = vm.SelectedBackgroundStretchModeOption;
        var selectedInverseBeforeLanguageChange = vm.SelectedInverseClearModeOption;
        var selectedVersionTypeBeforeLanguageChange = vm.SelectedVersionUpdateVersionTypeOption;
        var selectedResourceSourceBeforeLanguageChange = vm.SelectedVersionUpdateResourceSourceOption;
        var selectedProxyTypeBeforeLanguageChange = vm.SelectedVersionUpdateProxyTypeOption;

        await vm.ChangeLanguageAsync("en-us");
        await WaitUntilAsync(() => string.Equals(vm.Language, "en-us", StringComparison.OrdinalIgnoreCase));

        Assert.NotSame(originalThemeOptions, vm.ThemeOptions);
        Assert.NotSame(originalOperNameLanguageOptions, vm.OperNameLanguageOptions);
        Assert.NotSame(originalBackgroundStretchModes, vm.BackgroundStretchModes);
        Assert.NotSame(originalInverseClearModeOptions, vm.InverseClearModeOptions);
        Assert.NotSame(originalVersionTypeOptions, vm.VersionUpdateVersionTypeOptions);
        Assert.NotSame(originalResourceSourceOptions, vm.VersionUpdateResourceSourceOptions);
        Assert.NotSame(originalProxyTypeOptions, vm.VersionUpdateProxyTypeOptions);

        Assert.NotEqual(originalThemeLight, FindDisplay(vm.ThemeOptions, "Light"));
        Assert.NotEqual(originalOperNameClient, FindDisplay(vm.OperNameLanguageOptions, "OperNameLanguageClient"));
        Assert.NotEqual(originalStretchCover, FindDisplay(vm.BackgroundStretchModes, "UniformToFill"));
        Assert.NotEqual(originalInverseSwitchable, FindDisplay(vm.InverseClearModeOptions, "ClearInverse"));
        Assert.NotEqual(originalVersionChannelBeta, FindDisplay(vm.VersionUpdateVersionTypeOptions, "Beta"));
        Assert.NotEqual(originalResourceSourceMirror, FindDisplay(vm.VersionUpdateResourceSourceOptions, "MirrorChyan"));

        Assert.Equal("Dark", vm.SelectedThemeOption?.Value);
        Assert.Equal("OperNameLanguageClient", vm.SelectedOperNameLanguageOption?.Value);
        Assert.Equal("UniformToFill", vm.SelectedBackgroundStretchModeOption?.Value);
        Assert.Equal("ClearInverse", vm.SelectedInverseClearModeOption?.Value);
        Assert.Equal("Beta", vm.SelectedVersionUpdateVersionTypeOption?.Value);
        Assert.Equal("MirrorChyan", vm.SelectedVersionUpdateResourceSourceOption?.Value);
        Assert.Equal("socks5", vm.SelectedVersionUpdateProxyTypeOption?.Value);

        Assert.NotSame(selectedThemeBeforeLanguageChange, vm.SelectedThemeOption);
        Assert.NotSame(selectedOperNameBeforeLanguageChange, vm.SelectedOperNameLanguageOption);
        Assert.NotSame(selectedStretchBeforeLanguageChange, vm.SelectedBackgroundStretchModeOption);
        Assert.NotSame(selectedInverseBeforeLanguageChange, vm.SelectedInverseClearModeOption);
        Assert.NotSame(selectedVersionTypeBeforeLanguageChange, vm.SelectedVersionUpdateVersionTypeOption);
        Assert.NotSame(selectedResourceSourceBeforeLanguageChange, vm.SelectedVersionUpdateResourceSourceOption);
        Assert.NotSame(selectedProxyTypeBeforeLanguageChange, vm.SelectedVersionUpdateProxyTypeOption);
        Assert.Equal(selectedThemeBeforeLanguageChange, vm.SelectedThemeOption);
        Assert.Equal(selectedOperNameBeforeLanguageChange, vm.SelectedOperNameLanguageOption);
        Assert.Equal(selectedStretchBeforeLanguageChange, vm.SelectedBackgroundStretchModeOption);
        Assert.Equal(selectedInverseBeforeLanguageChange, vm.SelectedInverseClearModeOption);
        Assert.Equal(selectedVersionTypeBeforeLanguageChange, vm.SelectedVersionUpdateVersionTypeOption);
        Assert.Equal(selectedResourceSourceBeforeLanguageChange, vm.SelectedVersionUpdateResourceSourceOption);
        Assert.Equal(selectedProxyTypeBeforeLanguageChange, vm.SelectedVersionUpdateProxyTypeOption);
    }

    [Fact]
    public async Task LanguageChange_ShouldNotifySettingsAndConnectRootTextsForOpenSections()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var settingsChanged = new List<string>();
        var connectChanged = new List<string>();
        var changeLock = new object();
        vm.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                lock (changeLock)
                {
                    settingsChanged.Add(e.PropertyName);
                }
            }
        };
        vm.ConnectionGameSharedState.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                lock (changeLock)
                {
                    connectChanged.Add(e.PropertyName);
                }
            }
        };

        await vm.ChangeLanguageAsync("en-us");
        await WaitUntilAsync(() => string.Equals(vm.Language, "en-us", StringComparison.OrdinalIgnoreCase));

        string[] settingsSnapshot;
        string[] connectSnapshot;
        lock (changeLock)
        {
            settingsSnapshot = settingsChanged.ToArray();
            connectSnapshot = connectChanged.ToArray();
        }

        Assert.Contains(nameof(SettingsPageViewModel.RootTexts), settingsSnapshot);
        Assert.Contains(nameof(ConnectionGameSharedStateViewModel.RootTexts), connectSnapshot);
    }

    [Fact]
    public async Task GuiSettingsView_LanguageSelectionChanged_UpdatesLanguageImmediately()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var view = new GuiSettingsView
        {
            DataContext = vm,
        };

        SelectLanguageThroughView(view, vm, "ja-jp");

        await WaitUntilAsync(() =>
            string.Equals(vm.Language, "ja-jp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(vm.SelectedLanguageValue, "ja-jp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.Localization), "ja-jp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ja-jp", vm.Language);
        Assert.Equal("ja-jp", vm.SelectedLanguageValue);
        Assert.Equal("ja-jp", vm.SelectedLanguageOption?.Value);
    }

    [Fact]
    public async Task SelectedLanguageOption_WhenCoordinatorFails_ShouldRevertSelectionAndKeepPersistedLanguage()
    {
        var coordinator = new FailingUiLanguageCoordinator("zh-cn");
        await using var fixture = await RuntimeFixture.CreateAsync(uiLanguageCoordinator: coordinator);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                changedProperties.Add(e.PropertyName);
            }
        };

        var languageOption = vm.SupportedLanguages.First(option => string.Equals(option.Value, "en-us", StringComparison.OrdinalIgnoreCase));

        vm.SelectedLanguageOption = languageOption;

        await WaitUntilAsync(() => coordinator.ChangeCallCount == 1);
        Assert.Contains(nameof(SettingsPageViewModel.SelectedLanguageOption), changedProperties);
        Assert.Contains(nameof(SettingsPageViewModel.SelectedLanguageValue), changedProperties);
        Assert.Equal("zh-cn", vm.Language);
        Assert.Equal("zh-cn", vm.SelectedLanguageValue);
        Assert.Equal("zh-cn", vm.SelectedLanguageOption?.Value);
        Assert.True(string.IsNullOrWhiteSpace(ReadGlobalString(fixture.Config, ConfigurationKeys.Localization)));
    }

    [Fact]
    public async Task SelectedLanguageValue_ReboundDuringApply_ShouldNotQueueSecondLanguageChange()
    {
        var coordinator = new CountingUiLanguageCoordinator("zh-cn");
        await using var fixture = await RuntimeFixture.CreateAsync(uiLanguageCoordinator: coordinator);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var injectedRebind = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (injectedRebind
                || !string.Equals(e.PropertyName, nameof(SettingsPageViewModel.SelectedLanguageValue), StringComparison.Ordinal)
                || !string.Equals(vm.Language, "en-us", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(vm.SelectedLanguageValue, "en-us", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            injectedRebind = true;
            vm.SelectedLanguageValue = "zh-cn";
        };

        vm.SelectedLanguageValue = "en-us";

        await WaitUntilAsync(() =>
            injectedRebind
            && string.Equals(vm.Language, "en-us", StringComparison.OrdinalIgnoreCase)
            && string.Equals(vm.SelectedLanguageValue, "en-us", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, coordinator.ChangeCallCount);
        Assert.Equal("en-us", vm.Language);
        Assert.Equal("en-us", vm.SelectedLanguageValue);
        Assert.Equal("en-us", vm.SelectedLanguageOption?.Value);
    }

    [Fact]
    public async Task ChangeLanguageAsync_ShouldNotTriggerVersionUpdateAutosave()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService();
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var baselineChannelSaves = versionUpdate.SaveChannelCallCount;
        var baselineProxySaves = versionUpdate.SaveProxyCallCount;
        var baselinePolicySaves = versionUpdate.SavePolicyCallCount;
        var baselineVersionUpdateError = vm.VersionUpdateErrorMessage;
        var baselineHasVersionUpdateError = vm.HasVersionUpdateErrorMessage;

        await vm.ChangeLanguageAsync("en-us");
        await Task.Delay(1000);

        Assert.Equal(baselineChannelSaves, versionUpdate.SaveChannelCallCount);
        Assert.Equal(baselineProxySaves, versionUpdate.SaveProxyCallCount);
        Assert.Equal(baselinePolicySaves, versionUpdate.SavePolicyCallCount);
        Assert.Equal(baselineHasVersionUpdateError, vm.HasVersionUpdateErrorMessage);
        Assert.Equal(baselineVersionUpdateError, vm.VersionUpdateErrorMessage);
    }

    [Fact]
    public async Task StartupVersionUpdateCheck_ShouldRefreshVersionAndResourceAvailability()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService
        {
            CheckForUpdatesResult = UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v2.0.0",
                    ReleaseName: "Release v2.0.0",
                    Summary: "Summary",
                    Body: "Body",
                    PackageName: "pkg.zip",
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: true,
                    HasPackage: false),
                "发现新版本：v2.0.0"),
            CheckResourceUpdateResult = UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: true,
                    DisplayVersion: "2026-04-03 10:00:00",
                    ReleaseNote: "episode",
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: true,
                    DownloadUrl: null),
                "检测到资源更新，可手动更新。"),
        };
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.VersionUpdateResourceSource = "MirrorChyan";

        Assert.False(vm.HasPendingVersionUpdateAvailability);
        Assert.False(vm.HasPendingResourceUpdateAvailability);

        await vm.RunStartupVersionUpdateCheckAsync();

        Assert.True(vm.HasPendingVersionUpdateAvailability);
        Assert.True(vm.HasPendingResourceUpdateAvailability);
        Assert.Contains("episode", vm.PendingResourceUpdateSummary, StringComparison.Ordinal);
        Assert.Equal(1, versionUpdate.CheckResourceCallCount);
        Assert.Equal(0, versionUpdate.UpdateResourceCallCount);
        var bridge = Assert.IsType<FakeBridge>(fixture.Runtime.CoreBridge);
        Assert.Equal(0, bridge.ReloadResourceCallCount);
    }

    [Fact]
    public async Task StartupVersionUpdateCheck_WhenGithubResourceUpdateAvailable_DoesNotAutoApply()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService
        {
            CheckResourceUpdateResult = UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: true,
                    DisplayVersion: "2026-04-22 08:48:01",
                    ReleaseNote: "episode",
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: false,
                    DownloadUrl: "https://example.com/resource.zip"),
                "检测到资源更新，可手动更新。"),
        };
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.VersionUpdateResourceSource = "Github";

        await vm.RunStartupVersionUpdateCheckAsync();

        Assert.True(vm.HasPendingResourceUpdateAvailability);
        Assert.Equal(1, versionUpdate.CheckResourceCallCount);
        Assert.Equal(0, versionUpdate.UpdateResourceCallCount);
        var bridge = Assert.IsType<FakeBridge>(fixture.Runtime.CoreBridge);
        Assert.Equal(0, bridge.ReloadResourceCallCount);
    }

    [Fact]
    public async Task StartupVersionUpdateCheck_WhenMirrorChyanResourceUpdateAvailable_AutoApplies()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService
        {
            CheckResourceUpdateResult = UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: true,
                    DisplayVersion: "2026-04-22 08:48:01",
                    ReleaseNote: "episode",
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: false,
                    DownloadUrl: "https://example.com/resource.zip"),
                "检测到资源更新。"),
        };
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.VersionUpdateResourceSource = "MirrorChyan";

        await vm.RunStartupVersionUpdateCheckAsync();

        Assert.False(vm.HasPendingResourceUpdateAvailability);
        Assert.Equal(1, versionUpdate.CheckResourceCallCount);
        Assert.Equal(1, versionUpdate.UpdateResourceCallCount);
        var bridge = Assert.IsType<FakeBridge>(fixture.Runtime.CoreBridge);
        Assert.Equal(1, bridge.ReloadResourceCallCount);
    }

    [Fact]
    public async Task ManualUpdateResource_WhenMirrorChyanWithoutCdk_ShouldShortCircuitWithLocalizedGuidance()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService();
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.VersionUpdateResourceSource = "MirrorChyan";
        vm.VersionUpdateMirrorChyanCdk = string.Empty;

        await vm.ManualUpdateResourceAsync();

        Assert.Equal(0, versionUpdate.CheckResourceCallCount);
        Assert.Contains("CDK", vm.VersionUpdateStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualSoftwareUpdateCheck_WhenMirrorChyanWithoutCdk_ShouldShortCircuitWithLocalizedGuidance()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService();
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.VersionUpdateResourceSource = "MirrorChyan";
        vm.VersionUpdateMirrorChyanCdk = string.Empty;

        await vm.CheckVersionUpdateAsync();

        Assert.Equal(0, versionUpdate.CheckForUpdatesCallCount);
        Assert.Contains("CDK", vm.VersionUpdateStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasVersionUpdateErrorMessage);
    }

    [Theory]
    [InlineData("check-http")]
    [InlineData("check-code")]
    [InlineData("update-http")]
    [InlineData("update-code")]
    public async Task ManualMirrorChyanFailure_DoesNotRecordResponseSecrets(string failureKind)
    {
        const string sentinel = "ROUND8-VM-SECRET-+/=SENTINEL";
        var encodedSentinel = Uri.EscapeDataString(sentinel);
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        var requestCount = 0;
        using var httpClient = new HttpClient(new CallbackHttpMessageHandler(_ =>
        {
            requestCount++;
            if (failureKind.StartsWith("update", StringComparison.Ordinal) && requestCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "code": 0,
                          "data": {
                            "version_name": "2099-01-01",
                            "url": "https://download.example.com/resource.zip"
                          }
                        }
                        """),
                };
            }

            if (failureKind.EndsWith("http", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent($"response={sentinel}; encoded={encodedSentinel}"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"code\":403,\"msg\":\"{sentinel} {encodedSentinel}\"}}"),
            };
        }));
        await using var fixture = await RuntimeFixture.CreateAsync(
            root: root,
            versionUpdateFeatureServiceFactory: (config, diagnostics, log) => new VersionUpdateFeatureService(
                config,
                diagnosticsService: diagnostics,
                uiLogService: log,
                runtimeBaseDirectory: root,
                resourceHttpClient: httpClient));
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.VersionUpdateResourceSource = "MirrorChyan";
        vm.VersionUpdateMirrorChyanCdk = "test-cdk";

        await vm.ManualUpdateResourceAsync();

        var uiLogs = string.Join('\n', fixture.Runtime.LogService.Snapshot.Select(log => log.Message));
        var diagnosticLogs = File.Exists(fixture.Diagnostics.ErrorLogPath)
            ? await File.ReadAllTextAsync(fixture.Diagnostics.ErrorLogPath)
            : string.Empty;
        Assert.DoesNotContain(sentinel, uiLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(encodedSentinel, uiLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, diagnosticLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(encodedSentinel, diagnosticLogs, StringComparison.Ordinal);
        Assert.Contains(
            failureKind.EndsWith("http", StringComparison.Ordinal) ? "status=502" : "code=403",
            diagnosticLogs,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualUpdateResource_WhenAlreadyLatest_ShouldSkipDownload()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService
        {
            CheckResourceUpdateResult = UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: false,
                    DisplayVersion: string.Empty,
                    ReleaseNote: string.Empty,
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: false,
                    DownloadUrl: null),
                "资源已是最新版本。"),
        };
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.VersionUpdateResourceSource = "Github";

        await vm.ManualUpdateResourceAsync();

        Assert.Equal(1, versionUpdate.CheckResourceCallCount);
        Assert.Equal(0, versionUpdate.UpdateResourceCallCount);
        Assert.Contains("最新", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);
        Assert.Contains("最新", vm.VersionUpdateInlineMessage, StringComparison.Ordinal);
        Assert.True(vm.HasVersionUpdateInlineMessage);
        Assert.False(vm.HasPendingResourceUpdateAvailability);
    }

    [Fact]
    public async Task ManualUpdateResource_WhenDownloading_ShouldShowProgressInSettings()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService
        {
            EmitResourceProgress = true,
            CheckResourceUpdateResult = UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: true,
                    DisplayVersion: "2026-04-09",
                    ReleaseNote: "2026-04-09",
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: false,
                    DownloadUrl: "https://example.com/resource.zip"),
                "检测到资源更新。"),
        };
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.ManualUpdateResourceAsync();

        await WaitUntilAsync(() =>
        {
            Dispatcher.UIThread.RunJobs(null);
            return vm.VersionUpdateActivityMessage.Contains("游戏资源已更新", StringComparison.Ordinal);
        });
        Assert.Contains("游戏资源已更新", vm.VersionUpdateActivityMessage, StringComparison.Ordinal);
        Assert.Contains("游戏资源已更新", vm.VersionUpdateInlineMessage, StringComparison.Ordinal);
        Assert.Contains("资源更新完成", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);
        var bridge = Assert.IsType<FakeBridge>(fixture.Runtime.CoreBridge);
        Assert.Equal(1, bridge.ReloadResourceCallCount);
    }

    [Fact]
    public async Task CheckVersionUpdateAsync_WhenPackageDownloads_ShouldShowProgressInSettings()
    {
        var versionUpdate = new SpyVersionUpdateFeatureService
        {
            EmitSoftwareProgress = true,
            CheckForUpdatesResult = UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v1.1.0",
                    ReleaseName: "v1.1.0",
                    Summary: "summary",
                    Body: "body",
                    PackageName: "MAA-v1.1.0-win-x64.zip",
                    PackageDownloadUrl: new Uri("https://example.com/MAA-v1.1.0-win-x64.zip"),
                    PackageSize: 64 * 1024 * 1024,
                    IsNewVersion: true,
                    HasPackage: true,
                    PreparedPackagePath: "/tmp/MAA-v1.1.0-win-x64.zip"),
                "检测到新版本。"),
        };
        await using var fixture = await RuntimeFixture.CreateAsync(versionUpdateFeatureService: versionUpdate);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.CheckVersionUpdateAsync();

        await WaitUntilAsync(() =>
        {
            Dispatcher.UIThread.RunJobs(null);
            return vm.VersionUpdateActivityMessage.Contains("下载完成", StringComparison.Ordinal);
        });
        Assert.Contains("下载完成", vm.VersionUpdateActivityMessage, StringComparison.Ordinal);
        Assert.True(vm.HasPendingVersionUpdateAvailability);
    }

    [Fact]
    public async Task UseTray_Disabled_ClearsMinimizeToTrayState()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.UseTray = true;
        vm.MinimizeToTray = true;
        await vm.SaveGuiSettingsAsync();
        Assert.True(vm.MinimizeToTray);

        vm.UseTray = false;

        await WaitUntilAsync(() =>
            string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.UseTray), "False", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.MinimizeToTray), "False", StringComparison.OrdinalIgnoreCase));

        Assert.False(vm.MinimizeToTray);
    }

    [Fact]
    public async Task SaveGuiSettingsAsync_InvalidBackgroundPath_SavesOtherSettingsAndKeepsPathDraft()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var baselinePath = CreateExistingFile(fixture.Root, "bg-baseline.txt");
        await vm.ChangeLanguageAsync("en-us");
        vm.UseTray = true;
        vm.MinimizeToTray = false;
        vm.WindowTitleScrollable = true;
        vm.BackgroundImagePath = baselinePath;
        vm.BackgroundOpacity = 45;
        vm.BackgroundBlur = 12;
        vm.BackgroundStretchMode = "UniformToFill";
        await vm.SaveGuiSettingsAsync();
        Assert.False(vm.HasPendingGuiChanges);

        var missingPath = Path.Combine(fixture.Root, "missing.png");
        vm.BackgroundImagePath = missingPath;
        await vm.ChangeLanguageAsync("ja-jp");
        vm.UseTray = false;
        await vm.SaveGuiSettingsAsync();

        // WPF parity (BackgroundSettingsUserControlModel): a missing background file must not block
        // persisting the remaining GUI settings; the invalid path stays an unpersisted draft.
        var after = CaptureGuiSnapshot(fixture.Config);
        Assert.Equal("ja-jp", after[ConfigurationKeys.Localization]);
        Assert.Equal("False", after[ConfigurationKeys.UseTray]);
        Assert.Equal(baselinePath, after[ConfigurationKeys.BackgroundImagePath]);
        Assert.True(vm.HasPendingGuiChanges);
        Assert.Equal(missingPath, vm.BackgroundImagePath);
        Assert.Equal("ja-jp", vm.Language);
        Assert.Contains("does not exist", vm.BackgroundValidationMessage, StringComparison.OrdinalIgnoreCase);

        // Fixing the path clears the pending state and persists everything.
        vm.BackgroundImagePath = baselinePath;
        await vm.SaveGuiSettingsAsync();
        Assert.False(vm.HasPendingGuiChanges);
        Assert.Equal(baselinePath, ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundImagePath));
    }

    [Fact]
    public async Task SaveGuiSettingsAsync_InvalidBackgroundPathOnFreshInstall_RevertsToConventionalDefault()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // Fresh install: the key was never written, so the WPF conventional default applies.
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey(ConfigurationKeys.BackgroundImagePath));
        Assert.Equal("background/background.png", vm.BackgroundImagePath);

        var missingPath = Path.Combine(fixture.Root, "missing.png");
        vm.BackgroundImagePath = missingPath;
        vm.Theme = "Dark";
        await vm.SaveGuiSettingsAsync();

        // The invalid path stays an unpersisted draft while the rest of the batch is saved.
        Assert.True(vm.HasPendingGuiChanges);
        Assert.Equal(missingPath, vm.BackgroundImagePath);
        Assert.Equal("Dark", ReadGlobalString(fixture.Config, "Theme.Mode"));
        Assert.Contains("does not exist", vm.BackgroundValidationMessage, StringComparison.OrdinalIgnoreCase);

        // The rejection must revert to the effective previous value (the conventional default), not
        // persist GUI.BackgroundImagePath="" — otherwise "never written -> default applies" would
        // silently become an explicit clear the user never requested (N2 missing-vs-empty semantics).
        var persisted = fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.BackgroundImagePath];
        var persistedText = persisted is JsonValue persistedValue && persistedValue.TryGetValue(out string? text) ? text : persisted?.ToString() ?? string.Empty;
        Assert.Equal("background/background.png", persistedText);

        // After a restart the settings page shows the default path again, not an empty textbox.
        var reloaded = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await reloaded.InitializeAsync();
        Assert.Equal("background/background.png", reloaded.BackgroundImagePath);
    }

    [Fact]
    public void StartupShellSnapshot_UsesLegacyDarkMode_WhenThemeModeMissing()
    {
        var config = new UnifiedConfig();
        config.GlobalValues["GUI.DarkMode" /* legacy key; intentionally absent from the frozen 300-key baseline contract */] = JsonValue.Create("Dark");

        Assert.Equal("Dark", StartupShellSnapshot.FromConfig(config).Theme);

        config.GlobalValues["GUI.DarkMode" /* legacy key; intentionally absent from the frozen 300-key baseline contract */] = JsonValue.Create("SyncWithOs");
        Assert.Equal("SyncWithOs", StartupShellSnapshot.FromConfig(config).Theme);

        // An explicit Avalonia Theme.Mode value always wins over the legacy key.
        config.GlobalValues["Theme.Mode"] = JsonValue.Create("Light");
        Assert.Equal("Light", StartupShellSnapshot.FromConfig(config).Theme);
    }

    [Fact]
    public void StartupShellSnapshot_BackgroundDefaults_MatchFrozenWpf()
    {
        // Frozen WPF (BackgroundSettingsUserControlModel.cs): path background/background.png,
        // opacity 50, blur 5.
        Assert.Equal("background/background.png", StartupShellSnapshot.Default.BackgroundImagePath);
        Assert.Equal(50, StartupShellSnapshot.Default.BackgroundOpacity);
        Assert.Equal(5, StartupShellSnapshot.Default.BackgroundBlur);

        // The conventional default applies only when the key was never written; an explicitly
        // cleared (empty) stored value stays empty so users can remove the background.
        Assert.Equal("background/background.png", StartupShellSnapshot.FromConfig(new UnifiedConfig()).BackgroundImagePath);

        var cleared = new UnifiedConfig();
        cleared.GlobalValues[ConfigurationKeys.BackgroundImagePath] = JsonValue.Create(string.Empty);
        Assert.Equal(string.Empty, StartupShellSnapshot.FromConfig(cleared).BackgroundImagePath);
    }

    [Fact]
    public async Task SaveGuiSettingsAsync_DefaultBackgroundPathMissing_SavesWithoutValidationError()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // Fresh install: the WPF conventional default path is offered even though the file is absent.
        Assert.Equal("background/background.png", vm.BackgroundImagePath);

        vm.Theme = "Dark";
        await vm.SaveGuiSettingsAsync();

        // WPF parity (BackgroundSettingsUserControlModel.RefreshBackgroundImage): a missing default
        // image renders nothing; it must not fail the save or leave the batch pending.
        Assert.False(vm.HasPendingGuiChanges);
        Assert.False(vm.HasBackgroundValidationMessage);
        Assert.Equal("background/background.png", ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundImagePath));

        var reloaded = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await reloaded.InitializeAsync();
        Assert.Equal("background/background.png", reloaded.BackgroundImagePath);
    }

    [Fact]
    public async Task GuiSettings_ProfileScopedLegacyKeys_AreHonoredAndPromotedToGlobalOnSave()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var config = fixture.Config.CurrentConfig;
        var profile = config.Profiles[config.CurrentProfile];
        // Simulate a WPF gui.json import: these keys are per-configuration (profile-scoped) in WPF.
        profile.Values[ConfigurationKeys.UseCardLog] = JsonValue.Create("False");
        profile.Values[ConfigurationKeys.MaxNumberOfLogThumbnails] = JsonValue.Create("7");
        profile.Values[ConfigurationKeys.WindowTitlePrefix] = JsonValue.Create("LEGACY");

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // WPF parity (ConfigurationHelper.GetValue is profile-first): imported profile values are
        // honored immediately, before any GUI save.
        Assert.False(vm.UseCardLog);
        Assert.Equal(7, vm.MaxNumberOfLogThumbnails);

        // An unrelated GUI save promotes the shown values to global scope and drops the stale
        // profile copies so the profile-first runtime readers keep observing the saved values.
        vm.UseTray = false;
        await vm.SaveGuiSettingsAsync();

        Assert.Equal("False", ReadGlobalString(fixture.Config, ConfigurationKeys.UseCardLog));
        Assert.Equal("7", ReadGlobalString(fixture.Config, ConfigurationKeys.MaxNumberOfLogThumbnails));
        Assert.False(profile.Values.ContainsKey(ConfigurationKeys.UseCardLog));
        Assert.False(profile.Values.ContainsKey(ConfigurationKeys.MaxNumberOfLogThumbnails));
        // Nothing writes the prefix to global scope, so its profile copy stays the source of truth.
        Assert.True(profile.Values.ContainsKey(ConfigurationKeys.WindowTitlePrefix));
    }

    [Fact]
    public async Task TaskQueuePage_RuntimeReaders_HonorProfileScopedLegacyGuiKeys()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var config = fixture.Config.CurrentConfig;
        var profile = config.Profiles[config.CurrentProfile];
        profile.Values[ConfigurationKeys.UseCardLog] = JsonValue.Create("False");

        var taskQueue = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await taskQueue.InitializeAsync();

        // WPF parity (ConfigurationHelper.GetValue is profile-first with global fallback).
        Assert.False(taskQueue.UseCardLog);

        // A global value remains the fallback when the profile does not carry the key.
        profile.Values.Remove(ConfigurationKeys.UseCardLog);
        config.GlobalValues[ConfigurationKeys.UseCardLog] = JsonValue.Create("False");
        var fallbackQueue = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await fallbackQueue.InitializeAsync();
        Assert.False(fallbackQueue.UseCardLog);

        config.GlobalValues[ConfigurationKeys.UseCardLog] = JsonValue.Create("True");
        profile.Values[ConfigurationKeys.UseCardLog] = JsonValue.Create("False");
        var profileWinsQueue = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await profileWinsQueue.InitializeAsync();
        Assert.False(profileWinsQueue.UseCardLog);
    }

    [Fact]
    public async Task SaveGuiSettingsAsync_WritesLegacyDarkModeKey()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.Theme = "Dark";
        await vm.SaveGuiSettingsAsync();

        Assert.Equal("Dark", ReadGlobalString(fixture.Config, "Theme.Mode"));
        Assert.Equal("Dark", ReadGlobalString(fixture.Config, "GUI.DarkMode" /* legacy key; intentionally absent from the frozen 300-key baseline contract */));
    }

    [Fact]
    public async Task LanguageList_HidesPallas_UnlessCheersUnlocked()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.DoesNotContain(vm.SupportedLanguages, option => option.Value == UiLanguageCatalog.PallasLanguage);

        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Cheers] = JsonValue.Create(true);
        var cheersVm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await cheersVm.InitializeAsync();
        Assert.Contains(cheersVm.SupportedLanguages, option => option.Value == UiLanguageCatalog.PallasLanguage);
    }

    [Fact]
    public async Task ChangeLanguage_LeavingPallas_RecordsHangoverAndSoberLanguage()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create("pallas");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Cheers] = JsonValue.Create(true);

        var coordinator = new UiLanguageCoordinator(fixture.Config);
        Assert.Equal("pallas", coordinator.CurrentLanguage);
        var result = await coordinator.ChangeLanguageAsync("en-us");

        Assert.True(result.Success);
        Assert.Equal("false", ReadGlobalString(fixture.Config, ConfigurationKeys.Cheers));
        Assert.Equal("true", ReadGlobalString(fixture.Config, ConfigurationKeys.Hangover));
        Assert.Equal("en-us", ReadGlobalString(fixture.Config, ConfigurationKeys.SoberLanguage));

        // The hangover is completed once at the next startup and then cleared.
        Assert.True(await coordinator.CompleteHangoverIfNeededAsync());
        Assert.False(await coordinator.CompleteHangoverIfNeededAsync());
        Assert.Equal("false", ReadGlobalString(fixture.Config, ConfigurationKeys.Hangover));
    }

    [Fact]
    public async Task SoberOnExit_RestoresSoberLanguage()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create("pallas");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Cheers] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.SoberLanguage] = JsonValue.Create("ja-jp");

        var coordinator = new UiLanguageCoordinator(fixture.Config);
        await coordinator.SoberOnExitAsync();

        Assert.Equal("ja-jp", ReadGlobalString(fixture.Config, ConfigurationKeys.Localization));
        Assert.Equal("ja-jp", coordinator.CurrentLanguage);
        Assert.Equal("true", ReadGlobalString(fixture.Config, ConfigurationKeys.Hangover));
        Assert.Equal("false", ReadGlobalString(fixture.Config, ConfigurationKeys.Cheers));

        // A sober session is not rewritten on exit.
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Hangover] = JsonValue.Create(false);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Cheers] = JsonValue.Create(false);
        await coordinator.SoberOnExitAsync();
        Assert.Equal("ja-jp", ReadGlobalString(fixture.Config, ConfigurationKeys.Localization));
    }

    [Fact]
    public void DidYouBuyWine_DetectsWineInEnabledMallTaskBuyFirst()
    {
        var config = new UnifiedConfig();
        config.Profiles["Default"] = new UnifiedProfile();
        var mall = new UnifiedTaskItem
        {
            Type = "Mall",
            IsEnabled = true,
            Params = new JsonObject { ["buy_first"] = new JsonArray("招聘许可", "酒") },
        };
        config.Profiles["Default"].TaskQueue.Add(mall);
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(PallasLanguageState.DidYouBuyWine(config, "Official", now));

        // Once recorded for the current YJ date, the trigger does not fire again.
        PallasLanguageState.MarkWineBought(config, "Official", now);
        Assert.False(PallasLanguageState.DidYouBuyWine(config, "Official", now));

        // A disabled Mall task does not count (WPF requires the Mall task to run).
        var disabledConfig = new UnifiedConfig();
        disabledConfig.Profiles["Default"] = new UnifiedProfile();
        disabledConfig.Profiles["Default"].TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            IsEnabled = false,
            Params = new JsonObject { ["buy_first"] = new JsonArray("酒") },
        });
        Assert.False(PallasLanguageState.DidYouBuyWine(disabledConfig, "Official", now));

        // No wine tokens, no trigger.
        var soberConfig = new UnifiedConfig();
        soberConfig.Profiles["Default"] = new UnifiedProfile();
        soberConfig.Profiles["Default"].TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            IsEnabled = true,
            Params = new JsonObject { ["buy_first"] = new JsonArray("招聘许可") },
        });
        Assert.False(PallasLanguageState.DidYouBuyWine(soberConfig, "Official", now));
    }

    [Fact]
    public async Task OperNameLanguageForce_PreservedThroughLoadAndSave()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.OperNameLanguage] = JsonValue.Create("OperNameLanguageForce.en-us");

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal("OperNameLanguageForce.en-us", vm.OperNameLanguage);
        Assert.NotNull(vm.SelectedOperNameLanguageOption);

        // Unrelated GUI saves must not clobber the imported force value.
        vm.UseTray = false;
        await vm.SaveGuiSettingsAsync();
        Assert.Equal("OperNameLanguageForce.en-us", ReadGlobalString(fixture.Config, ConfigurationKeys.OperNameLanguage));
    }

    [Fact]
    public async Task GuiSettings_LegacyControls_RoundTrip()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.False(vm.HideCloseButton);
        Assert.True(vm.UseCardLog);
        Assert.Equal(100, vm.MaxNumberOfLogThumbnails);
        Assert.False(vm.MainTasksInvertNullFunction);
        Assert.False(vm.WindowTitleShowConfigurationName);
        Assert.True(vm.WindowTitleShowConnectionPreset);
        Assert.True(vm.WindowTitleShowConnectionAddress);
        Assert.True(vm.WindowTitleShowClientType);

        vm.HideCloseButton = true;
        vm.UseCardLog = false;
        vm.MaxNumberOfLogThumbnails = 42;
        vm.MainTasksInvertNullFunction = true;
        vm.WindowTitleShowConnectionAddress = false;
        await vm.SaveGuiSettingsAsync();

        Assert.Equal("True", ReadGlobalString(fixture.Config, ConfigurationKeys.HideCloseButton));
        Assert.Equal("False", ReadGlobalString(fixture.Config, ConfigurationKeys.UseCardLog));
        Assert.Equal("42", ReadGlobalString(fixture.Config, ConfigurationKeys.MaxNumberOfLogThumbnails));
        Assert.Equal("True", ReadGlobalString(fixture.Config, "GUI.MainTasksInvertNullFunction"));
        Assert.Equal("2 4", ReadGlobalString(fixture.Config, ConfigurationKeys.WindowTitleSelectShowList));

        var reloaded = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await reloaded.InitializeAsync();
        Assert.True(reloaded.HideCloseButton);
        Assert.False(reloaded.UseCardLog);
        Assert.Equal(42, reloaded.MaxNumberOfLogThumbnails);
        Assert.True(reloaded.MainTasksInvertNullFunction);
        Assert.False(reloaded.WindowTitleShowConnectionAddress);
    }

    [Fact]
    public async Task AutoSave_OnCheckboxAndNumeric_TriggersSave()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.UseTray = false;
        vm.BackgroundOpacity = 73;

        await WaitUntilAsync(() =>
            string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.UseTray), "False", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundOpacity), "73", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeveloperModeToggle_AutoSaveAndRuntimeVerbose_ShouldStayInSync()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.False(vm.DeveloperModeEnabled);
        Assert.False(fixture.Runtime.LogService.VerboseEnabled);

        vm.DeveloperModeEnabled = true;
        await WaitUntilAsync(() =>
            string.Equals(ReadGlobalString(fixture.Config, "GUI.DeveloperMode"), "True", StringComparison.OrdinalIgnoreCase));
        Assert.True(fixture.Runtime.LogService.VerboseEnabled);

        vm.DeveloperModeEnabled = false;
        await WaitUntilAsync(() =>
            string.Equals(ReadGlobalString(fixture.Config, "GUI.DeveloperMode"), "False", StringComparison.OrdinalIgnoreCase));
        Assert.False(fixture.Runtime.LogService.VerboseEnabled);
    }

    [Fact]
    public async Task AutoSave_OnTextCommit_TriggersSave()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var path = CreateExistingFile(fixture.Root, "bg-text-commit.txt");
        vm.BackgroundImagePath = path;

        await Task.Delay(200);
        Assert.NotEqual(path, ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundImagePath));

        await vm.SaveGuiSettingsAsync();

        await WaitUntilAsync(() =>
            string.Equals(ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundImagePath), path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveFailure_KeepsInputAndMarksPending()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var baselinePath = CreateExistingFile(fixture.Root, "bg-before-fail.txt");
        vm.BackgroundImagePath = baselinePath;
        await vm.SaveGuiSettingsAsync();

        var missingPath = Path.Combine(fixture.Root, "not-exist.png");
        vm.BackgroundImagePath = missingPath;
        await vm.SaveGuiSettingsAsync();

        Assert.True(vm.HasPendingGuiChanges);
        Assert.Equal(missingPath, vm.BackgroundImagePath);
        Assert.Contains("does not exist", vm.GuiValidationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasGuiSectionValidationMessage);
        Assert.True(vm.HasBackgroundValidationMessage);
        Assert.Contains("does not exist", vm.BackgroundValidationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(baselinePath, ReadGlobalString(fixture.Config, ConfigurationKeys.BackgroundImagePath));
    }

    [Fact]
    public async Task LoadFromConfig_OutOfRangeBackground_ClampsAndWarns()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.BackgroundOpacity] = JsonValue.Create("-8");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.BackgroundBlurEffectRadius] = JsonValue.Create("999");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.BackgroundImageStretchMode] = JsonValue.Create("BadStretch");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.BackgroundImagePath] = JsonValue.Create(Path.Combine(fixture.Root, "missing.jpg"));
        fixture.Config.CurrentConfig.GlobalValues["Theme.Mode"] = JsonValue.Create("UnknownTheme");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create("No-Such-Language");

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal("SyncWithOs", vm.Theme);
        Assert.Equal("zh-cn", vm.Language);
        Assert.Equal(0, vm.BackgroundOpacity);
        Assert.Equal(50, vm.BackgroundBlur);
        Assert.Equal("Fill", vm.BackgroundStretchMode);
        // WPF parity: a missing background file keeps the stored path and renders no image.
        Assert.Equal(Path.Combine(fixture.Root, "missing.jpg"), vm.BackgroundImagePath);
        Assert.True(vm.HasGuiValidationMessage);
        Assert.True(vm.HasGuiSectionValidationMessage);
        Assert.True(vm.HasBackgroundValidationMessage);

        var eventLog = await File.ReadAllTextAsync(fixture.Diagnostics.EventLogPath);
        Assert.Contains("Settings.Gui.Normalize", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_AutostartFeedback_RemainsHiddenByDefault()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            autostartService: new ScriptedAutostartService(initialEnabled: false));
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        await vm.InitializeAsync();

        Assert.False(vm.StartSelf);
        Assert.False(vm.HasAutostartWarningMessage);
        Assert.False(vm.HasAutostartErrorMessage);
    }

    [Fact]
    public async Task StartSelf_Mismatch_ShowsDelayedWarningAfterOneSecond()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            autostartService: new ScriptedAutostartService(initialEnabled: false, keepExistingStateOnSet: true));
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.StartSelf = true;

        await Task.Delay(850);
        Assert.False(vm.HasAutostartWarningMessage);
        Assert.False(vm.HasAutostartErrorMessage);

        await WaitUntilAsync(() => vm.HasAutostartWarningMessage, timeoutMs: 2500);
        Assert.False(vm.StartSelf);
        Assert.Contains("未启用", vm.AutostartWarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartSelf_SetFailure_ShowsDelayedErrorAfterOneSecond()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            autostartService: new ScriptedAutostartService(initialEnabled: false, failOnSet: true));
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.StartSelf = true;

        await Task.Delay(850);
        Assert.False(vm.HasAutostartWarningMessage);
        Assert.False(vm.HasAutostartErrorMessage);

        await WaitUntilAsync(() => vm.HasAutostartErrorMessage, timeoutMs: 2500);
        Assert.False(vm.StartSelf);
        Assert.Contains("set failed", vm.AutostartErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartSelf_AdminDenied_ShowsLocalizedWpfGuidanceError()
    {
        // WPF parity (AutoStart.SetStart @23ddd271): the administrator+UAC denial
        // surfaces the localized LaunchOnSystemStartupAdminPrompt guidance instead of
        // the provider's raw English message.
        await using var fixture = await RuntimeFixture.CreateAsync(
            autostartService: new ScriptedAutostartService(initialEnabled: false, adminDeniedOnSet: true));
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.StartSelf = true;

        await WaitUntilAsync(() => vm.HasAutostartErrorMessage, timeoutMs: 2500);
        Assert.False(vm.StartSelf);
        Assert.Contains("任务计划程序", vm.AutostartErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Autostart cannot be enabled while MAAUnified is running as administrator.",
            vm.AutostartErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartRoundTrip_ThemeLanguageBackground_PersistAndReapply()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var first = await RuntimeFixture.CreateAsync(root, cleanupRoot: false))
            {
                var vm = new SettingsPageViewModel(first.Runtime, new ConnectionGameSharedStateViewModel());
                await vm.InitializeAsync();

                var path = CreateExistingFile(root, "bg-restart.txt");
                vm.Theme = "Dark";
                await vm.ChangeLanguageAsync("ko-kr");
                vm.BackgroundImagePath = path;
                vm.BackgroundOpacity = 52;
                vm.BackgroundBlur = 18;
                vm.BackgroundStretchMode = "Fill";
                await vm.SaveGuiSettingsAsync();
            }

            await using var second = await RuntimeFixture.CreateAsync(root, cleanupRoot: false);
            var reloaded = new SettingsPageViewModel(second.Runtime, new ConnectionGameSharedStateViewModel());
            await reloaded.InitializeAsync();

            Assert.Equal("Dark", reloaded.Theme);
            Assert.Equal("ko-kr", reloaded.Language);
            Assert.Equal(Path.Combine(root, "bg-restart.txt"), reloaded.BackgroundImagePath);
            Assert.Equal(52, reloaded.BackgroundOpacity);
            Assert.Equal(18, reloaded.BackgroundBlur);
            Assert.Equal("Fill", reloaded.BackgroundStretchMode);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public async Task LanguageApply_RefreshesTaskQueueAndTrayText()
    {
        var tray = new CapturingTrayService();
        await using var fixture = await RuntimeFixture.CreateAsync(trayService: tray);
        var vm = new MainShellViewModel(fixture.Runtime);
        try
        {
            await vm.SwitchLanguageToAsync("ja-jp");
            await WaitUntilAsync(() => string.Equals(vm.TaskQueuePage.Texts.Language, "ja-jp", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("ja-jp", vm.TaskQueuePage.Texts.Language);
            Assert.NotNull(tray.LastMenuText);
            Assert.Equal("スタート", tray.LastMenuText!.Start);
        }
        finally
        {
            TestShellCleanup.StopTimerScheduler(vm);
        }
    }

    private static Dictionary<string, string> CaptureGuiSnapshot(UnifiedConfigurationService config)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Theme.Mode"] = ReadGlobalString(config, "Theme.Mode"),
            [ConfigurationKeys.Localization] = ReadGlobalString(config, ConfigurationKeys.Localization),
            [ConfigurationKeys.UseTray] = ReadGlobalString(config, ConfigurationKeys.UseTray),
            [ConfigurationKeys.MinimizeToTray] = ReadGlobalString(config, ConfigurationKeys.MinimizeToTray),
            [ConfigurationKeys.WindowTitleScrollable] = ReadGlobalString(config, ConfigurationKeys.WindowTitleScrollable),
            [ConfigurationKeys.UiScalePercent] = ReadGlobalString(config, ConfigurationKeys.UiScalePercent),
            [ConfigurationKeys.BackgroundImagePath] = ReadGlobalString(config, ConfigurationKeys.BackgroundImagePath),
            [ConfigurationKeys.BackgroundOpacity] = ReadGlobalString(config, ConfigurationKeys.BackgroundOpacity),
            [ConfigurationKeys.BackgroundBlurEffectRadius] = ReadGlobalString(config, ConfigurationKeys.BackgroundBlurEffectRadius),
            [ConfigurationKeys.BackgroundImageStretchMode] = ReadGlobalString(config, ConfigurationKeys.BackgroundImageStretchMode),
        };
    }

    private static string ReadGlobalString(UnifiedConfigurationService config, string key)
    {
        if (!config.CurrentConfig.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text) && text is not null)
        {
            return text;
        }

        return node.ToString();
    }

    private static void SelectLanguageThroughView(GuiSettingsView view, SettingsPageViewModel vm, string language)
    {
        vm.SelectedLanguageValue = language;
    }

    private static string FindDisplay(IReadOnlyList<DisplayValueOption> options, string value)
    {
        return options.First(candidate => string.Equals(candidate.Value, value, StringComparison.OrdinalIgnoreCase)).Display;
    }

    private static string CreateExistingFile(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "dummy");
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("Expected condition was not met.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly bool _cleanupRoot;

        private RuntimeFixture(
            string root,
            MAAUnifiedRuntime runtime,
            UnifiedConfigurationService config,
            UiDiagnosticsService diagnostics,
            bool cleanupRoot)
        {
            Root = root;
            Runtime = runtime;
            Config = config;
            Diagnostics = diagnostics;
            _cleanupRoot = cleanupRoot;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public UnifiedConfigurationService Config { get; }

        public UiDiagnosticsService Diagnostics { get; }

        public static async Task<RuntimeFixture> CreateAsync(
            string? root = null,
            bool cleanupRoot = true,
            ITrayService? trayService = null,
            IAutostartService? autostartService = null,
            IUiLanguageCoordinator? uiLanguageCoordinator = null,
            IVersionUpdateFeatureService? versionUpdateFeatureService = null,
            Func<UnifiedConfigurationService, UiDiagnosticsService, UiLogService, IVersionUpdateFeatureService>?
                versionUpdateFeatureServiceFactory = null)
        {
            root ??= Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();

            var bridge = new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            trayService ??= new CapturingTrayService();
            var platform = new PlatformServiceBundle
            {
                TrayService = trayService,
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = autostartService ?? new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(session, config);
            var shell = new ShellFeatureService(connect);
            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connect,
                ShellFeatureService = shell,
                TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                VersionUpdateFeatureService = versionUpdateFeatureService
                    ?? versionUpdateFeatureServiceFactory?.Invoke(config, diagnostics, log)
                    ?? new VersionUpdateFeatureService(config, runtimeBaseDirectory: root),
                UiLanguageCoordinator = uiLanguageCoordinator ?? new UiLanguageCoordinator(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new RuntimeFixture(root, runtime, config, diagnostics, cleanupRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if (!_cleanupRoot)
            {
                return;
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    private sealed class ScriptedAutostartService : IAutostartService
    {
        private readonly bool _keepExistingStateOnSet;
        private readonly bool _failOnSet;
        private readonly bool _adminDeniedOnSet;

        public ScriptedAutostartService(
            bool initialEnabled,
            bool keepExistingStateOnSet = false,
            bool failOnSet = false,
            bool adminDeniedOnSet = false)
        {
            Enabled = initialEnabled;
            _keepExistingStateOnSet = keepExistingStateOnSet;
            _failOnSet = failOnSet;
            _adminDeniedOnSet = adminDeniedOnSet;
        }

        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "autostart test service",
            Provider: "test");

        public bool Enabled { get; private set; }

        public Task<PlatformOperationResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    Enabled,
                    "autostart queried",
                    "autostart.query"));
        }

        public Task<PlatformOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (_failOnSet)
            {
                return Task.FromResult(
                    PlatformOperation.Failed(
                        Capability.Provider,
                        "set failed",
                        PlatformErrorCodes.AutostartSetFailed,
                        "autostart.set"));
            }

            if (_adminDeniedOnSet && enabled)
            {
                return Task.FromResult(
                    PlatformOperation.Failed(
                        Capability.Provider,
                        "Autostart cannot be enabled while MAAUnified is running as administrator.",
                        PlatformErrorCodes.AutostartAdminDenied,
                        "autostart.set"));
            }

            if (!_keepExistingStateOnSet)
            {
                Enabled = enabled;
            }

            return Task.FromResult(
                PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    "autostart updated",
                    "autostart.set"));
        }
    }

    private sealed class CapturingTrayService : ITrayService
    {
        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "tray test service",
            Provider: "test");

        public event EventHandler<TrayCommandEvent>? CommandInvoked;

        public event EventHandler<TrayMenuRequestEvent>? MenuRequested;
        public event EventHandler<TrayActivationEvent>? ActivationRequested;

        public TrayMenuText? LastMenuText { get; private set; }

        public Task<PlatformOperationResult> InitializeAsync(
            string appTitle,
            TrayMenuText? menuText,
            CancellationToken cancellationToken = default)
        {
            LastMenuText = menuText;
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "initialized", "tray.initialize"));
        }

        public Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "shutdown", "tray.shutdown"));
        }

        public Task<PlatformOperationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "show", "tray.show"));
        }

        public Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "set-menu", "tray.setMenuState"));
        }

        public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "set-visible", "tray.setVisible"));
        }
    }

    private sealed class FailingUiLanguageCoordinator : IUiLanguageCoordinator
    {
        public FailingUiLanguageCoordinator(string currentLanguage)
        {
            CurrentLanguage = currentLanguage;
        }

        public string CurrentLanguage { get; }

        public int ChangeCallCount { get; private set; }

        public event EventHandler<UiLanguageChangedEventArgs>? LanguageChanged;
        public bool IsPallasUnlocked => false;

        public Task<bool> CompleteHangoverIfNeededAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);


        public Task<UiOperationResult<string>> ChangeLanguageAsync(string targetLanguage, CancellationToken cancellationToken = default)
        {
            ChangeCallCount++;
            return Task.FromResult(
                UiOperationResult<string>.Fail(
                    UiErrorCode.LanguageSwitchFailed,
                    $"Failed to switch language to {targetLanguage}."));
        }
    }

    private sealed class CountingUiLanguageCoordinator : IUiLanguageCoordinator
    {
        public CountingUiLanguageCoordinator(string currentLanguage)
        {
            CurrentLanguage = UiLanguageCatalog.Normalize(currentLanguage);
        }

        public string CurrentLanguage { get; private set; }

        public int ChangeCallCount { get; private set; }

        public event EventHandler<UiLanguageChangedEventArgs>? LanguageChanged;
        public bool IsPallasUnlocked => false;

        public Task<bool> CompleteHangoverIfNeededAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);


        public Task<UiOperationResult<string>> ChangeLanguageAsync(string targetLanguage, CancellationToken cancellationToken = default)
        {
            ChangeCallCount++;
            var normalized = UiLanguageCatalog.Normalize(targetLanguage);
            var previous = CurrentLanguage;
            CurrentLanguage = normalized;
            LanguageChanged?.Invoke(this, new UiLanguageChangedEventArgs(previous, normalized));
            return Task.FromResult(UiOperationResult<string>.Ok(normalized, $"Language switched to {normalized}."));
        }
    }

    private sealed class CallbackHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class SpyVersionUpdateFeatureService : IVersionUpdateFeatureService
    {
        public int SaveChannelCallCount { get; private set; }

        public int SaveProxyCallCount { get; private set; }

        public int SavePolicyCallCount { get; private set; }

        public int CheckResourceCallCount { get; private set; }

        public int CheckForUpdatesCallCount { get; private set; }

        public int UpdateResourceCallCount { get; private set; }

        public bool EmitSoftwareProgress { get; set; }

        public bool EmitResourceProgress { get; set; }

        public UiOperationResult<VersionUpdateCheckResult> CheckForUpdatesResult { get; set; } =
            UiOperationResult<VersionUpdateCheckResult>.Ok(
                new VersionUpdateCheckResult(
                    Channel: "Stable",
                    CurrentVersion: "v1.0.0",
                    TargetVersion: "v1.0.0",
                    ReleaseName: string.Empty,
                    Summary: string.Empty,
                    Body: string.Empty,
                    PackageName: string.Empty,
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: false,
                    HasPackage: false),
                "Checked.");

        public UiOperationResult<ResourceUpdateCheckResult> CheckResourceUpdateResult { get; set; } =
            UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: false,
                    DisplayVersion: string.Empty,
                    ReleaseNote: string.Empty,
                    VersionTimestamp: null,
                    RequiresMirrorChyanCdk: false,
                    DownloadUrl: null),
                "Resources are up to date.");

        public Task<UiOperationResult<VersionUpdatePolicy>> LoadPolicyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(UiOperationResult<VersionUpdatePolicy>.Ok(VersionUpdatePolicy.Default, "Loaded."));
        }

        public Task<UiOperationResult<ResourceVersionInfo>> LoadResourceVersionInfoAsync(
            string? clientType,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                UiOperationResult<ResourceVersionInfo>.Ok(
                    new ResourceVersionInfo(string.Empty, DateTime.MinValue),
                    "Loaded."));
        }

        public Task<UiOperationResult> SaveChannelAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
        {
            SaveChannelCallCount++;
            return Task.FromResult(UiOperationResult.Ok("Saved channel."));
        }

        public Task<UiOperationResult> SaveProxyAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
        {
            SaveProxyCallCount++;
            return Task.FromResult(UiOperationResult.Ok("Saved proxy."));
        }

        public Task<UiOperationResult> SavePolicyAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
        {
            SavePolicyCallCount++;
            return Task.FromResult(UiOperationResult.Ok("Saved policy."));
        }

        public Task<UiOperationResult<string>> UpdateResourceAsync(
            VersionUpdatePolicy policy,
            string? clientType,
            IProgress<VersionUpdateProgressInfo>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UpdateResourceCallCount++;
            if (EmitResourceProgress)
            {
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.ResourcePackage,
                    VersionUpdateProgressStage.Started,
                    VersionUpdateProgressSource.GlobalSource));
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.ResourcePackage,
                    VersionUpdateProgressStage.Downloading,
                    VersionUpdateProgressSource.GlobalSource,
                    BytesTransferred: 8 * 1024 * 1024,
                    TotalBytes: 16 * 1024 * 1024,
                    BytesPerSecond: 2 * 1024 * 1024));
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.ResourcePackage,
                    VersionUpdateProgressStage.Preparing,
                    VersionUpdateProgressSource.GlobalSource));
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.ResourcePackage,
                    VersionUpdateProgressStage.Completed,
                    VersionUpdateProgressSource.GlobalSource));
            }
            return Task.FromResult(UiOperationResult<string>.Ok("资源更新完成（Github）。", "Updated."));
        }

        public Task<UiOperationResult<ResourceUpdateCheckResult>> CheckResourceUpdateAsync(
            VersionUpdatePolicy policy,
            string? clientType,
            CancellationToken cancellationToken = default)
        {
            CheckResourceCallCount++;
            return Task.FromResult(CheckResourceUpdateResult);
        }

        public Task<UiOperationResult<VersionUpdateCheckResult>> CheckForUpdatesAsync(
            VersionUpdatePolicy policy,
            string currentVersion,
            IProgress<VersionUpdateProgressInfo>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CheckForUpdatesCallCount++;
            if (EmitSoftwareProgress)
            {
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.SoftwarePackage,
                    VersionUpdateProgressStage.Started));
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.SoftwarePackage,
                    VersionUpdateProgressStage.Downloading,
                    BytesTransferred: 32 * 1024 * 1024,
                    TotalBytes: 64 * 1024 * 1024,
                    BytesPerSecond: 4 * 1024 * 1024));
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.SoftwarePackage,
                    VersionUpdateProgressStage.Completed));
            }

            return Task.FromResult(CheckForUpdatesResult with
            {
                Value = CheckForUpdatesResult.Value is null
                    ? null
                    : CheckForUpdatesResult.Value with { CurrentVersion = currentVersion },
            });
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        public int ReloadResourceCallCount { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));
        }

        public Task<CoreResult<bool>> ReloadResourceAsync(string? clientType = null, CancellationToken cancellationToken = default)
        {
            ReloadResourceCallCount++;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
