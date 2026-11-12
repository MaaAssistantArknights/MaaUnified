using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

/// <summary>
/// P1-11 / P1-35: settings-page section order and collapse persistence (WPF
/// <c>Gui.SettingOrders</c> / <c>Gui.CollapesStates</c>) plus the dev-v2 section reorg and the
/// global Performance section (WPF <c>0bc04350c0</c>, <c>f7f23f0386</c>, <c>ff2d6cd2e7</c>,
/// <c>b5412be3a1</c>, <c>f464016813</c>).
/// </summary>
public sealed class SettingsSectionLayoutFeatureTests
{
    private static readonly string[] ExpectedDefaultOrder =
    [
        "ConfigurationManager",
        "Timer",
        "Performance",
        "Game",
        "Connect",
        "Start",
        "RemoteControl",
        "GUI",
        "Notification",
        "ThirdParty",
        "HotKey",
        "Achievement",
        "VersionUpdate",
        "IssueReport",
        "About",
    ];

    [Fact]
    public void LayoutDefaults_ShouldMatchThePreExistingOrderWithoutMergedSections()
    {
        Assert.Equal(ExpectedDefaultOrder, SettingsSectionLayout.DefaultOrder);
        Assert.DoesNotContain("Background", SettingsSectionLayout.DefaultOrder);
        Assert.DoesNotContain("ExternalNotification", SettingsSectionLayout.DefaultOrder);
        Assert.Equal("NotificationSettings", SettingsSectionLayout.TryGetWpfKey("Notification"));
    }

