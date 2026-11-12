using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.ThirdParty;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

/// <summary>
/// P1-10 (WPF 247db567f3): the third-party service settings get their own section, and the
/// yituliu OpenAPI token/switch persist next to the reporting toggles.
/// </summary>
public sealed class SettingsThirdPartyFeatureTests
{
    [Fact]
    public async Task Sections_ShouldPlaceThirdPartyBetweenNotificationsAndHotkeys()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var keys = vm.Sections.Select(section => section.Key).ToList();
        Assert.Contains("ThirdParty", keys);
        Assert.True(keys.IndexOf("ThirdParty") > keys.IndexOf("Notification"));
        Assert.True(keys.IndexOf("ThirdParty") < keys.IndexOf("HotKey"));
        var section = vm.Sections.Single(item => item.Key == "ThirdParty");
        Assert.False(string.IsNullOrWhiteSpace(section.DisplayName));
    }

    [Fact]
    public async Task ThirdPartySection_ShouldExposeDedicatedSaveAction()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedSection = vm.Sections.Single(section => section.Key == "ThirdParty");

        var primary = Assert.Single(vm.CurrentSectionActions, action => action.IsPrimary);
        Assert.Equal("settings.save-third-party", primary.ActionId);
        Assert.False(string.IsNullOrWhiteSpace(primary.Label));
    }

    [Fact]
    public async Task YituliuOpenApiSettings_ShouldLoadFromProfileAndAutoSave()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var config = fixture.Runtime.ConfigurationService;
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.Values[ThirdPartySettingKeys.YituliuOpenApiToken] = JsonValue.Create("seeded-token");
        profile.Values[ThirdPartySettingKeys.OperBoxUseYituliuApi] = JsonValue.Create(true);

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Equal("seeded-token", vm.YituliuOpenApiToken);
        Assert.True(vm.OperBoxUseYituliuApi);

        vm.YituliuOpenApiToken = "  updated-token  ";
        vm.OperBoxUseYituliuApi = false;

        var persisted = await WaitForAsync(() =>
            ReadProfileValue(config, ThirdPartySettingKeys.YituliuOpenApiToken) == "updated-token"
            && ReadProfileValue(config, ThirdPartySettingKeys.OperBoxUseYituliuApi) == "False");
        Assert.True(persisted, "Third-party OpenAPI settings should auto-save into the current profile.");

        // Reporting toggles keep their frozen v6.3.7 keys, so the third-party section can be
        // moved without a config migration.
        vm.EnablePenguin = false;
        vm.EnableYituliu = true;
        vm.PenguinId = "penguin-001";
        Assert.True(await WaitForAsync(() =>
            ReadProfileValue(config, "Penguin.EnablePenguin") == "False"
            && ReadProfileValue(config, "Yituliu.EnableYituliu") == "True"
            && ReadProfileValue(config, "Penguin.Id") == "penguin-001"));
    }

    [Fact]
    public void ThirdPartyView_ShouldOwnReportingAndOpenApiControls()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var thirdParty = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ThirdPartySettingsView.axaml"));
        var game = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GameSettingsView.axaml"));

        Assert.Contains("Settings.ThirdParty.ReportToPenguin", thirdParty, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding EnablePenguin}\"", thirdParty, StringComparison.Ordinal);
        Assert.Contains("Settings.ThirdParty.ReportToYituliu", thirdParty, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding EnableYituliu}\"", thirdParty, StringComparison.Ordinal);
        Assert.Contains("Settings.ThirdParty.PenguinId", thirdParty, StringComparison.Ordinal);
        Assert.Contains("Settings.ThirdParty.OpenApiToken", thirdParty, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding OperBoxUseYituliuApi}\"", thirdParty, StringComparison.Ordinal);

        // WPF 247db567f3 moved the reporting fields out of the game settings section.
        Assert.DoesNotContain("Binding EnablePenguin", game, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding PenguinId", game, StringComparison.Ordinal);
    }

    private static string ReadProfileValue(UnifiedConfigurationService config, string key)
    {
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        return profile.Values.TryGetValue(key, out var node) && node is not null
            ? node.ToString()
            : string.Empty;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(30);
        }

        return condition();
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(string root, MAAUnifiedRuntime runtime)
        {
            Root = root;
            Runtime = runtime;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-thirdparty-tests", Guid.NewGuid().ToString("N"));
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

            var bridge = new MaaCoreBridgeStub();
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
                ShellFeatureService = new ShellFeatureService(connect),
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

            return new RuntimeFixture(root, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }
}
