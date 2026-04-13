using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class SettingsUiAuditRegressionTests
{
    [Fact]
    public void BackgroundSettingsView_ShouldWireSelectionButtonThroughFilePickerAndGuiSave()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "BackgroundSettingsView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "BackgroundSettingsView.axaml.cs"));

        Assert.Contains("Click=\"OnSelectBackgroundImageClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenFilePickerAsync(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryGetLocalPath()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("vm.BackgroundImagePath = path;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await vm.SaveGuiSettingsAsync();", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HotKeyEditor_ShouldBeRemovedAndUnreferencedFromSettingsRoot()
    {
        var root = GetMaaUnifiedRoot();
        var editorXamlPath = Path.Combine(root, "App", "Features", "Settings", "HotKeyEditorView.axaml");
        var editorCodePath = Path.Combine(root, "App", "Features", "Settings", "HotKeyEditorView.axaml.cs");
        var settingsXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var settingsCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml.cs"));

        Assert.False(File.Exists(editorXamlPath));
        Assert.False(File.Exists(editorCodePath));

        Assert.DoesNotContain("SectionHotKeyEditor", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("settingsViews:HotKeyEditorView", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterSectionAnchor(\"HotKeyEditor\"", settingsCode, StringComparison.Ordinal);

        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.DoesNotContain(vm.Sections, section => string.Equals(section.Key, "HotKeyEditor", StringComparison.Ordinal));
        Assert.False(vm.SelectSection("HotKeyEditor"));
    }

    [Fact]
    public void VersionUpdateSettingsView_ShouldKeepStartupCheckBoundAndEditable()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "VersionUpdateSettingsView.axaml"));
        const string startupCheckBinding = "Content=\"{Binding RootTexts[Settings.VersionUpdate.StartupCheck]}\"";
        var startupCheckIndex = xaml.IndexOf(startupCheckBinding, StringComparison.Ordinal);

        Assert.True(startupCheckIndex >= 0, "Version update settings should bind the startup check option through RootTexts.");

        var startupCheckBlock = xaml.Substring(
            startupCheckIndex,
            Math.Min(160, xaml.Length - startupCheckIndex));

        Assert.DoesNotContain("启动时检查更新", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding VersionUpdateStartupCheck}\"", startupCheckBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=\"False\"", startupCheckBlock, StringComparison.Ordinal);
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly bool _cleanupRoot;

        private RuntimeFixture(string root, MAAUnifiedRuntime runtime, bool cleanupRoot)
        {
            Root = root;
            Runtime = runtime;
            _cleanupRoot = cleanupRoot;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

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

            return new RuntimeFixture(root, runtime, cleanupRoot);
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
}
