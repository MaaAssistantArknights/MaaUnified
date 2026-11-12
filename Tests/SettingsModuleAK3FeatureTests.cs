using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class SettingsModuleAK3FeatureTests
{
    [Fact]
    public async Task Timer_DefaultSlotHours_MatchWpfSlotIndexTimesThree()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal(8, vm.Timers.Count);
        for (var i = 0; i < vm.Timers.Count; i++)
        {
            var expectedTime = FormattableString.Invariant($"{i * 3:00}:00");
            Assert.Equal(expectedTime, vm.Timers[i].Time);
            Assert.Equal(i * 3, vm.Timers[i].Hour);
            Assert.Equal(0, vm.Timers[i].Minute);
        }
    }

    [Fact]
    public async Task Timer_SaveAndReload_RoundTrip8Slots()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var first = await RuntimeFixture.CreateAsync(root, cleanupRoot: false))
            {
                first.Config.CurrentConfig.Profiles["Alpha"] = new UnifiedProfile();
                first.Config.CurrentConfig.Profiles["Beta"] = new UnifiedProfile();
                await first.Config.SaveAsync();

                var vm = new SettingsPageViewModel(first.Runtime, new ConnectionGameSharedStateViewModel());
                await vm.InitializeAsync();

                vm.ForceScheduledStart = true;
                vm.ShowWindowBeforeForceScheduledStart = false;
                vm.CustomTimerConfig = true;
                for (var i = 0; i < vm.Timers.Count; i++)
                {
                    var slot = vm.Timers[i];
                    slot.Enabled = i % 2 == 0;
                    slot.Time = FormattableString.Invariant($"{i + 1:00}:{(i * 7) % 60:00}");
                    slot.Profile = i % 2 == 0 ? "Alpha" : "Beta";
                }

                await vm.SaveTimerSettingsAsync();

                Assert.False(vm.HasPendingTimerChanges);
                Assert.Equal("True", ReadGlobalString(first.Config, LegacyConfigurationKeys.ForceScheduledStart));
                Assert.Equal("False", ReadGlobalString(first.Config, LegacyConfigurationKeys.ShowWindowBeforeForceScheduledStart));
                Assert.Equal("True", ReadGlobalString(first.Config, LegacyConfigurationKeys.CustomConfig));

                for (var index = 1; index <= 8; index++)
                {
                    Assert.Equal((index % 2 == 1).ToString(), ReadGlobalString(first.Config, TimerEnabledKey(index)));
                    Assert.Equal((index).ToString(), ReadGlobalString(first.Config, TimerHourKey(index)));
                    Assert.Equal((((index - 1) * 7) % 60).ToString(), ReadGlobalString(first.Config, TimerMinuteKey(index)));
                    Assert.Equal(index % 2 == 1 ? "Alpha" : "Beta", ReadGlobalString(first.Config, TimerProfileKey(index)));
                }
            }

            await using var second = await RuntimeFixture.CreateAsync(root, cleanupRoot: false);
            var reloaded = new SettingsPageViewModel(second.Runtime, new ConnectionGameSharedStateViewModel());
            await reloaded.InitializeAsync();

            Assert.True(reloaded.ForceScheduledStart);
            Assert.False(reloaded.ShowWindowBeforeForceScheduledStart);
            Assert.True(reloaded.CustomTimerConfig);
            for (var i = 0; i < reloaded.Timers.Count; i++)
            {
                var slot = reloaded.Timers[i];
                Assert.Equal(i % 2 == 0, slot.Enabled);
                Assert.Equal(FormattableString.Invariant($"{i + 1:00}:{(i * 7) % 60:00}"), slot.Time);
                Assert.Equal(i % 2 == 0 ? "Alpha" : "Beta", slot.Profile);
            }
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
    public async Task Timer_SaveInvalidTime_BlocksAndKeepsOldConfig()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.Timers[0].Enabled = true;
        vm.Timers[0].Time = "08:30";
        vm.Timers[0].Profile = "Default";
        await vm.SaveTimerSettingsAsync();

        var oldHour = ReadGlobalString(fixture.Config, TimerHourKey(1));
        var oldMinute = ReadGlobalString(fixture.Config, TimerMinuteKey(1));

        vm.Timers[0].Time = "8:30";
        await vm.SaveTimerSettingsAsync();

        Assert.Equal(oldHour, ReadGlobalString(fixture.Config, TimerHourKey(1)));
        Assert.Equal(oldMinute, ReadGlobalString(fixture.Config, TimerMinuteKey(1)));
        Assert.True(vm.HasPendingTimerChanges);
        Assert.Contains("定时任务 1", vm.TimerValidationMessage, StringComparison.Ordinal);
        Assert.Contains("HH:mm", vm.TimerValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_SaveInvalidProfileWhenCustomConfig_Blocks()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.CustomTimerConfig = true;
        vm.Timers[0].Enabled = true;
        vm.Timers[0].Time = "09:45";
        vm.Timers[0].Profile = "Default";
        await vm.SaveTimerSettingsAsync();
        var oldProfile = ReadGlobalString(fixture.Config, TimerProfileKey(1));

        vm.Timers[0].Profile = "MissingProfile";
        await vm.SaveTimerSettingsAsync();

        Assert.Equal(oldProfile, ReadGlobalString(fixture.Config, TimerProfileKey(1)));
        Assert.True(vm.HasPendingTimerChanges);
        Assert.Contains("定时任务 1", vm.TimerValidationMessage, StringComparison.Ordinal);
        Assert.Contains("不存在", vm.TimerValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_SaveDisabledSlotWithoutProfileWhenCustomConfig_AllowsSave()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.CustomTimerConfig = true;
        vm.Timers[3].Enabled = false;
        vm.Timers[3].Time = "10:15";
        vm.Timers[3].Profile = string.Empty;

        await vm.SaveTimerSettingsAsync();

        Assert.False(vm.HasPendingTimerChanges, vm.TimerValidationMessage);
        Assert.Equal("False", ReadGlobalString(fixture.Config, TimerEnabledKey(4)));
        Assert.Equal(string.Empty, ReadGlobalString(fixture.Config, TimerProfileKey(4)));
    }

    [Fact]
    public async Task Timer_SaveOneShotNullSlot_RoundTripsWpfRightClickState()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.CustomTimerConfig = true;
        vm.Timers[0].Enabled = null;
        vm.Timers[0].Time = "11:20";
        vm.Timers[0].Profile = "Default";

        await vm.SaveTimerSettingsAsync();

        Assert.False(vm.HasPendingTimerChanges, vm.TimerValidationMessage);
        Assert.Equal(string.Empty, ReadGlobalString(fixture.Config, TimerEnabledKey(1)));

        var reloaded = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await reloaded.InitializeAsync();

        Assert.Null(reloaded.Timers[0].Enabled);
        Assert.True(reloaded.Timers[0].IsEffectivelyEnabled);
        Assert.Equal("11:20", reloaded.Timers[0].Time);
        Assert.Equal("Default", reloaded.Timers[0].Profile);
    }

    [Fact]
    public async Task Timer_ViewCompositionTransientEmptyProfile_IsRepairedBeforeAutoSaveResumes()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Alpha"] = new UnifiedProfile();
        fixture.Config.CurrentConfig.Profiles["Beta"] = new UnifiedProfile();
        fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.CustomConfig] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[TimerEnabledKey(1)] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[TimerHourKey(1)] = JsonValue.Create(7);
        fixture.Config.CurrentConfig.GlobalValues[TimerMinuteKey(1)] = JsonValue.Create(30);
        fixture.Config.CurrentConfig.GlobalValues[TimerProfileKey(1)] = JsonValue.Create("Alpha");
        fixture.Config.CurrentConfig.GlobalValues[TimerEnabledKey(3)] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[TimerHourKey(3)] = JsonValue.Create(8);
        fixture.Config.CurrentConfig.GlobalValues[TimerMinuteKey(3)] = JsonValue.Create(45);
        fixture.Config.CurrentConfig.GlobalValues[TimerProfileKey(3)] = JsonValue.Create("Beta");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.BeginViewComposition();
        vm.Timers[0].Profile = string.Empty;
        vm.Timers[2].Profile = string.Empty;

        Assert.False(vm.HasPendingTimerChanges);
        Assert.Equal("Alpha", vm.Timers[0].Profile);
        Assert.Equal("Beta", vm.Timers[2].Profile);

        vm.EndViewComposition();

        Assert.False(vm.HasPendingTimerChanges, vm.TimerValidationMessage);
        Assert.Equal("Alpha", vm.Timers[0].Profile);
        Assert.Equal("Beta", vm.Timers[2].Profile);

        var errorLog = File.Exists(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            ? await File.ReadAllTextAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            : string.Empty;
        Assert.DoesNotContain(UiErrorCode.TimerProfileMissing, errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_TransientEmptyProfileAfterInitialization_IsImmediatelyRestored()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Alpha"] = new UnifiedProfile();
        fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.CustomConfig] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[TimerEnabledKey(1)] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[TimerHourKey(1)] = JsonValue.Create(7);
        fixture.Config.CurrentConfig.GlobalValues[TimerMinuteKey(1)] = JsonValue.Create(30);
        fixture.Config.CurrentConfig.GlobalValues[TimerProfileKey(1)] = JsonValue.Create("Alpha");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.Timers[0].Profile = string.Empty;

        Assert.False(vm.HasPendingTimerChanges, vm.TimerValidationMessage);
        Assert.Equal("Alpha", vm.Timers[0].Profile);

        var errorLog = File.Exists(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            ? await File.ReadAllTextAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            : string.Empty;
        Assert.DoesNotContain(UiErrorCode.TimerProfileMissing, errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_LoadLegacyKeys_NormalizesAndWarns()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.ForceScheduledStart] = JsonValue.Create(1);
        fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.ShowWindowBeforeForceScheduledStart] = JsonValue.Create("0");
        fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.CustomConfig] = JsonValue.Create("1");
        fixture.Config.CurrentConfig.GlobalValues[TimerEnabledKey(1)] = JsonValue.Create("true");
        fixture.Config.CurrentConfig.GlobalValues[TimerHourKey(1)] = JsonValue.Create("99");
        fixture.Config.CurrentConfig.GlobalValues[TimerMinuteKey(1)] = JsonValue.Create("-3");
        fixture.Config.CurrentConfig.GlobalValues[TimerProfileKey(1)] = JsonValue.Create("UnknownProfile");

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(vm.ForceScheduledStart);
        Assert.False(vm.ShowWindowBeforeForceScheduledStart);
        Assert.True(vm.CustomTimerConfig);
        Assert.True(vm.Timers[0].Enabled);
        Assert.Equal("23:00", vm.Timers[0].Time);
        Assert.Equal("Default", vm.Timers[0].Profile);
        Assert.Contains("限制为", vm.TimerValidationMessage, StringComparison.Ordinal);
        Assert.Contains("回退", vm.TimerValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_NotificationMinutes_ClampAndPersist()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.ScheduledStartNotificationMinutes = 0;
        Assert.Equal(1, vm.ScheduledStartNotificationMinutes);
        vm.ScheduledStartNotificationMinutes = 5000;
        Assert.Equal(1439, vm.ScheduledStartNotificationMinutes);
        vm.ScheduledStartNotificationMinutes = 30;
        vm.NotifyBeforeScheduledStart = true;
        await vm.SaveTimerSettingsAsync();

        Assert.False(vm.HasPendingTimerChanges, vm.TimerValidationMessage);
        Assert.Equal("30", ReadGlobalString(fixture.Config, LegacyConfigurationKeys.ScheduledStartNotificationMinutes));
        Assert.Equal("True", ReadGlobalString(fixture.Config, LegacyConfigurationKeys.NotifyBeforeScheduledStart));
        Assert.Equal("False", ReadGlobalString(fixture.Config, LegacyConfigurationKeys.ScheduledWakeUp));

        // A value persisted by another build is clamped on load and reported in the page warning.
        fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.ScheduledStartNotificationMinutes] =
            JsonValue.Create(5000);
        await fixture.Config.SaveAsync();
        var reloaded = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await reloaded.InitializeAsync();

        Assert.Equal(1439, reloaded.ScheduledStartNotificationMinutes);
        Assert.Contains("限制为", reloaded.TimerValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_ScheduledWakeUp_PlanReflectsSlotsAndCapability()
    {
        var registrar = new RecordingScheduledWakeUpRegistrar();
        await using var fixture = await RuntimeFixture.CreateAsync(scheduledWakeUpRegistrar: registrar);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(vm.IsScheduledWakeUpSupported);
        vm.Timers[0].Enabled = true;
        vm.Timers[0].Time = "07:05";
        vm.Timers[1].Enabled = false;
        vm.ScheduledWakeUp = true;

        var plan = vm.BuildScheduledWakeUpPlan();
        Assert.True(plan.Enabled);
        var slot = Assert.Single(plan.Slots);
        Assert.Equal(1, slot.Index);
        Assert.Equal(7, slot.Hour);
        Assert.Equal(5, slot.Minute);
        Assert.False(string.IsNullOrWhiteSpace(plan.ExecutablePath));
        Assert.Contains("--skip-startup-auto-run", plan.Arguments, StringComparison.Ordinal);

        await vm.PendingScheduledWakeUpSync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(registrar.Calls);

        // The in-app timer list change is debounced into one more rebuild (WPF TimerList change).
        vm.Timers[0].Time = "08:30";
        await vm.PendingScheduledWakeUpSync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, registrar.Calls.Count);
        Assert.Equal(8, registrar.Calls[^1].Slots[0].Hour);
        Assert.Equal(30, registrar.Calls[^1].Slots[0].Minute);
    }

    [Fact]
    public async Task Timer_ScheduledWakeUp_BackgroundReconcileRegistersPersistedPlan()
    {
        var registrar = new RecordingScheduledWakeUpRegistrar();
        await using var fixture = await RuntimeFixture.CreateAsync(scheduledWakeUpRegistrar: registrar);
        fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.ScheduledWakeUp] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[TimerEnabledKey(1)] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[TimerHourKey(1)] = JsonValue.Create(6);
        fixture.Config.CurrentConfig.GlobalValues[TimerMinuteKey(1)] = JsonValue.Create(15);
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.True(vm.ScheduledWakeUp);
        Assert.Empty(registrar.Calls);

        vm.ReconcileScheduledWakeUpInBackground();
        await vm.PendingScheduledWakeUpSync.WaitAsync(TimeSpan.FromSeconds(5));

        var plan = Assert.Single(registrar.Calls);
        var slot = Assert.Single(plan.Slots);
        Assert.Equal(6, slot.Hour);
        Assert.Equal(15, slot.Minute);
    }

    [Fact]
    public async Task Timer_ScheduledWakeUp_UnsupportedPlatformDisablesSwitch()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.False(vm.IsScheduledWakeUpSupported);
        Assert.True(vm.IsScheduledWakeUpUnsupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.ScheduledWakeUpCapabilityText));

        vm.ScheduledWakeUp = true;
        var plan = vm.BuildScheduledWakeUpPlan();
        Assert.False(plan.Enabled);
        Assert.Empty(plan.Slots);
    }

    private static string TimerEnabledKey(int index) => $"Timer.Timer{index}";

    private static string TimerHourKey(int index) => $"Timer.Timer{index}Hour";

    private static string TimerMinuteKey(int index) => $"Timer.Timer{index}Min";

    private static string TimerProfileKey(int index) => $"Timer.Timer{index}.Config";

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

        public static async Task<RuntimeFixture> CreateAsync(
            string? root = null,
            bool cleanupRoot = true,
            IScheduledWakeUpRegistrar? scheduledWakeUpRegistrar = null)
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
                ScheduledWakeUpRegistrar = scheduledWakeUpRegistrar ?? new UnsupportedScheduledWakeUpRegistrar(),
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

    private sealed class RecordingScheduledWakeUpRegistrar : MAAUnified.Platform.IScheduledWakeUpRegistrar
    {
        public MAAUnified.Platform.PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "test registrar",
            Provider: "test");

        public List<MAAUnified.Platform.ScheduledWakeUpPlan> Calls { get; } = [];

        public Task<MAAUnified.Platform.PlatformOperationResult> SyncAsync(
            MAAUnified.Platform.ScheduledWakeUpPlan plan,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(plan);
            return Task.FromResult(MAAUnified.Platform.PlatformOperation.NativeSuccess(
                "test",
                "synced",
                "scheduledWakeUp.sync"));
        }
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

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
