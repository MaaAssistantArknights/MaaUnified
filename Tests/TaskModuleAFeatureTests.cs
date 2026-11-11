using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class TaskModuleAFeatureTests
{
    [Fact]
    public async Task AddTask_NormalizesTaskTypeAndInjectsModuleDefaults()
    {
        await using var fixture = await TestFixture.CreateAsync();

        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUpTask", "startup")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("FightTask", "fight")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("RecruitTask", "recruit")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("SingleStepTask", "single-step")).Success);

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        var tasks = Assert.IsAssignableFrom<IReadOnlyList<UnifiedTaskItem>>(queue.Value);
        Assert.Equal(4, tasks.Count);

        Assert.Equal("StartUp", tasks[0].Type);
        Assert.Equal("Official", tasks[0].Params["client_type"]?.GetValue<string>());
        Assert.True(tasks[0].Params["start_game_enabled"]?.GetValue<bool>());

        Assert.Equal("Fight", tasks[1].Type);
        Assert.Equal(FightStageSelection.CurrentOrLast, tasks[1].Params["stage"]?.GetValue<string>());
        Assert.Equal(int.MaxValue, tasks[1].Params["times"]?.GetValue<int>());
        Assert.Equal(0, tasks[1].Params["series"]?.GetValue<int>());

        Assert.Equal("Recruit", tasks[2].Type);
        Assert.Equal(4, tasks[2].Params["times"]?.GetValue<int>());
        Assert.True(tasks[2].Params["refresh"]?.GetValue<bool>());
        Assert.True(tasks[2].Params["force_refresh"]?.GetValue<bool>());
        Assert.True(tasks[2].Params["skip_robot"]?.GetValue<bool>());

        Assert.Equal(TaskModuleTypes.SingleStep, tasks[3].Type);
        Assert.Empty(tasks[3].Params);
    }

    [Fact]
    public async Task QueueEnabledTasks_SingleStepMarkerWithoutRuntimeParams_ShouldBlockAppend()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step")).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.False(queueResult.Success);
        Assert.Contains("SingleStepTypeMissing", queueResult.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public async Task QueueEnabledTasks_SingleStepWithCoreParams_ShouldAppendSingleStep()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["type"] = "copilot",
                ["subtype"] = "start",
            })).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal(TaskModuleTypes.SingleStep, appended.Type);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal("copilot", json["type"]?.GetValue<string>());
        Assert.Equal("start", json["subtype"]?.GetValue<string>());
    }

    [Fact]
    public async Task QueueEnabledTasks_OneShotEnabledTask_ShouldAppendAndKeepOneShotState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["type"] = "copilot",
                ["subtype"] = "start",
            })).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(0, null)).Success);
        Assert.True((await fixture.TaskQueue.SaveAsync()).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue[0].IsEnabled);
        var persistedTask = await ReadPersistedTaskAsync(fixture);
        Assert.Null(persistedTask["IsEnabled"]);
    }

    [Fact]
    public async Task QueueEnabledTasks_OneShotSkippedTask_ShouldSkipAndKeepOneShotState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[TaskQueueEnabledState.MainTasksInvertNullFunctionKey] = JsonValue.Create(true);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["type"] = "copilot",
                ["subtype"] = "start",
            })).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(0, null)).Success);
        Assert.True((await fixture.TaskQueue.SaveAsync()).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue[0].IsEnabled);
        var persistedTask = await ReadPersistedTaskAsync(fixture);
        Assert.Null(persistedTask["IsEnabled"]);
    }

    [Fact]
    public async Task QueueEnabledTasks_FightInventoryTargetWithoutDepot_ShouldSkipAppend()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 300,
            IsInventoryTarget = true,
        })).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        Assert.Equal(0, queueResult.Value);
        Assert.Empty(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public async Task SaveStartUpParams_AppliesLinkageAndSharedProfileValues()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup")).Success);

        var save = await fixture.TaskQueue.SaveStartUpParamsAsync(0, new StartUpTaskParamsDto
        {
            AccountName = "my-account",
            ClientType = "Txwy",
            StartGameEnabled = true,
            ConnectConfig = "PC",
            ConnectAddress = "127.0.0.1:7555",
            AdbPath = "/opt/adb",
            TouchMode = "adb",
            AutoDetectConnection = false,
            AttachWindowScreencapMethod = "2",
            AttachWindowMouseMethod = "4",
            AttachWindowKeyboardMethod = "1",
        });

        Assert.True(save.Success);
        var startUpParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("Txwy", startUpParams["client_type"]?.GetValue<string>());
        Assert.Equal(string.Empty, startUpParams["account_name"]?.GetValue<string>());
        Assert.False(startUpParams["start_game_enabled"]?.GetValue<bool>());

        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        Assert.Equal("PC", profile.Values["ConnectConfig"]?.GetValue<string>());
        Assert.Equal("127.0.0.1:7555", profile.Values["ConnectAddress"]?.GetValue<string>());
        Assert.False(profile.Values["StartGame"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SaveFightParams_PreservesWarningAndLinkageFields()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight")).Success);

        var save = await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "Annihilation",
            UseMedicine = true,
            Medicine = 1,
            UseStone = false,
            Stone = 0,
            EnableTimesLimit = true,
            Times = 11,
            Series = 5,
            IsDrGrandet = true,
            UseExpiringMedicine = true,
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 2,
            UseCustomAnnihilation = true,
            AnnihilationStage = "Chernobog",
            UseAlternateStage = true,
            HideUnavailableStage = true,
            StageResetMode = "Current",
        });

        Assert.True(save.Success);
        var taskParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("Chernobog", taskParams["stage"]?.GetValue<string>());
        Assert.Equal("Ignore", taskParams["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.False(taskParams["_ui_hide_unavailable_stage"]?.GetValue<bool>());

        var validate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validate.Success);
        Assert.NotNull(validate.Value);
        Assert.Contains(validate.Value!.Issues, issue => issue.Code == "FightTimesMayNotExhausted" && !issue.Blocking);
    }

    [Fact]
    public async Task SaveRecruitParams_BlocksInvalidTime_AndAppliesLinkageWhenValid()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        var invalid = await fixture.TaskQueue.SaveRecruitParamsAsync(0, new RecruitTaskParamsDto
        {
            Times = 2,
            Refresh = true,
            ForceRefresh = true,
            UseExpedited = true,
            SkipRobot = true,
            SetTime = true,
            Level3Time = 55,
            Level4Time = 540,
            Level5Time = 540,
        });
        Assert.False(invalid.Success);

        var valid = await fixture.TaskQueue.SaveRecruitParamsAsync(0, new RecruitTaskParamsDto
        {
            Times = 3,
            Refresh = false,
            ForceRefresh = true,
            UseExpedited = true,
            SkipRobot = true,
            SetTime = true,
            Level3Time = 540,
            Level4Time = 540,
            Level5Time = 540,
        });
        Assert.True(valid.Success);

        var taskParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.False(taskParams["refresh"]?.GetValue<bool>());
        Assert.True(taskParams["force_refresh"]?.GetValue<bool>());
        Assert.Equal(3, taskParams["expedite_times"]?.GetValue<int>());
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitUseExpedited_ShouldBeOneShotAndNotPersisted()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Recruit, "recruit")).Success);
        Assert.True((await fixture.TaskQueue.SaveRecruitParamsAsync(0, new RecruitTaskParamsDto
        {
            Times = 2,
            Refresh = true,
            ForceRefresh = true,
            UseExpedited = true,
            SkipRobot = true,
            SetTime = true,
            Level3Time = 540,
            Level4Time = 540,
            Level5Time = 540,
        })).Success);

        var flush = await fixture.TaskQueue.FlushTaskParamWritesAsync();
        Assert.True(flush.Success);

        var persistedPath = Path.Combine(fixture.Root, "config", "avalonia.json");
        var persisted = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(persistedPath)));
        var persistedProfiles = Assert.IsType<JsonObject>(persisted["Profiles"]);
        var persistedDefault = Assert.IsType<JsonObject>(persistedProfiles["Default"]);
        var persistedQueue = Assert.IsType<JsonArray>(persistedDefault["TaskQueue"]);
        var persistedTask = Assert.IsType<JsonObject>(persistedQueue[0]);
        var persistedParams = Assert.IsType<JsonObject>(persistedTask["Params"]);
        Assert.False(persistedParams.ContainsKey("expedite"));
        Assert.False(persistedParams.ContainsKey("expedite_times"));

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var appendedJson = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.True(appendedJson["expedite"]?.GetValue<bool>());
        Assert.Equal(2, appendedJson["expedite_times"]?.GetValue<int>());

        var taskParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.False(taskParams["expedite"]?.GetValue<bool>());
        Assert.Null(taskParams["expedite_times"]);
    }

    [Fact]
    public async Task RecruitModule_TimeSetters_ShouldNormalizeHourMinuteCombinationWithWraparoundSemantics()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var log = new UiLogService();
        var diagnostics = new UiDiagnosticsService(fixture.Root, log);
        var session = new UnifiedSessionService(fixture.Bridge, fixture.Config, log, new SessionStateMachine());
        var platform = PlatformServicesFactory.CreateDefaults();
        var platformCapabilityService = new PlatformCapabilityFeatureService(platform, diagnostics);
        var connectFeatureService = new ConnectFeatureService(session, fixture.Config);
        var runtime = new MAAUnifiedRuntime
        {
            CoreBridge = fixture.Bridge,
            ConfigurationService = fixture.Config,
            ResourceWorkflowService = new ResourceWorkflowService(fixture.Root, fixture.Bridge, log),
            SessionService = session,
            Platform = platform,
            LogService = log,
            DiagnosticsService = diagnostics,
            ConnectFeatureService = connectFeatureService,
            ShellFeatureService = new ShellFeatureService(connectFeatureService),
            TaskQueueFeatureService = new TaskQueueFeatureService(session, fixture.Config),
            CopilotFeatureService = new CopilotFeatureService(),
            ToolboxFeatureService = new ToolboxFeatureService(),
            RemoteControlFeatureService = new RemoteControlFeatureService(),
            PlatformCapabilityService = platformCapabilityService,
            OverlayFeatureService = new OverlayFeatureService(platformCapabilityService),
            NotificationProviderFeatureService = new NotificationProviderFeatureService(),
            SettingsFeatureService = new SettingsFeatureService(fixture.Config, platformCapabilityService, diagnostics),
            DialogFeatureService = new DialogFeatureService(diagnostics),
            PostActionFeatureService = new PostActionFeatureService(
                fixture.Config,
                diagnostics,
                platform.PostActionExecutorService),
        };
        var module = new RecruitTaskModuleViewModel(runtime, new LocalizedTextMap { Language = "en-us" });

        Assert.False(module.UseExpedited);
        module.ForceRefresh = true;
        module.Refresh = false;
        Assert.True(module.ForceRefresh);

        module.UseExpedited = true;
        Assert.Null(module.UseExpedited);
        module.UseExpedited = false;
        Assert.False(module.UseExpedited);

        module.Level3Time = 90;
        module.Level3Hour = 9;
        Assert.Equal(510, module.Level3Time);
        Assert.Equal(8, module.Level3Hour);
        Assert.Equal(30, module.Level3Minute);

        module.Level4Time = 540;
        module.Level4Minute = 30;
        Assert.Equal(510, module.Level4Time);
        Assert.Equal(8, module.Level4Hour);
        Assert.Equal(30, module.Level4Minute);
    }

    [Fact]
    public async Task GetRecruitParamsAsync_IgnoresUnsupportedLevel6_AndKeepsLegacyEntriesCompatible()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": [
                    {
                      "Type": "Recruit",
                      "Name": "RecruitWithLevel6",
                      "IsEnabled": true,
                      "Params": {
                        "refresh": true,
                        "force_refresh": true,
                        "select": [4, 6],
                        "confirm": [1, 3, 4, 6],
                        "times": 4,
                        "set_time": true,
                        "expedite": false,
                        "skip_robot": true,
                        "extra_tags_mode": 0,
                        "first_tags": [],
                        "recruitment_time": {
                          "3": 540,
                          "4": 530,
                          "5": 520
                        }
                      }
                    },
                    {
                      "Type": "Recruit",
                      "Name": "RecruitLegacy",
                      "IsEnabled": true,
                      "Params": {
                        "refresh": false,
                        "force_refresh": false,
                        "select": [4],
                        "confirm": [1, 3, 4],
                        "times": 2,
                        "set_time": true,
                        "expedite": true,
                        "skip_robot": true,
                        "extra_tags_mode": 0,
                        "first_tags": [],
                        "recruitment_time": {
                          "3": 540,
                          "4": 540,
                          "5": 540
                        }
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            root);

        var load = await service.LoadOrBootstrapAsync();
        Assert.True(load.LoadedFromExistingConfig);

        var bridge = new CapturingBridge();
        var session = new UnifiedSessionService(bridge, service, new UiLogService(), new SessionStateMachine());
        var queue = new TaskQueueFeatureService(session, service);

        var withLevel6 = await queue.GetRecruitParamsAsync(0);
        Assert.True(withLevel6.Success);
        Assert.NotNull(withLevel6.Value);
        Assert.False(withLevel6.Value!.ChooseLevel6);
        Assert.True(withLevel6.Value.ChooseLevel4);
        Assert.Equal(530, withLevel6.Value.Level4Time);

        var legacy = await queue.GetRecruitParamsAsync(1);
        Assert.True(legacy.Success);
        Assert.NotNull(legacy.Value);
        Assert.False(legacy.Value!.ChooseLevel6);
        Assert.True(legacy.Value.ChooseLevel4);
        Assert.False(legacy.Value.Refresh);
        Assert.False(legacy.Value.UseExpedited);
        Assert.False(service.CurrentConfig.Profiles["Default"].TaskQueue[1].Params.ContainsKey("expedite"));
    }

    [Fact]
    public async Task QueueEnabledTasks_CompilesAndNormalizesParamsBeforeAppend()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUpTask", "startup")).Success);
        Assert.True((await fixture.TaskQueue.SaveStartUpParamsAsync(0, new StartUpTaskParamsDto
        {
            ClientType = "Txwy",
            AccountName = "will-clear",
            StartGameEnabled = true,
            ConnectConfig = "PC",
        })).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);
        Assert.Equal(1, queueResult.Value);

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("StartUp", appended.Type);
        var paramsJson = JsonNode.Parse(appended.ParamsJson) as JsonObject;
        Assert.NotNull(paramsJson);
        Assert.Equal(string.Empty, paramsJson!["account_name"]?.GetValue<string>());
        Assert.False(paramsJson["start_game_enabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task QueueEnabledTasks_FightMappingMatchesBaselineFields()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "Annihilation",
            UseMedicine = true,
            Medicine = 2,
            UseStone = true,
            Stone = 1,
            EnableTimesLimit = true,
            Times = 6,
            Series = 3,
            IsDrGrandet = true,
            UseExpiringMedicine = true,
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 2,
            UseCustomAnnihilation = true,
            AnnihilationStage = "Anni-3",
            UseAlternateStage = true,
            HideUnavailableStage = true,
            StageResetMode = "Current",
        })).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Fight", appended.Type);

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal("Anni-3", json["stage"]?.GetValue<string>());
        Assert.Equal(2, json["medicine"]?.GetValue<int>());
        Assert.Equal(1, json["stone"]?.GetValue<int>());
        Assert.Equal(9999, json["expiring_medicine"]?.GetValue<int>());
        Assert.Equal(2, json["medicine_expire_days"]?.GetValue<int>());
        Assert.Equal(6, json["times"]?.GetValue<int>());
        Assert.Equal(3, json["series"]?.GetValue<int>());
        Assert.True(json["DrGrandet"]?.GetValue<bool>());
        Assert.DoesNotContain(json, pair => pair.Key.StartsWith("_ui_", StringComparison.Ordinal));
        Assert.Equal("Official", json["client_type"]?.GetValue<string>());
        Assert.False(json.ContainsKey("penguin_id"));
        Assert.False(json.ContainsKey("yituliu_id"));
        Assert.Equal("CN", json["server"]?.GetValue<string>());

        var drops = Assert.IsType<JsonObject>(json["drops"]);
        Assert.Equal(2, drops["30012"]?.GetValue<int>());
    }

    [Fact]
    public async Task QueueEnabledTasks_FightCurrentOrLast_PersistsSentinelButAppendsEmptyStage()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = FightStageSelection.CurrentOrLast,
            UseMedicine = false,
            Medicine = 0,
            UseStone = false,
            Stone = 0,
            EnableTimesLimit = true,
            Times = 1,
            Series = 1,
        })).Success);

        var stored = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal(FightStageSelection.CurrentOrLast, stored["stage"]?.GetValue<string>());

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(string.Empty, json["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitMappingMatchesBaselineFields()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);
        Assert.True((await fixture.TaskQueue.SaveRecruitParamsAsync(0, new RecruitTaskParamsDto
        {
            Times = 4,
            Refresh = false,
            ForceRefresh = true,
            UseExpedited = true,
            SkipRobot = true,
            ChooseLevel3 = true,
            ChooseLevel4 = true,
            ChooseLevel5 = false,
            ChooseLevel6 = true,
            SetTime = true,
            Level3Time = 540,
            Level4Time = 530,
            Level5Time = 520,
        })).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Recruit", appended.Type);

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.False(json["refresh"]?.GetValue<bool>());
        Assert.True(json["force_refresh"]?.GetValue<bool>());
        Assert.True(json["expedite"]?.GetValue<bool>());
        Assert.Equal(4, json["times"]?.GetValue<int>());
        Assert.Equal(4, json["expedite_times"]?.GetValue<int>());
        Assert.True(json["skip_robot"]?.GetValue<bool>());

        var confirm = Assert.IsType<JsonArray>(json["confirm"]);
        Assert.Contains(confirm, item => item?.GetValue<int>() == 1);
        Assert.Contains(confirm, item => item?.GetValue<int>() == 3);
        Assert.Contains(confirm, item => item?.GetValue<int>() == 4);
        Assert.DoesNotContain(confirm, item => item?.GetValue<int>() == 6);
        Assert.DoesNotContain(confirm, item => item?.GetValue<int>() == 5);

        var select = Assert.IsType<JsonArray>(json["select"]);
        Assert.Contains(select, item => item?.GetValue<int>() == 4);
        Assert.DoesNotContain(select, item => item?.GetValue<int>() == 6);
        Assert.DoesNotContain(select, item => item?.GetValue<int>() == 5);

        var recruitmentTime = Assert.IsType<JsonObject>(json["recruitment_time"]);
        Assert.Equal(540, recruitmentTime["3"]?.GetValue<int>());
        Assert.Equal(530, recruitmentTime["4"]?.GetValue<int>());
        Assert.Equal(520, recruitmentTime["5"]?.GetValue<int>());
        Assert.DoesNotContain("6", recruitmentTime.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.Equal(
            [
                "confirm",
                "expedite",
                "expedite_times",
                "extra_tags_mode",
                "first_tags",
                "force_refresh",
                "penguin_id",
                "recruitment_time",
                "refresh",
                "report_to_penguin",
                "report_to_yituliu",
                "select",
                "server",
                "set_time",
                "skip_robot",
                "times",
                "yituliu_id",
            ],
            json.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitReportSwitches_DefaultTrueWhenSettingsAbsent_LikeFrozenWpf()
    {
        // Frozen WPF reads EnablePenguin/EnableYituliu with a bool.TrueString fallback
        // (GameSettingsUserControlModel), so a fresh profile that never saved the settings
        // page must still emit both switches as true and keep the (empty) ID fields.
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.True(json["report_to_penguin"]?.GetValue<bool>());
        Assert.True(json["report_to_yituliu"]?.GetValue<bool>());
        Assert.Equal(string.Empty, json["penguin_id"]?.GetValue<string>());
        Assert.Equal(string.Empty, json["yituliu_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitReportSwitches_ExplicitFalseIsHonored()
    {
        // An explicit stored false is a persistent user choice: it must survive the
        // true fallback and still strip the ID fields at the Core boundary.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["EnablePenguin"] = JsonValue.Create(false);
        profile.Values["EnableYituliu"] = JsonValue.Create(false);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.False(json["report_to_penguin"]?.GetValue<bool>());
        Assert.False(json["report_to_yituliu"]?.GetValue<bool>());
        Assert.DoesNotContain("penguin_id", json.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.DoesNotContain("yituliu_id", json.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitReportSwitches_SettingsPageDottedKeysAreHonored_LikeFrozenWpf()
    {
        // The settings page saves the dotted legacy keys (ConfigurationKeys.EnablePenguin etc.,
        // pinned by SettingsModuleAK2FeatureTests); compile must honor them so an explicit UI
        // privacy opt-out reaches the Core payload exactly like frozen WPF.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values[ConfigurationKeys.EnablePenguin] = JsonValue.Create("False");
        profile.Values[ConfigurationKeys.EnableYituliu] = JsonValue.Create("False");
        profile.Values[ConfigurationKeys.PenguinId] = JsonValue.Create("penguin-001");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.False(json["report_to_penguin"]?.GetValue<bool>());
        Assert.False(json["report_to_yituliu"]?.GetValue<bool>());
        Assert.DoesNotContain("penguin_id", json.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.DoesNotContain("yituliu_id", json.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitReportIds_SettingsPagePenguinIdReachesCorePayload()
    {
        // Switches absent -> default true (6550614); the settings-page Penguin.Id value must
        // be emitted for both penguin_id and yituliu_id (frozen WPF passes
        // GameSettings.PenguinId for both fields).
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values[ConfigurationKeys.PenguinId] = JsonValue.Create("penguin-001");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.True(json["report_to_penguin"]?.GetValue<bool>());
        Assert.True(json["report_to_yituliu"]?.GetValue<bool>());
        Assert.Equal("penguin-001", json["penguin_id"]?.GetValue<string>());
        Assert.Equal("penguin-001", json["yituliu_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitReportSwitches_SettingsPageKeyWinsOverStaleImportAlias()
    {
        // A gui.json import writes the bare alias key; a later explicit settings-page toggle
        // (dotted key) is the live user choice and must win over the stale alias.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["EnablePenguin"] = JsonValue.Create(true);
        profile.Values[ConfigurationKeys.EnablePenguin] = JsonValue.Create("False");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.False(json["report_to_penguin"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RecruitModule_UnrelatedSave_PreservesUnresolvableStoredFirstTags_LikeFrozenWpf()
    {
        // Frozen WPF rewrites Level3PreferTags only from the tag-selection setter
        // (AutoRecruitFirstList); unrelated edits (RecruitMaxTimes etc.) leave the stored
        // list verbatim and serialization emits it unchanged. Tags that are not resolvable
        // under the current client type (e.g. chosen under YoStarEN, then switched to
        // Official) must therefore survive a panel save that only touched another field.
        // (The test environment ships no recruitment.json, so every stored tag exercises
        // the unresolvable branch.)
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);
        var stored = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        stored["first_tags"] = new JsonArray("支援机械", "近战位");
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, stored)).Success);

        var log = new UiLogService();
        var diagnostics = new UiDiagnosticsService(fixture.Root, log);
        var session = new UnifiedSessionService(fixture.Bridge, fixture.Config, log, new SessionStateMachine());
        var platform = PlatformServicesFactory.CreateDefaults();
        var platformCapabilityService = new PlatformCapabilityFeatureService(platform, diagnostics);
        var connectFeatureService = new ConnectFeatureService(session, fixture.Config);
        var runtime = new MAAUnifiedRuntime
        {
            CoreBridge = fixture.Bridge,
            ConfigurationService = fixture.Config,
            ResourceWorkflowService = new ResourceWorkflowService(fixture.Root, fixture.Bridge, log),
            SessionService = session,
            Platform = platform,
            LogService = log,
            DiagnosticsService = diagnostics,
            ConnectFeatureService = connectFeatureService,
            ShellFeatureService = new ShellFeatureService(connectFeatureService),
            TaskQueueFeatureService = new TaskQueueFeatureService(session, fixture.Config),
            CopilotFeatureService = new CopilotFeatureService(),
            ToolboxFeatureService = new ToolboxFeatureService(),
            RemoteControlFeatureService = new RemoteControlFeatureService(),
            PlatformCapabilityService = platformCapabilityService,
            OverlayFeatureService = new OverlayFeatureService(platformCapabilityService),
            NotificationProviderFeatureService = new NotificationProviderFeatureService(),
            SettingsFeatureService = new SettingsFeatureService(fixture.Config, platformCapabilityService, diagnostics),
            DialogFeatureService = new DialogFeatureService(diagnostics),
            PostActionFeatureService = new PostActionFeatureService(
                fixture.Config,
                diagnostics,
                platform.PostActionExecutorService),
        };
        var module = new RecruitTaskModuleViewModel(runtime, new LocalizedTextMap { Language = "en-us" });
        await module.BindAsync(0);
        Assert.True(module.IsTaskBound);

        module.Times = 8;
        Assert.True(await module.SaveAsync());

        var persisted = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        var firstTags = Assert.IsType<JsonArray>(persisted["first_tags"]);
        Assert.Equal(["支援机械", "近战位"], firstTags.Select(tag => tag?.GetValue<string>()).ToArray());
    }

    [Fact]
    public void RecruitModule_ResolveFirstTagsForSave_MirrorsFrozenWpfStoredListLifetime()
    {
        // Untouched selection: the stored list persists verbatim, including values that are
        // not resolvable under the current client type.
        Assert.Equal(
            ["支援机械", "近战位"],
            RecruitTaskModuleViewModel.ResolveFirstTagsForSave(["支援机械", "近战位"], ["近战位"], selectionTouched: false));

        // Touched selection: the stored list is rewritten from the selection, dropping
        // unresolvable values exactly like the frozen WPF AutoRecruitFirstList setter.
        Assert.Equal(
            ["近战位"],
            RecruitTaskModuleViewModel.ResolveFirstTagsForSave(["支援机械", "近战位"], ["近战位"], selectionTouched: true));
    }

    [Theory]
    [InlineData("Official", "CN")]
    [InlineData("Bilibili", "CN")]
    [InlineData("YoStarEN", "US")]
    [InlineData("YoStarJP", "JP")]
    [InlineData("YoStarKR", "KR")]
    [InlineData("txwy", "ZH_TW")]
    [InlineData("NotAClientType", "CN")]
    public async Task QueueEnabledTasks_RecruitServer_DerivesFromClientTypeLikeFrozenWpf(string clientType, string expectedServer)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = clientType;
        // A stale/hand-edited ServerType key must not win: frozen WPF has no such
        // config key and always derives Recruit's server from the client type.
        profile.Values["ServerType"] = "KR";
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Recruit", appended.Type);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(expectedServer, json["server"]?.GetValue<string>());
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitIgnoresStalePreserveTagFields()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);
        var staleParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        staleParams["preserve_tags"] = new JsonArray("支援机械");
        staleParams["_ui_preserve_tags_enabled"] = true;
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, staleParams)).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.DoesNotContain("preserve_tags", json.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.DoesNotContain("_ui_preserve_tags_enabled", json.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitMalformedStoredTimeValues_AbortEntireQueueRun()
    {
        // Deliberate MAAUnified fail-fast design (frozen WPF passes these values through
        // unchanged): a hand-edited out-of-range recruit times/recruitment_time produces
        // blocking validation issues, and UnifiedSessionService aborts the whole append,
        // so no task - Recruit or otherwise - reaches Core. UI paths normalize these
        // values (NormalizeRecruitTime replicates the WPF setter quirk), so this state is
        // only reachable via a hand-edited avalonia.json or legacy gui.new.json import.
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Award", "award")).Success);

        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.TaskQueue[0].Params = new JsonObject
        {
            ["times"] = -1,
            ["recruitment_time"] = new JsonObject
            {
                ["3"] = 45,
                ["4"] = 600,
                ["5"] = 540,
            },
        };

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.False(queueResult.Success);
        Assert.Contains("RecruitTimesOutOfRange", queueResult.Error?.Message ?? string.Empty);
        Assert.Contains("RecruitTimeOutOfRange", queueResult.Error?.Message ?? string.Empty);
        Assert.Empty(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public async Task QueueEnabledTasks_RecruitHandEditedSetTimeFalse_IsHonored()
    {
        // Deliberate divergence from frozen WPF (which hardcodes SetRecruitTime = true):
        // MAAUnified keeps a stored `set_time` key with default true; the view model and
        // the legacy converter always write true, so only a hand-edited profile can carry
        // false, and that explicit value is honored instead of being overwritten.
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.TaskQueue[0].Params!["set_time"] = false;

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.False(json["set_time"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SaveAndReload_RoundTripPreservesModuleADtoSemantics()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit")).Success);

        Assert.True((await fixture.TaskQueue.SaveStartUpParamsAsync(0, new StartUpTaskParamsDto
        {
            AccountName = "account-a",
            ClientType = "Txwy",
            StartGameEnabled = true,
            ConnectConfig = "PC",
            ConnectAddress = "127.0.0.1:16384",
            AdbPath = "/opt/adb",
            TouchMode = "adb",
            AutoDetectConnection = false,
            AttachWindowScreencapMethod = "3",
            AttachWindowMouseMethod = "2",
            AttachWindowKeyboardMethod = "1",
        })).Success);

        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(1, new FightTaskParamsDto
        {
            Stage = "Annihilation",
            UseMedicine = true,
            Medicine = 2,
            UseStone = false,
            Stone = 0,
            EnableTimesLimit = true,
            Times = 11,
            Series = 5,
            IsDrGrandet = true,
            UseExpiringMedicine = true,
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 2,
            UseCustomAnnihilation = true,
            AnnihilationStage = "Chernobog",
            UseAlternateStage = true,
            HideUnavailableStage = true,
            StageResetMode = "Current",
        })).Success);

        Assert.True((await fixture.TaskQueue.SaveRecruitParamsAsync(2, new RecruitTaskParamsDto
        {
            Times = 3,
            Refresh = false,
            ForceRefresh = true,
            UseExpedited = true,
            SkipRobot = true,
            ChooseLevel3 = true,
            ChooseLevel4 = false,
            ChooseLevel5 = true,
            ChooseLevel6 = true,
            SetTime = true,
            FirstTags = ["先锋干员"],
            Level3Time = 540,
            Level4Time = 530,
            Level5Time = 520,
        })).Success);

        Assert.True((await fixture.TaskQueue.SaveAsync()).Success);

        var log = new UiLogService();
        var config = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            fixture.Root);
        var load = await config.LoadOrBootstrapAsync();
        Assert.True(load.LoadedFromExistingConfig);

        var bridge = new CapturingBridge();
        var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
        var queue = new TaskQueueFeatureService(session, config);

        var startUp = await queue.GetStartUpParamsAsync(0);
        Assert.True(startUp.Success);
        Assert.NotNull(startUp.Value);
        Assert.Equal(string.Empty, startUp.Value!.AccountName);
        Assert.Equal("Txwy", startUp.Value.ClientType);
        Assert.False(startUp.Value.StartGameEnabled);
        Assert.Equal("PC", startUp.Value.ConnectConfig);
        Assert.Equal("127.0.0.1:16384", startUp.Value.ConnectAddress);
        Assert.Equal("/opt/adb", startUp.Value.AdbPath);

        var fight = await queue.GetFightParamsAsync(1);
        Assert.True(fight.Success);
        Assert.NotNull(fight.Value);
        Assert.Equal("Chernobog", fight.Value!.Stage);
        Assert.True(fight.Value.UseAlternateStage);
        Assert.False(fight.Value.HideUnavailableStage);
        Assert.Equal("Ignore", fight.Value.StageResetMode);
        Assert.Equal(11, fight.Value.Times);
        Assert.Equal(5, fight.Value.Series);

        var recruit = await queue.GetRecruitParamsAsync(2);
        Assert.True(recruit.Success);
        Assert.NotNull(recruit.Value);
        Assert.Equal(3, recruit.Value!.Times);
        Assert.False(recruit.Value.Refresh);
        Assert.True(recruit.Value.ForceRefresh);
        Assert.False(recruit.Value.UseExpedited);
        Assert.True(recruit.Value.ChooseLevel3);
        Assert.False(recruit.Value.ChooseLevel4);
        Assert.True(recruit.Value.ChooseLevel5);
        Assert.False(recruit.Value.ChooseLevel6);
        Assert.Equal(["先锋干员"], recruit.Value.FirstTags);
        Assert.Equal(540, recruit.Value.Level3Time);
        Assert.Equal(530, recruit.Value.Level4Time);
        Assert.Equal(520, recruit.Value.Level5Time);
    }

    [Fact]
    public async Task LoadExistingConfig_ReportsValidationIssues_ForMissingRequiredFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": [
                    {
                      "Type": "Recruit",
                      "Name": "Recruit",
                      "IsEnabled": true,
                      "Params": {
                        "times": 4
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            root);

        var load = await service.LoadOrBootstrapAsync();
        Assert.True(load.LoadedFromExistingConfig);
        Assert.NotEmpty(load.ValidationIssues);
        Assert.Contains(load.ValidationIssues, issue => issue.Code == "TaskFieldMissing");
    }

    [Fact]
    public void Localization_HasModuleAKeys_ForAllSupportedLanguages()
    {
        var text = new LocalizedTextMap();
        var languages = new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas" };
        var requiredKeys = GetRequiredLocalizationKeys();

        foreach (var language in languages)
        {
            text.Language = language;
            foreach (var key in requiredKeys)
            {
                var value = text[key];
                Assert.False(string.Equals(value, key, StringComparison.Ordinal), $"Missing `{key}` for `{language}`");
            }
        }
    }

    [Fact]
    public void Localization_UsesUnifiedLocalizerInsteadOfEmbeddedLocaleDictionaries()
    {
        var legacyLocaleFields = typeof(LocalizedTextMap)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(field =>
                typeof(System.Collections.IDictionary).IsAssignableFrom(field.FieldType)
                || typeof(IReadOnlyDictionary<string, string>).IsAssignableFrom(field.FieldType))
            .ToArray();

        Assert.Empty(legacyLocaleFields);

        var requiredKeys = GetRequiredLocalizationKeys();
        var languages = new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas" };
        var text = new LocalizedTextMap();

        foreach (var language in languages)
        {
            text.Language = language;
            foreach (var key in requiredKeys)
            {
                Assert.NotEqual(key, text[key]);
            }
        }
    }

    [Fact]
    public async Task GetModuleAParamsAsync_LocalizesLoadedMessage_AndMapsLegacyDefaultTaskNamesToComponentTitles()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create("ja-jp");

        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "开始唤醒")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "理智作战")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "自动公招")).Success);

        var localizer = UiLocalizer.Create("ja-jp");
        var template = localizer.GetOrDefault("TaskQueue.Status.ParamsLoaded", "Loaded params for `{0}`.", "TaskQueue.Status");

        var raw = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(raw.Success);
        Assert.Equal(string.Format(template, localizer["StartUp.Title"]), raw.Message);
        Assert.DoesNotContain("开始唤醒", raw.Message, StringComparison.Ordinal);

        var startUp = await fixture.TaskQueue.GetStartUpParamsAsync(0);
        Assert.True(startUp.Success);
        Assert.Equal(string.Format(template, localizer["StartUp.Title"]), startUp.Message);

        var fight = await fixture.TaskQueue.GetFightParamsAsync(1);
        Assert.True(fight.Success);
        Assert.Equal(string.Format(template, localizer["Fight.Title"]), fight.Message);
        Assert.DoesNotContain("理智作战", fight.Message, StringComparison.Ordinal);

        var recruit = await fixture.TaskQueue.GetRecruitParamsAsync(2);
        Assert.True(recruit.Success);
        Assert.Equal(string.Format(template, localizer["Recruit.Title"]), recruit.Message);
        Assert.DoesNotContain("自动公招", recruit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueueOperations_LocalizeOperationalMessages_InJapanese()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create("ja-jp");

        var localizer = UiLocalizer.Create("ja-jp");

        var add = await fixture.TaskQueue.AddTaskAsync("StartUp", "开始唤醒");
        Assert.True(add.Success);
        Assert.Equal(
            string.Format(
                localizer.GetOrDefault("TaskQueue.Status.TaskAdded", "Added task `{0}`.", "TaskQueue.Status"),
                localizer["StartUp.Title"]),
            add.Message);

        var toggle = await fixture.TaskQueue.SetTaskEnabledAsync(0, false);
        Assert.True(toggle.Success);
        Assert.Equal(
            string.Format(
                localizer.GetOrDefault("TaskQueue.Status.TaskEnabled.False", "Task `{0}` disabled.", "TaskQueue.Status"),
                localizer["StartUp.Title"]),
            toggle.Message);

        var save = await fixture.TaskQueue.SaveStartUpParamsAsync(0, new StartUpTaskParamsDto
        {
            ClientType = "Official",
            StartGameEnabled = true,
            ConnectConfig = "General",
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            AdbPath = string.Empty,
            TouchMode = "minitouch",
            AutoDetectConnection = false,
            AttachWindowScreencapMethod = "0",
            AttachWindowMouseMethod = "0",
            AttachWindowKeyboardMethod = "0",
        });
        Assert.True(save.Success);
        Assert.Equal(
            string.Format(
                localizer.GetOrDefault("TaskQueue.Status.ParamsUpdated", "Updated params for `{0}`.", "TaskQueue.Status"),
                localizer["StartUp.Title"]),
            save.Message);

        var queueSave = await fixture.TaskQueue.SaveAsync();
        Assert.True(queueSave.Success);
        Assert.Equal(
            localizer.GetOrDefault("TaskQueue.Status.Saved", "Task queue saved.", "TaskQueue.Status"),
            queueSave.Message);
    }

    [Fact]
    public async Task TaskQueuePage_StartBlockedWhenConfigHasBlockingIssues_AndWritesDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": [
                    {
                      "Type": "Recruit",
                      "Name": "Recruit",
                      "IsEnabled": true,
                      "Params": {
                        "times": 4
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

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
        var platform = PlatformServicesFactory.CreateDefaults();
        var platformCapabilityService = new PlatformCapabilityFeatureService(platform, diagnostics);
        var connectFeatureService = new ConnectFeatureService(session, config, log, bridge, root);
        var runtime = new MAAUnifiedRuntime
        {
            CoreBridge = bridge,
            ConfigurationService = config,
            ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
            SessionService = session,
            Platform = platform,
            LogService = log,
            DiagnosticsService = diagnostics,
            ConnectFeatureService = connectFeatureService,
            ShellFeatureService = new ShellFeatureService(connectFeatureService),
            TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
            CopilotFeatureService = new CopilotFeatureService(),
            ToolboxFeatureService = new ToolboxFeatureService(),
            RemoteControlFeatureService = new RemoteControlFeatureService(),
            PlatformCapabilityService = platformCapabilityService,
            OverlayFeatureService = new OverlayFeatureService(platformCapabilityService),
            NotificationProviderFeatureService = new NotificationProviderFeatureService(),
            SettingsFeatureService = new SettingsFeatureService(config, platformCapabilityService, diagnostics),
            DialogFeatureService = new DialogFeatureService(diagnostics),
            PostActionFeatureService = new PostActionFeatureService(
                config,
                diagnostics,
                platform.PostActionExecutorService),
        };

        try
        {
            var page = new TaskQueuePageViewModel(runtime, new ConnectionGameSharedStateViewModel());
            var readyAdbPath = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "taskmodule-a-diagnostics-ready");
            Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(runtime.ConnectFeatureService, readyAdbPath)).Success);
            await page.StartAsync();
            Assert.True(page.HasBlockingConfigIssues);
            Assert.True(page.BlockingConfigIssueCount > 0);
            Assert.False(string.IsNullOrWhiteSpace(page.LastErrorMessage));

            var logPath = Path.Combine(root, "debug", "avalonia-ui-errors.log");
            Assert.True(File.Exists(logPath));
            var content = await File.ReadAllTextAsync(logPath);
            Assert.Contains("[FAILED][", content);
            Assert.Contains("code=", content);
            Assert.Contains("field=", content);
            Assert.Contains("profile=", content);
            Assert.Contains("taskIndex=", content);
            Assert.Contains("message=", content);
        }
        finally
        {
            await runtime.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in test temp folders
            }
        }
    }

    [Fact]
    public void ThemeSmoke_ModuleAViewsUseDynamicResources()
    {
        var root = ResolveRepoRoot();
        var startUpView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "StartUpTaskView.axaml"));
        var fightView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "FightSettingsView.axaml"));
        var recruitView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "RecruitSettingsView.axaml"));
        var mallView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "MallSettingsView.axaml"));
        var singleStepView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "SingleStepSettingsView.axaml"));

        Assert.Contains("DynamicResource", startUpView);
        Assert.Contains("DynamicResource", fightView);
        Assert.Contains("DynamicResource", recruitView);
        Assert.Contains("SelectedValue=\"{Binding Subtype}\"", singleStepView);
        Assert.Contains("Text=\"{Binding Type, UpdateSourceTrigger=LostFocus}\"", singleStepView);
        Assert.Contains("Text=\"{Binding DetailsText, UpdateSourceTrigger=LostFocus}\"", singleStepView);
        Assert.Contains("IsVisible=\"{Binding IsMacBundledAdbSupported}\"", startUpView);
        Assert.Contains("IsChecked=\"{Binding MacUseBundledAdb}\"", startUpView);
        Assert.Contains("IsVisible=\"{Binding ShowManualAdbPathControls}\"", startUpView);
        Assert.DoesNotContain("SelectedAttachWindowScreencapOption", startUpView);
        Assert.DoesNotContain("SelectedAttachWindowMouseOption", startUpView);
        Assert.DoesNotContain("SelectedAttachWindowKeyboardOption", startUpView);
        Assert.Contains("Text=\"{Binding Texts[Recruit.UseExpedited]}\"", recruitView);
        Assert.Contains("Tip=\"{Binding Texts[Recruit.UseExpeditedTip]}\"", recruitView);
        Assert.Contains("IsChecked=\"{Binding UseExpedited}\"", recruitView);
        Assert.Contains("IsChecked=\"False\"", recruitView);
        Assert.Contains("IsEnabled=\"False\"", recruitView);
        Assert.DoesNotContain("AutoSelectLevel6Notice", recruitView);
        Assert.Equal(2, CountOccurrences(mallView, "Tip=\"{Binding Texts[Mall.OnlyOnceADayTip]}\""));
        Assert.Contains("IsChecked=\"{Binding VisitFriendsOnceADay}\"", mallView);
        Assert.Contains("IsChecked=\"{Binding CreditFightOnceADay}\"", mallView);
    }

    private static string ResolveRepoRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }

    private static string[] ReadStringArray(JsonArray array)
    {
        return array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray();
    }

    private static string[] GetRequiredLocalizationKeys()
    {
        return
        [
            "StartUp.Title",
            "Fight.Title",
            "Recruit.Title",
            "Common.LoadingTaskHint",
            "Common.ValidationIssues",
            "Common.WarningPrefix",
            "Common.ErrorPrefix",
            "Issue.ClientTypeMissing",
            "Issue.FightStageMissing",
            "Issue.FightSeriesOutOfRange",
            "Issue.FightTimesOutOfRange",
            "Issue.FightDropMissing",
            "Issue.FightTimesMayNotExhausted",
            "Fight.StageSelect2",
            "Fight.AddStage",
            "Fight.CustomStageCode",
            "Fight.CustomStageCodeTip",
            "Fight.MultiTasksShareTip",
            "Fight.AutoRestartOption",
            "Fight.UseWeeklySchedule",
            "Fight.UseWeeklyScheduleTip",
            "Fight.WeeklySchedule.Sunday",
            "Fight.WeeklySchedule.Monday",
            "Fight.WeeklySchedule.Tuesday",
            "Fight.WeeklySchedule.Wednesday",
            "Fight.WeeklySchedule.Thursday",
            "Fight.WeeklySchedule.Friday",
            "Fight.WeeklySchedule.Saturday",
            "Issue.RecruitTimesOutOfRange",
            "Issue.RecruitTimeOutOfRange",
            "Recruit.AutoSelectLevel6",
            "Recruit.AutoSelectLevel6FixedTime",
            "Recruit.UseExpedited",
            "Recruit.UseExpeditedTip",
            "Mall.OnlyOnceADayTip",
            "Issue.TaskFieldMissing",
            "TaskQueue.Status.ParamsLoaded",
        ];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while (true)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }
    }

    private static async Task<JsonObject> ReadPersistedTaskAsync(TestFixture fixture)
    {
        var persistedPath = Path.Combine(fixture.Root, "config", "avalonia.json");
        var persisted = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(persistedPath)));
        var persistedProfiles = Assert.IsType<JsonObject>(persisted["Profiles"]);
        var persistedDefault = Assert.IsType<JsonObject>(persistedProfiles["Default"]);
        var persistedQueue = Assert.IsType<JsonArray>(persistedDefault["TaskQueue"]);
        return Assert.IsType<JsonObject>(persistedQueue[0]);
    }

    [Fact]
    public void RecruitSettingsView_TimesInput_HasNoUpperBoundLikeFrozenWpf()
    {
        // Frozen WPF RecruitSettingsUserControl.xaml binds RecruitMaxTimes to a
        // NumericUpDown with only Minimum="0" and no Maximum, so values above 99
        // stay expressible in the UI.
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "RecruitSettingsView.axaml"));

        var bindingIndex = text.IndexOf("Value=\"{Binding Times}\"", StringComparison.Ordinal);
        Assert.True(bindingIndex >= 0, "Recruit times input binding not found.");
        var elementStart = text.LastIndexOf("<controls:AppNumberInput", bindingIndex, StringComparison.Ordinal);
        Assert.True(elementStart >= 0, "Recruit times AppNumberInput element not found.");
        var elementEnd = text.IndexOf("/>", bindingIndex, StringComparison.Ordinal);
        Assert.True(elementEnd > bindingIndex, "Recruit times AppNumberInput element is not self-closing.");
        var element = text[elementStart..elementEnd];

        Assert.Contains("Minimum=\"0\"", element, StringComparison.Ordinal);
        Assert.DoesNotContain("Maximum", element, StringComparison.Ordinal);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            TaskQueueFeatureService taskQueue,
            CapturingBridge bridge)
        {
            Root = root;
            Config = config;
            TaskQueue = taskQueue;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public CapturingBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();
            _ = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "taskmodule-a-ready");

            var bridge = new CapturingBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var taskQueue = new TaskQueueFeatureService(session, config);
            return new TestFixture(root, config, taskQueue, bridge);
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
                // ignore cleanup failures in test temp folders
            }
        }
    }

    private sealed class CapturingBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private readonly List<CoreTaskRequest> _tasks = [];
        private int _taskId;

        public IReadOnlyList<CoreTaskRequest> AppendedTasks => _tasks;

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            _tasks.Add(task);
            return Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));
        }

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

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
