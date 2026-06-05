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

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        var tasks = Assert.IsAssignableFrom<IReadOnlyList<UnifiedTaskItem>>(queue.Value);
        Assert.Equal(3, tasks.Count);

        Assert.Equal("StartUp", tasks[0].Type);
        Assert.Equal("Official", tasks[0].Params["client_type"]?.GetValue<string>());
        Assert.True(tasks[0].Params["start_game_enabled"]?.GetValue<bool>());

        Assert.Equal("Fight", tasks[1].Type);
        Assert.Equal(FightStageSelection.CurrentOrLast, tasks[1].Params["stage"]?.GetValue<string>());
        Assert.Equal(int.MaxValue, tasks[1].Params["times"]?.GetValue<int>());
        Assert.Equal(1, tasks[1].Params["series"]?.GetValue<int>());

        Assert.Equal("Recruit", tasks[2].Type);
        Assert.Equal(4, tasks[2].Params["times"]?.GetValue<int>());
        Assert.True(tasks[2].Params["refresh"]?.GetValue<bool>());
        Assert.True(tasks[2].Params["force_refresh"]?.GetValue<bool>());
        Assert.True(tasks[2].Params["skip_robot"]?.GetValue<bool>());
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
        Assert.False(taskParams["force_refresh"]?.GetValue<bool>());
        Assert.Equal(3, taskParams["expedite_times"]?.GetValue<int>());
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
    public async Task GetRecruitParamsAsync_ReadsLevel6FromExistingParams_AndKeepsLegacyEntriesCompatible()
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
        Assert.True(withLevel6.Value!.ChooseLevel6);
        Assert.True(withLevel6.Value.ChooseLevel4);
        Assert.Equal(530, withLevel6.Value.Level4Time);

        var legacy = await queue.GetRecruitParamsAsync(1);
        Assert.True(legacy.Success);
        Assert.NotNull(legacy.Value);
        Assert.False(legacy.Value!.ChooseLevel6);
        Assert.True(legacy.Value.ChooseLevel4);
        Assert.False(legacy.Value.Refresh);
        Assert.True(legacy.Value.UseExpedited);
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
        Assert.Equal(6, json["times"]?.GetValue<int>());
        Assert.Equal(3, json["series"]?.GetValue<int>());
        Assert.True(json["DrGrandet"]?.GetValue<bool>());
        Assert.True(json["_ui_use_alternate_stage"]?.GetValue<bool>());
        Assert.False(json["_ui_hide_unavailable_stage"]?.GetValue<bool>());
        Assert.Equal("Ignore", json["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.True(json["_ui_use_custom_annihilation"]?.GetValue<bool>());
        Assert.Equal("Anni-3", json["_ui_annihilation_stage"]?.GetValue<string>());

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
        Assert.False(json["force_refresh"]?.GetValue<bool>());
        Assert.True(json["expedite"]?.GetValue<bool>());
        Assert.Equal(4, json["times"]?.GetValue<int>());
        Assert.Equal(4, json["expedite_times"]?.GetValue<int>());
        Assert.True(json["skip_robot"]?.GetValue<bool>());

        var confirm = Assert.IsType<JsonArray>(json["confirm"]);
        Assert.Contains(confirm, item => item?.GetValue<int>() == 1);
        Assert.Contains(confirm, item => item?.GetValue<int>() == 3);
        Assert.Contains(confirm, item => item?.GetValue<int>() == 4);
        Assert.Contains(confirm, item => item?.GetValue<int>() == 6);
        Assert.DoesNotContain(confirm, item => item?.GetValue<int>() == 5);

        var select = Assert.IsType<JsonArray>(json["select"]);
        Assert.Contains(select, item => item?.GetValue<int>() == 4);
        Assert.Contains(select, item => item?.GetValue<int>() == 6);
        Assert.DoesNotContain(select, item => item?.GetValue<int>() == 5);

        var recruitmentTime = Assert.IsType<JsonObject>(json["recruitment_time"]);
        Assert.Equal(540, recruitmentTime["3"]?.GetValue<int>());
        Assert.Equal(530, recruitmentTime["4"]?.GetValue<int>());
        Assert.Equal(520, recruitmentTime["5"]?.GetValue<int>());
        Assert.DoesNotContain("6", recruitmentTime.Select(pair => pair.Key), StringComparer.Ordinal);
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
        Assert.False(recruit.Value.ForceRefresh);
        Assert.True(recruit.Value.UseExpedited);
        Assert.True(recruit.Value.ChooseLevel3);
        Assert.False(recruit.Value.ChooseLevel4);
        Assert.True(recruit.Value.ChooseLevel5);
        Assert.True(recruit.Value.ChooseLevel6);
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
            ConnectAddress = "127.0.0.1:5555",
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
        var connectFeatureService = new ConnectFeatureService(session, config);
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
            Assert.True((await runtime.ConnectFeatureService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
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

        Assert.Contains("DynamicResource", startUpView);
        Assert.Contains("DynamicResource", fightView);
        Assert.Contains("DynamicResource", recruitView);
        Assert.Contains("IsVisible=\"{Binding IsMacBundledAdbSupported}\"", startUpView);
        Assert.Contains("IsChecked=\"{Binding MacUseBundledAdb}\"", startUpView);
        Assert.Contains("IsVisible=\"{Binding ShowManualAdbPathControls}\"", startUpView);
        Assert.DoesNotContain("SelectedAttachWindowScreencapOption", startUpView);
        Assert.DoesNotContain("SelectedAttachWindowMouseOption", startUpView);
        Assert.DoesNotContain("SelectedAttachWindowKeyboardOption", startUpView);
        Assert.Contains("IsChecked=\"{Binding ChooseLevel6}\"", recruitView);
        Assert.DoesNotContain("AutoSelectLevel6Notice", recruitView);
    }

    private static string ResolveRepoRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
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
            "Issue.TaskFieldMissing",
            "TaskQueue.Status.ParamsLoaded",
        ];
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