    [Fact]
    public void NormalizeOrder_ShouldRenameMergedSectionAndDropRemovedOrUnknownEntries()
    {
        var node = new JsonArray(
            "BackgroundSettings",
            "ExternalNotificationSettings",
            "3",
            "UiSettings",
            "ExternalNotificationSettings",
            "NotASection");

        var order = SettingsSectionLayout.NormalizeOrder(node);

        Assert.Equal(ExpectedDefaultOrder.Length, order.Count);
        Assert.Equal("Notification", order[0]);
        Assert.Equal("GUI", order[1]);
        Assert.Equal(
            ExpectedDefaultOrder.OrderBy(static key => key, StringComparer.Ordinal),
            order.OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void NormalizeCollapsed_ShouldNotInheritStateForMergedSections()
    {
        var node = new JsonArray(
            "BackgroundSettings",
            "ExternalNotificationSettings",
            "NotificationSettings",
            "NotificationSettings",
            "GameSettings",
            "3",
            "Nope");

        var collapsed = SettingsSectionLayout.NormalizeCollapsed(node);

        Assert.Equal(["Notification", "Game"], collapsed);
    }

    [Fact]
    public void NormalizeGlobalLayout_ShouldMigrateLegacyExpanderBooleans()
    {
        var globals = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            // v6.3.7 persistExpanderXxx: true = expanded.
            ["GUI.ExpanderUiSettings"] = JsonValue.Create(false),
            ["GUI.ExpanderGameSettings"] = JsonValue.Create(true),
            ["GUI.ExpanderExternalNotificationSettings"] = JsonValue.Create(false),
            ["GUI.ExpanderBackgroundSettings"] = JsonValue.Create(false),
            ["GUI.ExpanderUnknownSettings"] = JsonValue.Create(false),
            ["GUI.Localization"] = JsonValue.Create("zh-cn"),
        };

        var changed = SettingsSectionLayout.NormalizeGlobalLayout(globals);

        Assert.True(changed);
        Assert.DoesNotContain(globals.Keys, key => key.StartsWith("GUI.Expander", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("zh-cn", globals["GUI.Localization"]?.GetValue<string>());

        // No persisted order means "use the default order"; the migration must not dirty the
        // config just to materialize the defaults.
        Assert.False(globals.ContainsKey(SettingsSectionLayout.SettingOrdersKey));

        // Only the explicitly collapsed, still-existing section survives; the merged external
        // notification section does not inherit its old collapse state.
        var collapsed = Assert.IsType<JsonArray>(globals[SettingsSectionLayout.CollapsedSectionsKey]);
        Assert.Equal(["UiSettings"], collapsed.Select(item => item?.GetValue<string>()));
    }

    [Fact]
    public void NormalizeGlobalLayout_ShouldBeIdempotent()
    {
        var globals = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GUI.ExpanderUiSettings"] = JsonValue.Create(false),
            ["GUI.ExpanderGameSettings"] = JsonValue.Create(true),
        };

        Assert.True(SettingsSectionLayout.NormalizeGlobalLayout(globals));
        Assert.False(SettingsSectionLayout.NormalizeGlobalLayout(globals));
    }

    [Fact]
    public async Task Load_ShouldMigrateLegacyExpanderBooleansIntoCollapsedSections()
    {
        var root = CreateTempRoot();
        await WriteConfigAsync(
            root,
            """
            {
              "SchemaVersion": 3,
              "CurrentProfile": "Default",
              "Profiles": { "Default": { "Values": {}, "TaskQueue": [] } },
              "GlobalValues": {
                "GUI.ExpanderSwitchConfiguration": false,
                "GUI.ExpanderGameSettings": true,
                "GUI.ExpanderExternalNotificationSettings": false,
                "GUI.SettingOrders": [ "GameSettings", "BackgroundSettings", "UiSettings" ]
              },
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var globals = service.CurrentConfig.GlobalValues;
        Assert.DoesNotContain(globals.Keys, key => key.StartsWith("GUI.Expander", StringComparison.OrdinalIgnoreCase));
        var order = Assert.IsType<JsonArray>(globals[SettingsSectionLayout.SettingOrdersKey]);
        Assert.Equal("GameSettings", order[0]?.GetValue<string>());
        Assert.Equal("UiSettings", order[1]?.GetValue<string>());
        Assert.DoesNotContain("BackgroundSettings", order.Select(item => item?.GetValue<string>()));
        var collapsed = Assert.IsType<JsonArray>(globals[SettingsSectionLayout.CollapsedSectionsKey]);
        Assert.Equal(["SwitchConfiguration"], collapsed.Select(item => item?.GetValue<string>()));

        // The migration is persisted so the same legacy keys are not rediscovered on every start.
        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        var persistedGlobals = Assert.IsType<JsonObject>(persisted["GlobalValues"]);
        Assert.DoesNotContain(
            persistedGlobals.Select(property => property.Key),
            key => key.StartsWith("GUI.Expander", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LegacyGuiNewImport_ShouldMigrateExpanderBooleansIntoCollapsedSections()
    {
        var root = CreateTempRoot();
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": { "Default": {} },
              "GUI": {
                "Localization": "zh-cn",
                "ExpanderUiSettings": false,
                "ExpanderGameSettings": true,
                "ExpanderExternalNotificationSettings": false
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var globals = service.CurrentConfig.GlobalValues;
        Assert.DoesNotContain(globals.Keys, key => key.StartsWith("GUI.Expander", StringComparison.OrdinalIgnoreCase));
        var collapsed = Assert.IsType<JsonArray>(globals[SettingsSectionLayout.CollapsedSectionsKey]);
        Assert.Equal(["UiSettings"], collapsed.Select(item => item?.GetValue<string>()));
    }

    [Fact]
    public async Task GuiNewImport_ShouldPersistSectionOrderAndCollapseStateFromDevV2()
    {
        var root = CreateTempRoot();
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": { "Default": {} },
              "Gui": {
                "Localization": "en-us",
                "SettingOrders": [ "UiSettings", "ExternalNotificationSettings", "BackgroundSettings", "AboutUs", "Nope" ],
                "CollapesStates": [ "NotificationSettings", "BackgroundSettings", "ExternalNotificationSettings", "Bogus" ]
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var globals = service.CurrentConfig.GlobalValues;
        var order = Assert.IsType<JsonArray>(globals[SettingsSectionLayout.SettingOrdersKey]);
        var orderNames = order.Select(item => item?.GetValue<string>()).ToArray();
        Assert.Equal("UiSettings", orderNames[0]);
        Assert.Equal("NotificationSettings", orderNames[1]);
        Assert.Equal("AboutUs", orderNames[2]);
        Assert.Equal(ExpectedDefaultOrder.Length, orderNames.Length);
        Assert.DoesNotContain("BackgroundSettings", orderNames);
        Assert.DoesNotContain("Nope", orderNames);

        var collapsed = Assert.IsType<JsonArray>(globals[SettingsSectionLayout.CollapsedSectionsKey]);
        Assert.Equal(["NotificationSettings"], collapsed.Select(item => item?.GetValue<string>()));
        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("SettingOrders", StringComparison.Ordinal)
                       || warning.Contains("CollapesStates", StringComparison.Ordinal));

        // Round-trip: a second load must not change the normalized layout again.
        var reloaded = CreateService(root);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal(
            orderNames,
            Assert.IsType<JsonArray>(reloaded.CurrentConfig.GlobalValues[SettingsSectionLayout.SettingOrdersKey])
                .Select(item => item?.GetValue<string>()));
    }

    [Fact]
    public async Task Load_ShouldMoveProfileScopedPerformanceIntoGlobalConfiguration()
    {
        var root = CreateTempRoot();
        await WriteConfigAsync(
            root,
            """
            {
              "SchemaVersion": 3,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {
                    "Performance.UseGpu": "true",
                    "Performance.AllowDeprecatedGpu": 1,
                    "Performance.PreferredGpuDescription": "GPU A",
                    "Performance.PreferredGpuInstancePath": "pci:0",
                    "RunDirectly": true
                  },
                  "TaskQueue": []
                },
                "Alt": {
                  "Values": { "Performance.UseGpu": false },
                  "TaskQueue": []
                }
              },
              "GlobalValues": { "GUI.IgnoreBadModulesAndUseSoftwareRendering": "true" },
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var config = service.CurrentConfig;
        Assert.True(config.GlobalValues[ConfigurationKeys.PerformanceUseGpu]?.GetValue<bool>());
        Assert.True(config.GlobalValues[ConfigurationKeys.PerformanceAllowDeprecatedGpu]?.GetValue<bool>());
        Assert.Equal("GPU A", config.GlobalValues[ConfigurationKeys.PerformancePreferredGpuDescription]?.GetValue<string>());
        Assert.Equal("pci:0", config.GlobalValues[ConfigurationKeys.PerformancePreferredGpuInstancePath]?.GetValue<string>());
        Assert.True(config.GlobalValues[ConfigurationKeys.PerformanceIgnoreBadModulesAndUseSoftwareRendering]?.GetValue<bool>());
        Assert.False(config.GlobalValues.ContainsKey(ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering));

        foreach (var profile in config.Profiles.Values)
        {
            Assert.DoesNotContain(ConfigurationKeys.PerformanceUseGpu, profile.Values.Keys);
            Assert.DoesNotContain(ConfigurationKeys.PerformanceAllowDeprecatedGpu, profile.Values.Keys);
            Assert.DoesNotContain(ConfigurationKeys.PerformancePreferredGpuDescription, profile.Values.Keys);
            Assert.DoesNotContain(ConfigurationKeys.PerformancePreferredGpuInstancePath, profile.Values.Keys);
        }

        // Non-performance profile settings survive the migration.
        Assert.True(config.Profiles["Default"].Values["RunDirectly"]?.GetValue<bool>());

        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        var persistedGlobals = Assert.IsType<JsonObject>(persisted["GlobalValues"]);
        Assert.True(persistedGlobals[ConfigurationKeys.PerformanceUseGpu]?.GetValue<bool>());
        Assert.False(persistedGlobals.ContainsKey(ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering));
        var persistedProfileValues = Assert.IsType<JsonObject>(persisted["Profiles"]?["Default"]?["Values"]);
        Assert.False(persistedProfileValues.ContainsKey(ConfigurationKeys.PerformanceUseGpu));
    }

    [Fact]
    public async Task ViewModel_WithoutLayoutKeys_ShouldUseDefaultOrderAndNoCollapsedSections()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal(ExpectedDefaultOrder, vm.Sections.Select(section => section.Key));
        Assert.All(vm.Sections, section => Assert.False(section.IsCollapsed));
        Assert.Equal(ExpectedDefaultOrder[0], vm.SelectedSection?.Key);
    }

    [Fact]
    public async Task ViewModel_ApplySectionOrder_ShouldRebuildSectionsImmediatelyAndPersistWpfNames()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var applied = await vm.ApplySectionOrderAsync(["About", "GUI", "Timer"]);

        Assert.True(applied);
        Assert.Equal(["About", "GUI", "Timer"], vm.Sections.Take(3).Select(section => section.Key));
        Assert.Equal(ExpectedDefaultOrder.Length, vm.Sections.Count);

        var stored = Assert.IsType<JsonArray>(
            fixture.Config.CurrentConfig.GlobalValues[SettingsSectionLayout.SettingOrdersKey]);
        Assert.Equal("AboutUs", stored[0]?.GetValue<string>());
        Assert.Equal("UiSettings", stored[1]?.GetValue<string>());
        Assert.Equal("ScheduleSettings", stored[2]?.GetValue<string>());
        Assert.Equal(ExpectedDefaultOrder.Length, stored.Count);

        // A reload reads the persisted order back (persistence round-trip).
        vm.ReloadSectionLayout();
        Assert.Equal(["About", "GUI", "Timer"], vm.Sections.Take(3).Select(section => section.Key));
    }

