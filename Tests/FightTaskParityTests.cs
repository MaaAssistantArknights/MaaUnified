using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class FightTaskParityTests
{
    private static readonly IReadOnlyList<(string Stage, IReadOnlySet<DayOfWeek> OpenDays)> WeeklyStageFixtures =
    [
        ("CE-6", new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday }),
        ("AP-5", new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday }),
        ("CA-5", new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Sunday }),
        ("SK-5", new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Saturday }),
    ];

    [Fact]
    public async Task SaveFightParams_ShouldRoundTripStagePlanAndTriStateParityFields()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight")).Success);

        var save = await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "LS-6",
            StagePlan = ["LS-6", "CE-6"],
            IsStageManually = true,
            UseMedicine = null,
            Medicine = 5,
            UseStone = null,
            Stone = 2,
            EnableTimesLimit = null,
            Times = 7,
            Series = 1,
            EnableTargetDrop = null,
            DropId = "30012",
            DropCount = 3,
            UseAlternateStage = true,
            HideUnavailableStage = true,
            StageResetMode = "Current",
            UseWeeklySchedule = true,
            WeeklyScheduleMonday = false,
        });

        Assert.True(save.Success);

        var raw = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("LS-6", raw["stage"]?.GetValue<string>());
        Assert.True(raw["_ui_is_stage_manually"]?.GetValue<bool>());
        Assert.True(raw["_ui_use_alternate_stage"]?.GetValue<bool>());
        Assert.False(raw["_ui_hide_unavailable_stage"]?.GetValue<bool>());
        Assert.Equal("Ignore", raw["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.Null(raw["_ui_use_medicine"]);
        Assert.Null(raw["_ui_use_stone"]);
        Assert.Null(raw["_ui_enable_times_limit"]);
        Assert.Null(raw["_ui_enable_target_drop"]);

        var stagePlan = Assert.IsType<JsonArray>(raw["_ui_stage_plan"]);
        Assert.Equal(["LS-6", "CE-6"], stagePlan.Select(node => node!.GetValue<string>()).ToArray());

        var roundTrip = await fixture.TaskQueue.GetFightParamsAsync(0);
        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.Value);
        Assert.Equal(["LS-6", "CE-6"], roundTrip.Value!.StagePlan);
        Assert.True(roundTrip.Value.IsStageManually);
        Assert.Null(roundTrip.Value.UseMedicine);
        Assert.Null(roundTrip.Value.UseStone);
        Assert.Null(roundTrip.Value.EnableTimesLimit);
        Assert.Null(roundTrip.Value.EnableTargetDrop);
        Assert.True(roundTrip.Value.UseAlternateStage);
        Assert.False(roundTrip.Value.HideUnavailableStage);
        Assert.Equal("Ignore", roundTrip.Value.StageResetMode);
        Assert.True(roundTrip.Value.UseWeeklySchedule);
        Assert.False(roundTrip.Value.WeeklyScheduleMonday);
    }

    [Fact]
    public async Task GuiNewImport_FightStagePlanAndTriStateFields_ShouldPreserveParityMetadata()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "UseMedicine": null,
                      "UseStone": null,
                      "EnableTimesLimit": null,
                      "EnableTargetDrop": null,
                      "IsStageManually": true,
                      "UseOptionalStage": true,
                      "HideUnavailableStage": true,
                      "StageResetMode": 1,
                      "UseWeeklySchedule": true,
                      "WeeklySchedule": {
                        "Monday": false,
                        "Sunday": true
                      },
                      "StagePlan": ["", "LS-6", "CE-6"]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);

        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.Equal("Fight", task.Type);
        Assert.Equal("LS-6", task.Params["stage"]?.GetValue<string>());
        Assert.True(task.Params["_ui_is_stage_manually"]?.GetValue<bool>());
        Assert.True(task.Params["_ui_use_alternate_stage"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_hide_unavailable_stage"]?.GetValue<bool>());
        Assert.Equal("Ignore", task.Params["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.True(task.Params["_ui_use_weekly_schedule"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_weekly_schedule_monday"]?.GetValue<bool>());
        Assert.Null(task.Params["_ui_use_medicine"]);
        Assert.Null(task.Params["_ui_use_stone"]);
        Assert.Null(task.Params["_ui_enable_times_limit"]);
        Assert.Null(task.Params["_ui_enable_target_drop"]);

        var stagePlan = Assert.IsType<JsonArray>(task.Params["_ui_stage_plan"]);
        Assert.Equal(
            [FightStageSelection.CurrentOrLast, "LS-6", "CE-6"],
            stagePlan.Select(node => node!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task FightModule_AutoRestartOnDrop_ShouldReloadAndPersistSharedSetting()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.AutoRestartOnDrop] = JsonValue.Create(false);

        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await module.ReloadPersistentConfigAsync();

        Assert.False(module.AutoRestartOnDrop);

        module.AutoRestartOnDrop = true;

        Assert.True(module.AutoRestartOnDrop);
        Assert.True(fixture.Config.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.AutoRestartOnDrop]?.GetValue<bool>());
    }

    [Fact]
    public async Task FightModule_StageOptions_ShouldUseWpfCuratedStageList()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        module.HideUnavailableStage = false;

        var values = module.StageOptions.Select(option => option.Value).ToArray();

        Assert.Contains(FightStageSelection.CurrentOrLast, values);
        Assert.Contains("1-7", values);
        Assert.Contains("Annihilation", values);
        Assert.Contains("CE-6", values);
        Assert.DoesNotContain("GT-1", values);
        Assert.InRange(values.Length, 1, 40);
    }

    [Fact]
    public async Task FightModule_ClosedStageOptions_ShouldBeSelectableWhenVisibleAndHiddenWhenRequested()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        var closedStage = ResolveClosedWeeklyStage();

        module.HideUnavailableStage = false;

        var visibleOption = Assert.Single(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
        Assert.False(visibleOption.IsOpen);
        Assert.False(visibleOption.IsOutdated);
        Assert.Equal(closedStage, visibleOption.DisplayName);
        Assert.DoesNotContain("(Closed)", visibleOption.DisplayName, StringComparison.OrdinalIgnoreCase);

        module.SelectedStageOption = visibleOption;

        Assert.Equal(closedStage, module.Stage);

        module.HideUnavailableStage = true;

        Assert.DoesNotContain(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FightModule_SelectedClosedStage_ShouldBePreservedInternallyWhenHidden()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        var closedStage = ResolveClosedWeeklyStage();

        module.HideUnavailableStage = false;
        module.Stage = closedStage;
        module.HideUnavailableStage = true;

        var entry = Assert.Single(module.StagePlan);
        Assert.Equal(closedStage, module.Stage);
        Assert.Equal(closedStage, entry.Stage);
        Assert.Null(module.SelectedStageOption);
        Assert.True(entry.IsClosed);
        Assert.False(entry.IsOutdated);
        Assert.Equal(string.Empty, entry.StatusText);
        Assert.DoesNotContain(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FightModule_OutdatedSelectedStage_ShouldBePreservedInternallyButHiddenFromOptions()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        const string outdatedStage = "EXPIRED-STAGE-FOR-PARITY";

        module.HideUnavailableStage = false;
        module.Stage = outdatedStage;
        module.RefreshStageOptions();

        var entry = Assert.Single(module.StagePlan);
        Assert.Equal(outdatedStage, module.Stage);
        Assert.Equal(outdatedStage, entry.Stage);
        Assert.Null(module.SelectedStageOption);
        Assert.True(entry.IsOutdated);
        Assert.Equal("(Outdated)", entry.StatusText);
        Assert.DoesNotContain(
            module.StageOptions,
            option => string.Equals(option.Value, outdatedStage, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(module.StageOptions, option => option.IsOutdated);
        Assert.DoesNotContain(
            module.StageOptions,
            option => option.DisplayName.Contains("Outdated", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveClosedWeeklyStage()
    {
        var dayOfWeek = MallDailyResetHelper.GetYjDate(DateTime.UtcNow, "Official").DayOfWeek;
        return WeeklyStageFixtures.First(candidate => !candidate.OpenDays.Contains(dayOfWeek)).Stage;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-fight-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static UnifiedConfigurationService CreateService(string root)
    {
        return new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            root);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            TaskQueueFeatureService taskQueue,
            CapturingBridge bridge,
            MAAUnifiedRuntime runtime)
        {
            Root = root;
            Config = config;
            TaskQueue = taskQueue;
            Bridge = bridge;
            Runtime = runtime;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public CapturingBridge Bridge { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-fight-parity-tests", Guid.NewGuid().ToString("N"));
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

            var bridge = new CapturingBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var taskQueue = new TaskQueueFeatureService(session, config);

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
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(bridge, connect),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(config, diagnostics, platform.PostActionExecutorService),
            };
            return new TestFixture(root, config, taskQueue, bridge, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temp folders
            }
        }
    }

    private sealed class CapturingBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _taskId;

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbackChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _callbackChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
