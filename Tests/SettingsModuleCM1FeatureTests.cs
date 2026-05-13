using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.IO.Compression;
using System.Text.Json.Nodes;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Features.Settings;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class SettingsModuleCM1FeatureTests
{
    [Fact]
    public async Task VersionUpdate_SaveChannelAndProxy_UseSeparatedPipelines()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.UpdateProxy] = JsonValue.Create("http://127.0.0.1:5000");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.ProxyType] = JsonValue.Create("http");

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.VersionUpdateVersionType = "Beta";
        vm.VersionUpdateResourceSource = "MirrorChyan";
        vm.VersionUpdateStartupCheck = false;
        vm.VersionUpdateScheduledCheck = true;
        vm.VersionUpdateProxy = "127.0.0.1:7890";
        vm.VersionUpdateProxyType = "socks5";
        vm.VersionUpdateResourceApi = "https://example.com/api";

        await vm.SaveVersionUpdateChannelAsync();

        Assert.Equal("Beta", ReadGlobalString(fixture.Config, ConfigurationKeys.VersionType));
        Assert.Equal("MirrorChyan", ReadGlobalString(fixture.Config, ConfigurationKeys.UpdateSource));
        Assert.Equal("False", ReadGlobalString(fixture.Config, ConfigurationKeys.StartupUpdateCheck));
        Assert.Equal("True", ReadGlobalString(fixture.Config, ConfigurationKeys.UpdateAutoCheck));
        Assert.Equal("http://127.0.0.1:5000", ReadGlobalString(fixture.Config, ConfigurationKeys.UpdateProxy));
        Assert.Contains("通道", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);

        await vm.SaveVersionUpdateProxyAsync();

        Assert.Equal("127.0.0.1:7890", ReadGlobalString(fixture.Config, ConfigurationKeys.UpdateProxy));
        Assert.Equal("socks5", ReadGlobalString(fixture.Config, ConfigurationKeys.ProxyType));
        Assert.Equal("https://example.com/api", ReadGlobalString(fixture.Config, ConfigurationKeys.ResourceApi));
        Assert.Contains("代理", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);
        Assert.False(vm.HasVersionUpdateErrorMessage);
    }

    [Fact]
    public async Task VersionUpdate_CheckForUpdates_ShowsNotImplementedMessage()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            coreVersionResolver: () => "core-9.9.9");
        await vm.InitializeAsync();

        var feedPath = Path.Combine(Path.GetTempPath(), $"maa-unified-settings-feed-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(feedPath, """
                [
                  {
                    "tag_name": "v2.0.0",
                    "name": "Release v2.0.0",
                    "body": "Line one.\nLine two.",
                    "prerelease": false,
                    "assets": [
                      {
                        "name": "MAAUnified-v2.0.0-linux-x64.AppImage",
                        "browser_download_url": "https://example.com/MAAUnified-v2.0.0-linux-x64.AppImage",
                        "size": 1234
                      }
                    ]
                  }
                ]
                """);

            vm.VersionUpdateVersionType = "Stable";
            vm.VersionUpdateResourceApi = feedPath;
            vm.VersionUpdateAutoDownload = false;

            await vm.CheckVersionUpdateAsync();

            Assert.Contains("v2.0.0", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);
            Assert.False(vm.HasVersionUpdateErrorMessage);
            Assert.True(vm.HasPendingVersionUpdateAvailability);
            Assert.Equal("core-9.9.9", vm.UpdatePanelCoreVersion);
        }
        finally
        {
            try
            {
                if (File.Exists(feedPath))
                {
                    File.Delete(feedPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task VersionUpdate_CheckForUpdates_UsesUiVersionForSoftwareUpdate()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            coreVersionResolver: () => "v999.0.0");
        await vm.InitializeAsync();

        var feedPath = Path.Combine(Path.GetTempPath(), $"maa-unified-settings-core-feed-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(feedPath, """
                [
                  {
                    "tag_name": "v2.0.0",
                    "name": "Release v2.0.0",
                    "body": "Line one.\nLine two.",
                    "prerelease": false,
                    "assets": [
                      {
                        "name": "MAAUnified-v2.0.0-linux-x64.AppImage",
                        "browser_download_url": "https://example.com/MAAUnified-v2.0.0-linux-x64.AppImage",
                        "size": 1234
                      }
                    ]
                  }
                ]
                """);

            vm.VersionUpdateVersionType = "Stable";
            vm.VersionUpdateResourceApi = feedPath;
            vm.VersionUpdateAutoDownload = false;

            await vm.CheckVersionUpdateAsync();

            Assert.Equal("v999.0.0", vm.UpdatePanelCoreVersion);
            Assert.True(vm.HasPendingVersionUpdateAvailability);
            Assert.Contains("v2.0.0", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (File.Exists(feedPath))
                {
                    File.Delete(feedPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task VersionUpdate_CheckForUpdates_WithOnlyLegacyMaaPackage_DoesNotShowAvailability()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            coreVersionResolver: () => "v1.0.0");
        await vm.InitializeAsync();

        var feedPath = Path.Combine(Path.GetTempPath(), $"maa-unified-settings-legacy-feed-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(feedPath, """
                [
                  {
                    "tag_name": "v2.0.0",
                    "name": "Release v2.0.0",
                    "body": "Line one.\nLine two.",
                    "prerelease": false,
                    "assets": [
                      {
                        "name": "MAA-v2.0.0-linux-x64.AppImage",
                        "browser_download_url": "https://example.com/MAA-v2.0.0-linux-x64.AppImage",
                        "size": 1234
                      }
                    ]
                  }
                ]
                """);

            vm.VersionUpdateVersionType = "Stable";
            vm.VersionUpdateResourceApi = feedPath;
            vm.VersionUpdateAutoDownload = false;

            await vm.CheckVersionUpdateAsync();

            Assert.False(vm.HasPendingVersionUpdateAvailability);
            Assert.False(vm.HasVersionUpdateErrorMessage);
            Assert.Contains("当前已是最新", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);
            Assert.Equal("v1.0.0", vm.UpdatePanelCoreVersion);
        }
        finally
        {
            try
            {
                if (File.Exists(feedPath))
                {
                    File.Delete(feedPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task VersionUpdate_DefaultAutoDownload_ShouldMatchWpfDefault()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(VersionUpdatePolicy.Default.AutoDownloadUpdatePackage);
        Assert.True(vm.VersionUpdateAutoDownload);
    }

    [Fact]
    public async Task CheckAboutAnnouncementWithDialogAsync_WhenRemoteTimesOut_UsesCachedAnnouncement()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var cachedAnnouncement = "# Cached announcement";
        var saveResult = await fixture.Runtime.AnnouncementFeatureService.SaveStateAsync(
            new AnnouncementState(cachedAnnouncement, false, false));
        Assert.True(saveResult.Success);

        using var aboutAnnouncementHttpClient = new HttpClient(new TimeoutUntilCanceledMessageHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var dialogService = new RecordingAnnouncementDialogService();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            dialogService: dialogService,
            aboutAnnouncementHttpClient: aboutAnnouncementHttpClient,
            aboutAnnouncementTimeout: TimeSpan.FromMilliseconds(50));
        await vm.InitializeAsync();

        await vm.CheckAndDownloadAboutAnnouncementWithDialogAsync();

        Assert.NotNull(dialogService.LastAnnouncementRequest);
        Assert.Equal(cachedAnnouncement, dialogService.LastAnnouncementRequest!.AnnouncementInfo);
        Assert.False(vm.HasAboutErrorMessage);
    }

    [Fact]
    public async Task VersionUpdate_CheckForUpdates_ShouldNotRaiseErrorDialog()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var dialogRaised = false;
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, _) => dialogRaised = true;

        var feedPath = Path.Combine(Path.GetTempPath(), $"maa-unified-settings-feed-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(feedPath, """
                [
                  {
                    "tag_name": "v2.0.0",
                    "name": "Release v2.0.0",
                    "body": "Line one.\nLine two.",
                    "prerelease": false,
                    "assets": [
                      {
                        "name": "MAAUnified-v2.0.0-linux-x64.AppImage",
                        "browser_download_url": "https://example.com/MAAUnified-v2.0.0-linux-x64.AppImage",
                        "size": 1234
                      }
                    ]
                  }
                ]
                """);

            vm.VersionUpdateVersionType = "Stable";
            vm.VersionUpdateResourceApi = feedPath;
            vm.VersionUpdateAutoDownload = false;
            await vm.CheckVersionUpdateAsync();
        }
        finally
        {
            try
            {
                if (File.Exists(feedPath))
                {
                    File.Delete(feedPath);
                }
            }
            catch
            {
            }
        }

        Assert.False(vm.HasVersionUpdateErrorMessage);
        Assert.Contains("v2.0.0", vm.VersionUpdateStatusMessage, StringComparison.Ordinal);
        Assert.False(dialogRaised);
    }

    [Fact]
    public async Task ConfigurationManager_AddMoveSwitchDelete_SyncsWithUnifiedConfiguration()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Single(vm.ConfigurationProfiles);
        Assert.Equal("Default", vm.ConfigurationProfiles[0]);

        vm.ConfigurationManagerNewProfileName = "Alpha";
        await vm.AddConfigurationProfileAsync();

        Assert.Contains("Alpha", vm.ConfigurationProfiles, StringComparer.OrdinalIgnoreCase);
        Assert.True(fixture.Config.CurrentConfig.Profiles.ContainsKey("Alpha"));

        vm.ConfigurationManagerSelectedProfile = "Alpha";
        await vm.SwitchConfigurationProfileAsync();
        Assert.Equal("Alpha", fixture.Config.CurrentConfig.CurrentProfile);

        await vm.MoveConfigurationProfileUpAsync();
        Assert.Equal("Alpha", fixture.Config.CurrentConfig.Profiles.Keys.First());

        vm.ConfigurationManagerSelectedProfile = "Default";
        await vm.DeleteConfigurationProfileAsync();

        Assert.False(fixture.Config.CurrentConfig.Profiles.ContainsKey("Default"));
        Assert.Single(vm.ConfigurationProfiles);
        Assert.Equal("Alpha", vm.ConfigurationProfiles[0]);
    }

    [Fact]
    public async Task ConfigurationManager_SwitchProfile_ShouldRaiseConfigurationContextChanged()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Alt"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectAddress"] = JsonValue.Create("10.9.0.8:5555"),
            },
        };
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        ConfigurationContextChangedEventArgs? raised = null;
        vm.ConfigurationContextChanged += (_, args) => raised = args;

        vm.ConfigurationManagerSelectedProfile = "Alt";
        await vm.SwitchConfigurationProfileAsync();

        Assert.NotNull(raised);
        Assert.Equal(ConfigurationContextChangeReason.ProfileSwitched, raised!.Reason);
        Assert.Equal("10.9.0.8:5555", vm.ConnectionGameSharedState.ConnectAddress);
    }

    [Fact]
    public async Task ConfigurationManager_SwitchProfile_ShouldNotResetProfileCollection()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Alt"] = new UnifiedProfile();
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var resetCount = 0;
        vm.ConfigurationProfiles.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };

        vm.ConfigurationManagerSelectedProfile = "Alt";
        await vm.SwitchConfigurationProfileAsync();

        Assert.Equal("Alt", fixture.Config.CurrentConfig.CurrentProfile);
        Assert.Equal(0, resetCount);
    }

    [Fact]
    public async Task ConfigurationManager_DeleteCurrentProfile_ShouldAutoSwitchToRemainingProfile()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Alt"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectAddress"] = JsonValue.Create("10.8.0.4:5555"),
            },
        };
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.ConfigurationManagerSelectedProfile = "Alt";
        await vm.SwitchConfigurationProfileAsync();

        ConfigurationContextChangedEventArgs? raised = null;
        vm.ConfigurationContextChanged += (_, args) => raised = args;

        await vm.DeleteConfigurationProfileAsync();

        Assert.Equal("Default", fixture.Config.CurrentConfig.CurrentProfile);
        Assert.DoesNotContain("Alt", vm.ConfigurationProfiles, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(raised);
        Assert.Equal(ConfigurationContextChangeReason.ProfileSwitched, raised!.Reason);
        Assert.Contains("已删除", raised.Message, StringComparison.Ordinal);
        Assert.Contains("已切换至配置 `Default`", raised.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationManager_DeleteNonCurrentProfile_ShouldReloadCurrentProfileContext()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Default"].Values["ConnectAddress"] = JsonValue.Create("10.0.0.2:5555");
        fixture.Config.CurrentConfig.Profiles["Alt"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectAddress"] = JsonValue.Create("10.0.0.9:5555"),
            },
        };
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.ConnectionGameSharedState.ConnectAddress = "stale-value";
        vm.ConfigurationManagerSelectedProfile = "Alt";

        ConfigurationContextChangedEventArgs? raised = null;
        vm.ConfigurationContextChanged += (_, args) => raised = args;

        await vm.DeleteConfigurationProfileAsync();

        Assert.Equal("Default", fixture.Config.CurrentConfig.CurrentProfile);
        Assert.Equal("10.0.0.2:5555", vm.ConnectionGameSharedState.ConnectAddress);
        Assert.NotNull(raised);
        Assert.Equal(ConfigurationContextChangeReason.ProfileSwitched, raised!.Reason);
        Assert.Contains("已重新加载配置 `Default`", raised.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationManager_DeleteLastProfile_ShouldRecreateDefaultAndLoad()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles.Clear();
        fixture.Config.CurrentConfig.CurrentProfile = "Solo";
        fixture.Config.CurrentConfig.Profiles["Solo"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectAddress"] = JsonValue.Create("172.16.0.7:5555"),
            },
        };
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.ConnectionGameSharedState.ConnectAddress = "stale-value";
        vm.ConfigurationManagerSelectedProfile = "Solo";

        await vm.DeleteConfigurationProfileAsync();

        Assert.Equal("default", fixture.Config.CurrentConfig.CurrentProfile);
        Assert.Single(fixture.Config.CurrentConfig.Profiles);
        Assert.True(fixture.Config.CurrentConfig.Profiles.ContainsKey("default"));
        Assert.Equal("default", vm.ConfigurationManagerSelectedProfile);
        Assert.Equal("127.0.0.1:5555", vm.ConnectionGameSharedState.ConnectAddress);
        Assert.Contains("已切换至配置 `default`", vm.ConfigurationManagerStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportConfigurations_ShouldRaiseUnifiedImportConfigurationContextChanged()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var importPath = Path.Combine(fixture.Root, "imported-config.json");
        await File.WriteAllTextAsync(
            importPath,
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Imported",
              "Profiles": {
                "Imported": {
                  "Values": {
                    "ConnectAddress": "172.20.0.4:5555"
                  },
                  "TaskQueue": []
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        ConfigurationContextChangedEventArgs? raised = null;
        vm.ConfigurationContextChanged += (_, args) => raised = args;

        await vm.ImportConfigurationsAsync(importPath);

        Assert.NotNull(raised);
        Assert.Equal(ConfigurationContextChangeReason.UnifiedImport, raised!.Reason);
        Assert.Contains("已导入 1 个配置", raised.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportLegacyConfigurations_ShouldRefreshTimerProfileSelection()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var legacyGuiPath = Path.Combine(fixture.Root, "config", "gui.json");
        await File.WriteAllTextAsync(
            legacyGuiPath,
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Timer.Timer1": true,
                  "Timer.Timer1Hour": 8,
                  "Timer.Timer1Min": 15,
                  "Timer.Timer1.Config": "Night"
                },
                "Night": {}
              },
              "Global": {
                "Timer.CustomConfig": true
              }
            }
            """);

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var report = await vm.ImportLegacyConfigurationsAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(null, legacyGuiPath),
                ImportSource.GuiOnly,
                ManualImport: true,
                AllowPartialImport: true));

        Assert.True(report.AppliedConfig);
        Assert.Contains("Night", vm.ConfigurationProfiles, StringComparer.OrdinalIgnoreCase);
        Assert.True(vm.CustomTimerConfig);
        Assert.True(vm.Timers[0].Enabled);
        Assert.Equal("08:15", vm.Timers[0].Time);
        Assert.Equal("Night", vm.Timers[0].Profile);
    }

    [Fact]
    public async Task SettingsPage_ShouldLoadProfileScopedLegacyValues_AndRefreshThemOnProfileSwitch()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlGetTaskEndpointUri] = JsonValue.Create("https://default.example/task");
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.PenguinId] = JsonValue.Create("penguin-default");
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.StartEmulator] = JsonValue.Create("True");
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("SMTP");

        fixture.Config.CurrentConfig.Profiles["Alt"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationKeys.RemoteControlGetTaskEndpointUri] = JsonValue.Create("https://alt.example/task"),
                [ConfigurationKeys.PenguinId] = JsonValue.Create("penguin-alt"),
                [ConfigurationKeys.StartEmulator] = JsonValue.Create("False"),
                [ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create(string.Empty),
            },
        };
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal("https://default.example/task", vm.RemoteGetTaskEndpoint);
        Assert.Equal("penguin-default", vm.PenguinId);
        Assert.True(vm.OpenEmulatorAfterLaunch);
        Assert.True(vm.ExternalNotificationEnabled);

        vm.ConfigurationManagerSelectedProfile = "Alt";
        await vm.SwitchConfigurationProfileAsync();

        Assert.Equal("https://alt.example/task", vm.RemoteGetTaskEndpoint);
        Assert.Equal("penguin-alt", vm.PenguinId);
        Assert.False(vm.OpenEmulatorAfterLaunch);
        Assert.False(vm.ExternalNotificationEnabled);
    }

    [Fact]
    public async Task SettingsPage_ShouldKeepWpfGlobalSettingsStableAcrossProfileSwitch()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create("zh-cn");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.UseTray] = JsonValue.Create("True");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.ForceScheduledStart] = JsonValue.Create("True");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.ShowWindowBeforeForceScheduledStart] = JsonValue.Create("False");
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.Localization] = JsonValue.Create("zh-cn");
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.UseTray] = JsonValue.Create("True");
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.ForceScheduledStart] = JsonValue.Create("False");
        fixture.Config.CurrentConfig.Profiles["Alt"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationKeys.Localization] = JsonValue.Create("en-us"),
                [ConfigurationKeys.UseTray] = JsonValue.Create("False"),
                [ConfigurationKeys.ForceScheduledStart] = JsonValue.Create("False"),
                [ConfigurationKeys.ShowWindowBeforeForceScheduledStart] = JsonValue.Create("True"),
            },
        };
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal("zh-cn", vm.Language);
        Assert.True(vm.UseTray);
        Assert.True(vm.ForceScheduledStart);
        Assert.False(vm.ShowWindowBeforeForceScheduledStart);

        vm.ConfigurationManagerSelectedProfile = "Alt";
        await vm.SwitchConfigurationProfileAsync();

        Assert.Equal("zh-cn", vm.Language);
        Assert.True(vm.UseTray);
        Assert.True(vm.ForceScheduledStart);
        Assert.False(vm.ShowWindowBeforeForceScheduledStart);
    }

    [Fact]
    public void ConfigurationImportSelectionAnalyzer_SingleUnifiedJson_ShouldClassifyUnifiedConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var filePath = Path.Combine(root, "exported.json");
            File.WriteAllText(
                filePath,
                """
                {
                  "SchemaVersion": 2,
                  "CurrentProfile": "Default",
                  "Profiles": {
                    "Default": {
                      "Values": {},
                      "TaskQueue": []
                    }
                  },
                  "GlobalValues": {},
                  "Migration": {}
                }
                """);

            var analysis = ConfigurationImportSelectionAnalyzer.Analyze([filePath]);

            Assert.Equal(ConfigurationImportSelectionKind.UnifiedConfig, analysis.Kind);
            Assert.Equal(filePath, analysis.UnifiedConfigPath);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    [Fact]
    public void ConfigurationImportSelectionAnalyzer_LegacyDirectory_ShouldAcceptRootOrConfigFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        try
        {
            var guiNewPath = Path.Combine(config, "gui.new.json");
            var guiPath = Path.Combine(config, "gui.json");
            File.WriteAllText(guiNewPath, "{}");
            File.WriteAllText(guiPath, "{}");

            var rootAnalysis = ConfigurationImportSelectionAnalyzer.AnalyzeLegacyDirectory(root, static key => key);
            var configAnalysis = ConfigurationImportSelectionAnalyzer.AnalyzeLegacyDirectory(config, static key => key);

            Assert.Equal(ConfigurationImportSelectionKind.LegacyReady, rootAnalysis.Kind);
            Assert.Equal(guiNewPath, rootAnalysis.GuiNewPath);
            Assert.Equal(guiPath, rootAnalysis.GuiPath);
            Assert.Equal(ConfigurationImportSelectionKind.LegacyReady, configAnalysis.Kind);
            Assert.Equal(guiNewPath, configAnalysis.GuiNewPath);
            Assert.Equal(guiPath, configAnalysis.GuiPath);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    [Fact]
    public void ConfigurationImportSelectionAnalyzer_UnifiedDirectory_ShouldAcceptRootOrConfigFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        try
        {
            var avaloniaPath = Path.Combine(config, "avalonia.json");
            File.WriteAllText(avaloniaPath, "{}");

            var rootAnalysis = ConfigurationImportSelectionAnalyzer.AnalyzeUnifiedDirectory(root, static key => key);
            var configAnalysis = ConfigurationImportSelectionAnalyzer.AnalyzeUnifiedDirectory(config, static key => key);

            Assert.Equal(ConfigurationImportSelectionKind.UnifiedConfig, rootAnalysis.Kind);
            Assert.Equal(avaloniaPath, rootAnalysis.UnifiedConfigPath);
            Assert.Equal(ConfigurationImportSelectionKind.UnifiedConfig, configAnalysis.Kind);
            Assert.Equal(avaloniaPath, configAnalysis.UnifiedConfigPath);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    [Fact]
    public async Task ConfigurationProfileFeatureService_AddProfile_SaveFailure_RollsBack()
    {
        var config = CreateConfigServiceWithStore(new ThrowOnSaveStore());
        var service = new ConfigurationProfileFeatureService(config);

        var result = await service.AddProfileAsync("Alpha");

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.ConfigurationProfileSaveFailed, result.Error?.Code);
        Assert.Single(config.CurrentConfig.Profiles);
        Assert.True(config.CurrentConfig.Profiles.ContainsKey("Default"));
        Assert.False(config.CurrentConfig.Profiles.ContainsKey("Alpha"));
    }

    [Fact]
    public async Task VersionUpdateFeatureService_SaveProxy_SaveFailure_RollsBack()
    {
        var config = CreateConfigServiceWithStore(new ThrowOnSaveStore());
        config.CurrentConfig.GlobalValues[ConfigurationKeys.UpdateProxy] = JsonValue.Create("http://127.0.0.1:7000");
        var service = new VersionUpdateFeatureService(config, runtimeBaseDirectory: Path.GetTempPath());

        var policy = VersionUpdatePolicy.Default with
        {
            Proxy = "https://127.0.0.1:7890",
            ProxyType = "https",
            ResourceApi = "https://example.com/resource",
        };

        var result = await service.SaveProxyAsync(policy);

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.VersionUpdateSaveFailed, result.Error?.Code);
        Assert.Equal("http://127.0.0.1:7000", ReadGlobalString(config, ConfigurationKeys.UpdateProxy));
    }

    [Fact]
    public async Task IssueReport_BundleContainsRequiredArtifacts_AndIssueActionsAreRunnable()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var openedTargets = new List<string>();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            openExternalTargetAsync: (target, _) =>
            {
                openedTargets.Add(target);
                return Task.FromResult(UiOperationResult.Ok($"opened:{target}"));
            });
        await vm.InitializeAsync();

        var debugDirectory = Path.GetDirectoryName(fixture.Runtime.DiagnosticsService.EventLogPath)!;
        Directory.CreateDirectory(debugDirectory);
        await File.WriteAllTextAsync(Path.Combine(debugDirectory, "config-import-report.json"), "{}");
        await fixture.Runtime.DiagnosticsService.RecordErrorAsync("test", "synthetic ui error");
        await fixture.Runtime.DiagnosticsService.RecordEventAsync("test", "synthetic ui event");
        await File.WriteAllTextAsync(fixture.Runtime.DiagnosticsService.PlatformEventLogPath, "{\"event\":\"synthetic\"}");

        await vm.BuildIssueReportAsync();
        Assert.False(string.IsNullOrWhiteSpace(vm.IssueReportPath));
        Assert.True(File.Exists(vm.IssueReportPath));

        using (var archive = ZipFile.OpenRead(vm.IssueReportPath))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "config/avalonia.json");
            Assert.Contains(archive.Entries, e => e.FullName == "debug/config-import-report.json");
            Assert.Contains(archive.Entries, e => e.FullName == "debug/avalonia-ui-errors.log");
            Assert.Contains(archive.Entries, e => e.FullName == "debug/avalonia-ui-events.log");
            Assert.Contains(archive.Entries, e => e.FullName == "debug/avalonia-platform-events.log");
        }

        await vm.OpenIssueReportHelpAsync();
        await vm.OpenIssueReportEntryAsync();
        await vm.OpenIssueReportDebugDirectoryAsync();
        Assert.Equal(3, openedTargets.Count);
        Assert.Contains("https://maa.plus/docs/", openedTargets, StringComparer.Ordinal);
        Assert.Contains("issues/new/choose", openedTargets[1], StringComparison.Ordinal);
        Assert.Equal(debugDirectory, openedTargets[2]);
    }

    [Fact]
    public async Task IssueReport_DialogEntryAction_ShouldReuseSharedIssueEntryRoute_WithoutMutatingStatus()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var openedTargets = new List<string>();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            openExternalTargetAsync: (target, _) =>
            {
                openedTargets.Add(target);
                return Task.FromResult(UiOperationResult.Ok($"opened:{target}"));
            });
        await vm.InitializeAsync();

        var result = await vm.OpenIssueReportEntryForDialogAsync();

        Assert.True(result.Success);
        Assert.Single(openedTargets);
        Assert.Contains("issues/new/choose", openedTargets[0], StringComparison.Ordinal);
        Assert.False(vm.HasIssueReportPath);
        Assert.False(vm.HasIssueReportStatusMessage);
        Assert.True(string.IsNullOrEmpty(vm.IssueReportStatusMessage));
        Assert.False(vm.HasIssueReportErrorMessage);
    }

    [Fact]
    public async Task IssueReport_ClearImageCache_RemovesFiles()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            openExternalTargetAsync: (target, _) => Task.FromResult(UiOperationResult.Ok(target)));
        await vm.InitializeAsync();

        var baseDirectory = Directory.GetParent(Path.GetDirectoryName(fixture.Runtime.DiagnosticsService.EventLogPath)!)!.FullName;
        var cacheDirectory = Path.Combine(baseDirectory, "cache", "images");
        Directory.CreateDirectory(Path.Combine(cacheDirectory, "nested"));
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "a.tmp"), "1");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "nested", "b.tmp"), "2");

        await vm.ClearIssueReportImageCacheAsync();

        Assert.True(Directory.Exists(cacheDirectory));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories));
        Assert.False(vm.HasIssueReportErrorMessage);
    }

    [Fact]
    public async Task AboutAndAchievement_ActionsRouteToFeatureServicesAndLauncher()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var openedTargets = new List<string>();
        var vm = new SettingsPageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            openExternalTargetAsync: (target, _) =>
            {
                openedTargets.Add(target);
                return Task.FromResult(UiOperationResult.Ok($"opened:{target}"));
            });
        await vm.InitializeAsync();

        Assert.False(string.IsNullOrWhiteSpace(vm.AboutVersionInfo));

        await vm.RefreshAchievementPolicyAsync();
        Assert.Contains("当前策略：", vm.AchievementPolicySummary, StringComparison.Ordinal);
        Assert.False(vm.HasAchievementErrorMessage);

        await vm.OpenAchievementGuideAsync();
        await vm.OpenAboutOfficialWebsiteAsync();
        await vm.OpenAboutCommunityAsync();
        await vm.OpenAboutDownloadAsync();
        await vm.CheckAboutAnnouncementAsync();

        Assert.True(openedTargets.Count >= 4);
        Assert.Contains(openedTargets, target => target.Contains("maa.plus", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(vm.AboutStatusMessage));
        Assert.False(vm.HasAboutErrorMessage);
    }

    private static UnifiedConfigurationService CreateConfigServiceWithStore(IUnifiedConfigStore store)
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            root);
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

    private sealed class ThrowOnSaveStore : IUnifiedConfigStore
    {
        public string ConfigPath => "throw-on-save";

        public bool Exists() => false;

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UnifiedConfig?>(null);
        }

        public Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
        {
            throw new IOException("Simulated save failure.");
        }

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly bool _cleanupRoot;

        private RuntimeFixture(
            string root,
            MAAUnifiedRuntime runtime,
            UnifiedConfigurationService config,
            bool cleanupRoot)
        {
            Root = root;
            Runtime = runtime;
            Config = config;
            _cleanupRoot = cleanupRoot;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public UnifiedConfigurationService Config { get; }

        public static async Task<RuntimeFixture> CreateAsync(string? root = null, bool cleanupRoot = true)
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
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
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
                ConfigurationProfileFeatureService = new ConfigurationProfileFeatureService(config),
                VersionUpdateFeatureService = new VersionUpdateFeatureService(config, runtimeBaseDirectory: root),
                AchievementFeatureService = new AchievementFeatureService(config),
                AnnouncementFeatureService = new AnnouncementFeatureService(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new RuntimeFixture(root, runtime, config, cleanupRoot);
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

    private sealed class TimeoutUntilCanceledMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingAnnouncementDialogService : IAppDialogService
    {
        public AnnouncementDialogRequest? LastAnnouncementRequest { get; private set; }

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            LastAnnouncementRequest = request;
            return Task.FromResult(new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));
        }

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(DialogReturnSemantic.Close, null, "recorded"));
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
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