    [Fact]
    public async Task ViewModel_CollapseToggle_ShouldPersistAndRestoreWpfCollapsedNames()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.ToggleSectionCollapsedAsync("Notification");

        Assert.True(vm.IsSectionCollapsed("Notification"));
        Assert.True(vm.Sections.Single(section => section.Key == "Notification").IsCollapsed);
        var stored = Assert.IsType<JsonArray>(
            fixture.Config.CurrentConfig.GlobalValues[SettingsSectionLayout.CollapsedSectionsKey]);
        Assert.Equal(["NotificationSettings"], stored.Select(item => item?.GetValue<string>()));

        vm.ReloadSectionLayout();
        Assert.True(vm.IsSectionCollapsed("Notification"));

        await vm.ToggleSectionCollapsedAsync("Notification");
        Assert.False(vm.IsSectionCollapsed("Notification"));
        Assert.False(
            fixture.Config.CurrentConfig.GlobalValues.ContainsKey(SettingsSectionLayout.CollapsedSectionsKey));
    }

    [Fact]
    public async Task ViewModel_ShouldReadWpfOrderAndCollapseAndDropRemovedSections()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[SettingsSectionLayout.SettingOrdersKey] = new JsonArray(
            "BackgroundSettings",
            "ExternalNotificationSettings",
            "BogusSection",
            "UiSettings");
        fixture.Config.CurrentConfig.GlobalValues[SettingsSectionLayout.CollapsedSectionsKey] = new JsonArray(
            "BackgroundSettings",
            "ExternalNotificationSettings",
            "NotificationSettings",
            "3");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal("Notification", vm.Sections[0].Key);
        Assert.Equal("GUI", vm.Sections[1].Key);
        Assert.Equal(ExpectedDefaultOrder.Length, vm.Sections.Count);
        Assert.True(vm.IsSectionCollapsed("Notification"));
        Assert.Equal(["Notification"], vm.Sections.Where(section => section.IsCollapsed).Select(section => section.Key));
    }

    [Fact]
    public async Task ViewModel_PerformanceSection_ShouldReadAndWriteGlobalPerformanceKeys()
    {
        // Force the Windows-supported GPU mode so the performance section keeps GPU settings
        // regardless of the host platform (macOS/Linux use UnsupportedGpuCapabilityService).
        await using var fixture = await RuntimeFixture.CreateAsync(new SupportedGpuCapabilityService());
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.Values[ConfigurationKeys.PerformanceUseGpu] = JsonValue.Create(true);
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // Global-first read: the legacy profile-scoped value is still honored.
        Assert.True(vm.PerformanceUseGpu);

        vm.SelectedGpuOptionId = "disabled";
        vm.UseSoftwareRendering = true;
        await vm.SaveStartPerformanceSettingsAsync();

        var globals = fixture.Config.CurrentConfig.GlobalValues;
        // Settings saves persist scalar values as strings; compare the serialized payload.
        Assert.Equal("False", globals[ConfigurationKeys.PerformanceUseGpu]?.ToString());
        Assert.Equal(
            "True",
            globals[ConfigurationKeys.PerformanceIgnoreBadModulesAndUseSoftwareRendering]?.ToString());
        Assert.DoesNotContain(ConfigurationKeys.PerformanceUseGpu, profile.Values.Keys);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static async Task WriteConfigAsync(string root, string json)
        => await File.WriteAllTextAsync(Path.Combine(root, "config", "avalonia.json"), json);

    private static UnifiedConfigurationService CreateService(string root)
    {
        var log = new UiLogService();
        return new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root,
            SensitiveSettingsService.CreatePersistentFileForTests(root));
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(string root, MAAUnifiedRuntime runtime, UnifiedConfigurationService config)
        {
            Root = root;
            Runtime = runtime;
            Config = config;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public UnifiedConfigurationService Config { get; }

        public static async Task<RuntimeFixture> CreateAsync(IGpuCapabilityService? gpuCapabilityService = null)
        {
            var root = CreateTempRoot();
            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = CreateService(root);
            await config.LoadOrBootstrapAsync();

            var bridge = new NullBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            gpuCapabilityService ??= new UnsupportedGpuCapabilityService();
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                GpuCapabilityService = gpuCapabilityService,
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
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log, gpuCapabilityService),
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
                VersionUpdateFeatureService = new VersionUpdateFeatureService(config, runtimeBaseDirectory: root),
                UiLanguageCoordinator = new UiLanguageCoordinator(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new RuntimeFixture(root, runtime, config);
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
                // Best-effort cleanup of the temporary test directory.
            }
        }
    }

    private sealed class SupportedGpuCapabilityService : IGpuCapabilityService
    {
        public GpuSelectionResolution Resolve(GpuPreference preference)
        {
            var systemDefault = GpuOptionDescriptor.SystemDefault("test GPU");
            return new GpuSelectionResolution(
                Snapshot: new GpuCapabilitySnapshot(
                    SupportMode: GpuPlatformSupportMode.WindowsSupported,
                    IsEditable: true,
                    AppliesToCore: true,
                    SupportsDeprecatedToggle: true,
                    Options: [GpuOptionDescriptor.Disabled, systemDefault],
                    StatusTextKey: "Settings.Performance.Gpu.Status.Supported",
                    Provider: "test"),
                SelectedOption: preference.UseGpu ? systemDefault : GpuOptionDescriptor.Disabled);
        }
    }

    private sealed class NullBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbacks = Channel.CreateUnbounded<CoreCallbackEvent>();

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(
                new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(
            CoreConnectionInfo connectionInfo,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(
            CoreAttachWindowRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbacks.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _callbacks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
