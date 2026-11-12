using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

/// <summary>
/// 任务类型与参数扩展对齐（WPF v6.3.7 → dev-v2）：P1-16 OperProgress、P1-17
/// SwitchTheme、P1-19 Award signinevent。
/// </summary>
public sealed class TaskTypeExpansionFeatureTests
{
    private const string BattleDataFixture = """
        {
          "chars": {
            "char_002_amiya": {
              "name": "阿米娅",
              "name_en": "Amiya",
              "name_jp": "アーミヤ",
              "name_kr": "아미야",
              "name_tw": "阿米婭",
              "profession": "CASTER"
            },
            "char_1001_amiya2": {
              "name": "阿米娅",
              "name_en": "Amiya",
              "name_jp": "アーミヤ",
              "name_kr": "아미야",
              "name_tw": "阿米婭",
              "profession": "WARRIOR"
            },
            "char_010_chen": {
              "name": "陈",
              "name_en": "Ch'en",
              "name_jp": "チェン",
              "name_kr": "첸",
              "name_tw": "陳",
              "profession": "WARRIOR"
            },
            "char_144_robin": {
              "name": "罗宾",
              "name_en": "Robin",
              "name_jp": "ロビン",
              "name_kr": "로빈",
              "name_tw": "羅賓",
              "profession": "SPECIAL"
            }
          }
        }
        """;

    [Fact]
    public void OperNameIndex_NormalizesLocalizedAliasesToCanonicalNamesAndRoles()
    {
        var index = OperNameIndex.FromBattleDataJson(BattleDataFixture);

        Assert.False(index.IsEmpty);
        Assert.Equal("陈", index.NormalizeName("Ch'en"));
        Assert.Equal("陈", index.NormalizeName("チェン"));
        Assert.Equal("陈", index.NormalizeName("첸"));
        Assert.Equal("陈", index.NormalizeName("陳"));
        Assert.Equal("陈", index.NormalizeName("  陈  "));
        Assert.Equal("Warrior", index.ResolveRole("Ch'en"));
        Assert.Equal("Special", index.ResolveRole("robin"));
        Assert.Equal("Robin", index.GetLocalizedDisplayName("罗宾", "en-us"));
        Assert.Equal("罗宾", index.GetLocalizedDisplayName("Robin", "zh-cn"));

        // Unknown input survives verbatim so hand-edited plans are never silently blanked.
        Assert.Equal("UnknownOper", index.NormalizeName("  UnknownOper  "));
        Assert.Equal(OperProgressRoles.Unknown, index.ResolveRole("UnknownOper"));
        Assert.False(index.TryResolve("UnknownOper", out _));
    }

    [Fact]
    public void OperNameIndex_EmptyIndexKeepsNamesUntouched()
    {
        Assert.True(OperNameIndex.Empty.IsEmpty);
        Assert.Equal("Ch'en", OperNameIndex.Empty.NormalizeName("Ch'en"));
        Assert.Equal(OperProgressRoles.Unknown, OperNameIndex.Empty.ResolveRole("Ch'en"));
    }

    [Fact]
    public void SwitchTheme_LegacyConverter_MapsWpfThemeList()
    {
        var legacy = new JsonObject
        {
            ["$type"] = "SwitchThemeTask",
            ["Name"] = "Theme",
            ["IsEnable"] = true,
            ["Themes"] = new JsonArray("罗德岛", string.Empty, " 彩六 "),
        };

        var converted = LegacyTaskSchemaConverter.TryConvertLegacyTask(
            legacy,
            new UnifiedProfile(),
            new UnifiedConfig(),
            out var task,
            out var error);

        Assert.True(converted, error);
        Assert.Equal(TaskModuleTypes.SwitchTheme, task.Type);
        Assert.True(task.IsEnabled);
        Assert.Equal(
            ["罗德岛", "彩六"],
            Assert.IsType<JsonArray>(task.Params["themes"]).Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void OperProgress_LegacyConverter_MapsWpfPlansAndNormalizesLocalizedNames()
    {
        var legacy = new JsonObject
        {
            ["$type"] = "OperProgressTask",
            ["Name"] = "Oper",
            ["IsEnable"] = true,
            ["Plans"] = new JsonArray
            {
                new JsonObject
                {
                    ["Role"] = "Warrior",
                    ["Name"] = "Ch'en",
                    ["Elite"] = 2,
                    ["SkillLevel"] = 7,
                    ["SkillMastery"] = new JsonArray(0, 0, 3),
                    ["ShowRole"] = false,
                },
                new JsonObject
                {
                    ["Role"] = "Special",
                    ["Name"] = "罗宾",
                    ["Elite"] = 0,
                    ["SkillLevel"] = 0,
                    ["SkillMastery"] = new JsonArray(0, 0, 0),
                },
            },
        };
        var operNames = OperNameIndex.FromBattleDataJson(BattleDataFixture);

        var converted = LegacyTaskSchemaConverter.TryConvertLegacyTask(
            legacy,
            new UnifiedProfile(),
            new UnifiedConfig(),
            out var task,
            out var error,
            operNames: operNames);

        Assert.True(converted, error);
        Assert.Equal(TaskModuleTypes.OperProgress, task.Type);
        var plans = Assert.IsType<JsonArray>(task.Params["plans"]);
        Assert.Equal(2, plans.Count);

        var chen = plans[0]!;
        Assert.Equal("Warrior", chen["role"]!.GetValue<string>());
        Assert.Equal("陈", chen["name"]!.GetValue<string>());
        Assert.Equal(2, chen["elite"]!.GetValue<int>());
        Assert.Equal(7, chen["skill_level"]!.GetValue<int>());
        Assert.Equal([0, 0, 3], chen["skill_mastery"]!.AsArray().Select(node => node!.GetValue<int>()));

        var robin = plans[1]!;
        Assert.Equal("Special", robin["role"]!.GetValue<string>());
        Assert.Equal("罗宾", robin["name"]!.GetValue<string>());
        Assert.Null(robin["elite"]);
        Assert.Null(robin["skill_level"]);
        Assert.Null(robin["skill_mastery"]);
    }

    [Fact]
    public void OperProgress_LegacyConverter_AcceptsNumericRolesAndClipboardShape()
    {
        var legacy = new JsonObject
        {
            ["$type"] = "OperProgressTask",
            ["Name"] = "Oper",
            ["Plans"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "unknown",
                    ["name"] = "Robin",
                    ["skill_mastery"] = new JsonArray(1),
                },
                new JsonObject
                {
                    ["Role"] = 5,
                    ["Name"] = "陈",
                },
            },
        };

        var converted = LegacyTaskSchemaConverter.TryConvertLegacyTask(
            legacy,
            new UnifiedProfile(),
            new UnifiedConfig(),
            out var task,
            out var error,
            operNames: OperNameIndex.Empty);

        Assert.True(converted, error);
        var plans = Assert.IsType<JsonArray>(task.Params["plans"]);
        Assert.Equal("Robin", plans[0]!["name"]!.GetValue<string>());
        Assert.Equal(OperProgressRoles.Unknown, plans[0]!["role"]!.GetValue<string>());
        Assert.Equal([1, 0, 0], plans[0]!["skill_mastery"]!.AsArray().Select(node => node!.GetValue<int>()));
        Assert.Equal("Sniper", plans[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddTask_SwitchThemeAndOperProgress_SeedEmptyManagedDefaults()
    {
        await using var fixture = await TestFixture.CreateAsync();

        Assert.True((await fixture.TaskQueue.AddTaskAsync("SwitchThemeTask", "theme")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("OperProgressTask", "oper")).Success);

        var tasks = (await fixture.TaskQueue.GetCurrentTaskQueueAsync()).Value!;
        Assert.Equal(TaskModuleTypes.SwitchTheme, tasks[0].Type);
        Assert.Empty(Assert.IsType<JsonArray>(tasks[0].Params["themes"]));
        Assert.Equal(TaskModuleTypes.OperProgress, tasks[1].Type);
        Assert.Empty(Assert.IsType<JsonArray>(tasks[1].Params["plans"]));
    }

    [Fact]
    public async Task SwitchTheme_EditRoundTrip_KeepsOrderDuplicatesAndDropsBlanks()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SwitchTheme, "theme")).Success);

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        var module = new SwitchThemeModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await module.BindAsync(0, parameters);
        Assert.True(module.ShowEmptyHint);

        module.ThemesText = "罗德岛\r\n\r\n 彩六 ;罗德岛；";
        Assert.True(module.HasThemes);
        Assert.False(module.ShowEmptyHint);
        Assert.True(await module.FlushPendingChangesAsync());

        var persisted = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal(
            ["罗德岛", "彩六", "罗德岛"],
            Assert.IsType<JsonArray>(persisted["themes"]).Select(node => node!.GetValue<string>()));

        var rebound = new SwitchThemeModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await rebound.BindAsync(0, persisted);
        Assert.Equal(
            SwitchThemeModuleViewModel.ParseThemes(module.ThemesText),
            SwitchThemeModuleViewModel.ParseThemes(rebound.ThemesText));
    }

    [Fact]
    public void SwitchTheme_Compile_SkipsEmptyListAndSendsOnlyThemes()
    {
        var empty = TaskParamCompiler.CompileTask(
            new UnifiedTaskItem { Type = TaskModuleTypes.SwitchTheme, Params = new JsonObject() },
            new UnifiedProfile(),
            new UnifiedConfig(),
            strict: true);
        Assert.Contains(empty.Issues, issue => issue.Code == "TaskCompileSkipAppend" && !issue.Blocking);

        var compiled = TaskParamCompiler.CompileTask(
            new UnifiedTaskItem
            {
                Type = TaskModuleTypes.SwitchTheme,
                Params = new JsonObject
                {
                    ["themes"] = new JsonArray(" 罗德岛 ", string.Empty, "彩六"),
                    ["legacy_extra"] = true,
                },
            },
            new UnifiedProfile(),
            new UnifiedConfig(),
            strict: true);

        Assert.DoesNotContain(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend");
        var runtime = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.SwitchTheme, compiled.Params);
        Assert.Equal(
            ["themes"],
            runtime.Select(pair => pair.Key).ToArray());
        Assert.Equal(
            ["罗德岛", "彩六"],
            Assert.IsType<JsonArray>(runtime["themes"]).Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task SwitchTheme_QueueEnabledTasks_AppendsThemesPayload()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SwitchTheme, "theme")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject { ["themes"] = new JsonArray("罗德岛", "彩六") })).Success);

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(result.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("SwitchTheme", appended.Type);
        var parameters = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(
            ["罗德岛", "彩六"],
            Assert.IsType<JsonArray>(parameters["themes"]).Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task SwitchTheme_EmptyListQueueEnabledTasks_AppendsNothing()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SwitchTheme, "theme")).Success);

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(result.Success);
        Assert.Empty(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public async Task OperProgress_EditRoundTripNormalizesNamesRolesAndOrder()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.OperProgress, "oper")).Success);

        var operNames = OperNameIndex.FromBattleDataJson(BattleDataFixture);
        var module = new OperProgressModuleViewModel(
            fixture.Runtime,
            new LocalizedTextMap { Language = "en-us" },
            operNames);
        await module.BindAsync(0, (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!);
        Assert.True(module.ShowEmptyHint);

        module.NewPlanName = "Ch'en";
        Assert.True(module.AddPlan());
        Assert.Single(module.Plans);
        Assert.Equal("陈", module.Plans[0].Name);
        Assert.Equal("Warrior", module.Plans[0].Role);
        Assert.Equal(2, module.Plans[0].Elite);
        Assert.Equal(7, module.Plans[0].SkillLevel);
        Assert.Equal([3, 3, 3], new[] { module.Plans[0].Mastery1, module.Plans[0].Mastery2, module.Plans[0].Mastery3 });

        module.NewPlanName = "Robin";
        module.NewPlanRole = "Special";
        Assert.True(module.AddPlan());
        Assert.Equal("罗宾", module.Plans[1].Name);

        // Out-of-range values are clamped into Core's accepted ranges; clearing every
        // target leaves only role/name in the payload (WPF omits zero targets).
        module.Plans[1].Elite = 9;
        module.Plans[1].SkillLevel = 1;
        module.Plans[1].Mastery1 = 0;
        module.Plans[1].Mastery2 = 0;
        module.Plans[1].Mastery3 = 0;

        module.MovePlan(module.Plans[1], -1);
        Assert.Equal("罗宾", module.Plans[0].Name);
        Assert.False(module.CanMoveUp(module.Plans[0]));
        Assert.False(module.CanMoveDown(module.Plans[1]));

        Assert.True(await module.FlushPendingChangesAsync());
        var persisted = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        var plans = Assert.IsType<JsonArray>(persisted["plans"]);
        Assert.Equal(2, plans.Count);
        Assert.Equal("罗宾", plans[0]!["name"]!.GetValue<string>());
        Assert.Null(plans[0]!["elite"]);
        Assert.Null(plans[0]!["skill_level"]);
        Assert.Null(plans[0]!["skill_mastery"]);
        Assert.Equal("陈", plans[1]!["name"]!.GetValue<string>());
        Assert.Equal("Warrior", plans[1]!["role"]!.GetValue<string>());
        Assert.Equal(2, plans[1]!["elite"]!.GetValue<int>());

        var rebound = new OperProgressModuleViewModel(
            fixture.Runtime,
            new LocalizedTextMap { Language = "en-us" },
            operNames);
        await rebound.BindAsync(0, persisted);
        Assert.Equal(["罗宾", "陈"], rebound.Plans.Select(plan => plan.Name));
        Assert.Equal(0, rebound.Plans[0].Elite);
        Assert.Equal(2, rebound.Plans[1].Elite);

        module.RemovePlan(module.Plans[0]);
        Assert.True(await module.FlushPendingChangesAsync());
        var afterRemove = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Single(Assert.IsType<JsonArray>(afterRemove["plans"]));
    }

    [Fact]
    public void OperProgress_Compile_OmitsZeroTargetsAndDropsInvalidPlans()
    {
        var compiled = TaskParamCompiler.CompileTask(
            new UnifiedTaskItem
            {
                Type = TaskModuleTypes.OperProgress,
                Params = new JsonObject
                {
                    ["plans"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "warrior",
                            ["name"] = "  陈  ",
                            ["elite"] = 0,
                            ["skill_level"] = 0,
                            ["skill_mastery"] = new JsonArray(0, 0, 0),
                        },
                        new JsonObject
                        {
                            ["role"] = "Unknown",
                            ["name"] = "罗宾",
                            ["elite"] = 3,
                            ["skill_level"] = 9,
                            ["skill_mastery"] = new JsonArray(1, 5, 2),
                        },
                        new JsonObject
                        {
                            ["role"] = "Medic",
                            ["name"] = "   ",
                        },
                    },
                },
            },
            new UnifiedProfile(),
            new UnifiedConfig(),
            strict: true);

        Assert.DoesNotContain(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend");
        Assert.Contains(compiled.Issues, issue => issue.Code == "OperProgressPlanSkipped" && !issue.Blocking);
        Assert.Contains(compiled.Issues, issue => issue.Code == "OperProgressTargetNormalized" && !issue.Blocking);

        var runtime = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.OperProgress, compiled.Params);
        var plans = Assert.IsType<JsonArray>(runtime["plans"]);
        Assert.Equal(2, plans.Count);

        var chen = plans[0]!;
        Assert.Equal("陈", chen["name"]!.GetValue<string>());
        Assert.Equal("Warrior", chen["role"]!.GetValue<string>());
        Assert.Null(chen["elite"]);
        Assert.Null(chen["skill_level"]);
        Assert.Null(chen["skill_mastery"]);

        var robin = plans[1]!;
        Assert.Equal("罗宾", robin["name"]!.GetValue<string>());
        Assert.Null(robin["elite"]);
        Assert.Null(robin["skill_level"]);
        Assert.Equal([1, 3, 2], robin["skill_mastery"]!.AsArray().Select(node => node!.GetValue<int>()));
    }

    [Fact]
    public void OperProgress_Compile_EmptyPlansSkipAppend()
    {
        var compiled = TaskParamCompiler.CompileTask(
            new UnifiedTaskItem
            {
                Type = TaskModuleTypes.OperProgress,
                Params = new JsonObject { ["plans"] = new JsonArray() },
            },
            new UnifiedProfile(),
            new UnifiedConfig(),
            strict: true);

        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && !issue.Blocking);
        Assert.Empty(Assert.IsType<JsonArray>(compiled.Params["plans"]));
    }

    [Fact]
    public async Task OperProgress_QueueEnabledTasks_AppendsPlansToCore()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.OperProgress, "oper")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["plans"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "Warrior",
                        ["name"] = "陈",
                        ["elite"] = 2,
                        ["skill_level"] = 7,
                        ["skill_mastery"] = new JsonArray(0, 0, 3),
                    },
                },
            })).Success);

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(result.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("OperProgress", appended.Type);
        var parameters = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        var plan = Assert.Single(Assert.IsType<JsonArray>(parameters["plans"]))!;
        Assert.Equal("Warrior", plan["role"]!.GetValue<string>());
        Assert.Equal("陈", plan["name"]!.GetValue<string>());
        Assert.Equal(2, plan["elite"]!.GetValue<int>());
        Assert.Equal(7, plan["skill_level"]!.GetValue<int>());
        Assert.Equal([0, 0, 3], plan["skill_mastery"]!.AsArray().Select(node => node!.GetValue<int>()));
    }

    [Fact]
    public async Task OperProgress_EmptyPlans_AreSkippedWhenQueueingLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.OperProgress, "oper")).Success);

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(result.Success);
        Assert.Empty(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public async Task TaskQueuePage_ExposesSwitchThemeAndOperProgressPanels()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SwitchTheme, "theme")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.OperProgress, "oper")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal(2, vm.TaskPanels.Count);
        Assert.IsType<SwitchThemeModuleViewModel>(vm.TaskPanels[0].ModuleViewModel);
        Assert.IsType<OperProgressModuleViewModel>(vm.TaskPanels[1].ModuleViewModel);

        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();
        Assert.True(vm.IsSwitchThemeTaskSelected);
        Assert.False(vm.IsOperProgressTaskSelected);
        Assert.False(vm.ShowSettingsModeSwitch);

        vm.SelectedTask = vm.Tasks[1];
        await vm.WaitForPendingBindingAsync();
        Assert.True(vm.IsOperProgressTaskSelected);
        Assert.False(vm.IsSwitchThemeTaskSelected);
        Assert.False(vm.ShowSettingsModeSwitch);
        Assert.Same(vm.TaskPanels[1].ModuleViewModel, vm.SelectedTaskSettingsViewModel);
    }

    [Fact]
    public async Task Infrast_NewParams_EditRoundTripAndCorePayload()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Infrast, "infra")).Success);

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await module.BindAsync(0, parameters);
        await module.WaitForPendingPlanReloadAsync();

        Assert.Equal(
            ["清流", "可露希尔", "但书"],
            new[] { module.FiammettaTarget1, module.FiammettaTarget2, module.FiammettaTarget3 });
        Assert.False(module.FiammettaRecoveryEnabled);
        Assert.True(module.ShowCrossFacilitySettings);
        Assert.False(module.ShowFiammettaTargets);
        Assert.False(module.UseWeeklySchedule);
        Assert.False(module.ShowWeeklyScheduleDays);
        Assert.True(module.WeeklyScheduleMonday);

        module.FiammettaTarget1 = "可露希尔";
        module.FiammettaTarget2 = "可露希尔";
        module.FiammettaTarget3 = string.Empty;
        module.FiammettaRecoveryEnabled = true;
        Assert.True(module.ShowFiammettaTargets);
        module.UsePinusSylvestris = true;
        module.UsePerceptionInformation = true;
        module.UseWorldlyPlight = true;
        module.UseAbyssalHunter = true;
        module.UseWeeklySchedule = true;
        Assert.True(module.ShowWeeklyScheduleDays);
        module.WeeklyScheduleMonday = false;

        Assert.True(await module.FlushPendingChangesAsync());
        var persisted = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal(["可露希尔"], Assert.IsType<JsonArray>(persisted["fiammetta_targets"]).Select(node => node!.GetValue<string>()));
        Assert.True(persisted["fiammetta_recovery_enabled"]!.GetValue<bool>());
        Assert.True(persisted["use_pinus_sylvestris"]!.GetValue<bool>());
        Assert.True(persisted["use_abyssal_hunter"]!.GetValue<bool>());
        Assert.True(persisted["_ui_use_weekly_schedule"]!.GetValue<bool>());
        Assert.False(persisted["_ui_weekly_schedule_monday"]!.GetValue<bool>());
        Assert.True(persisted["_ui_weekly_schedule_sunday"]!.GetValue<bool>());

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(result.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var coreParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(["可露希尔"], Assert.IsType<JsonArray>(coreParams["fiammetta_targets"]).Select(node => node!.GetValue<string>()));
        Assert.True(coreParams["fiammetta_recovery_enabled"]!.GetValue<bool>());
        Assert.True(coreParams["use_perception_information"]!.GetValue<bool>());
        Assert.True(coreParams["use_worldly_plight"]!.GetValue<bool>());
        Assert.DoesNotContain(
            coreParams.Select(pair => pair.Key),
            key => key.StartsWith("_ui_", StringComparison.Ordinal));

        var rebound = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await rebound.BindAsync(0, persisted);
        await rebound.WaitForPendingPlanReloadAsync();
        Assert.Equal("可露希尔", rebound.FiammettaTarget1);
        Assert.Equal(string.Empty, rebound.FiammettaTarget3);
        Assert.True(rebound.UseAbyssalHunter);
        Assert.True(rebound.UseWeeklySchedule);
        Assert.False(rebound.WeeklyScheduleMonday);
    }

    [Fact]
    public async Task Infrast_WeeklySchedule_SkipsDisabledDayLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Infrast, "infra")).Success);

        var currentDay = MallDailyResetHelper.GetYjDate(DateTime.UtcNow, "Official").DayOfWeek;
        var parameters = new InfrastParams
        {
            Mode = 0,
            UseWeeklySchedule = true,
            WeeklyScheduleSunday = currentDay != DayOfWeek.Sunday,
            WeeklyScheduleMonday = currentDay != DayOfWeek.Monday,
            WeeklyScheduleTuesday = currentDay != DayOfWeek.Tuesday,
            WeeklyScheduleWednesday = currentDay != DayOfWeek.Wednesday,
            WeeklyScheduleThursday = currentDay != DayOfWeek.Thursday,
            WeeklyScheduleFriday = currentDay != DayOfWeek.Friday,
            WeeklyScheduleSaturday = currentDay != DayOfWeek.Saturday,
        }.ToJson();
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, parameters)).Success);

        var skipped = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(skipped.Success);
        Assert.Empty(fixture.Bridge.AppendedTasks);

        // Enabling today's entry appends the task again (schedule is the only gate).
        parameters = InfrastParams.FromJson(parameters).ToJson();
        switch (currentDay)
        {
            case DayOfWeek.Sunday: parameters["_ui_weekly_schedule_sunday"] = true; break;
            case DayOfWeek.Monday: parameters["_ui_weekly_schedule_monday"] = true; break;
            case DayOfWeek.Tuesday: parameters["_ui_weekly_schedule_tuesday"] = true; break;
            case DayOfWeek.Wednesday: parameters["_ui_weekly_schedule_wednesday"] = true; break;
            case DayOfWeek.Thursday: parameters["_ui_weekly_schedule_thursday"] = true; break;
            case DayOfWeek.Friday: parameters["_ui_weekly_schedule_friday"] = true; break;
            case DayOfWeek.Saturday: parameters["_ui_weekly_schedule_saturday"] = true; break;
        }

        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, parameters)).Success);
        var queued = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queued.Success);
        Assert.Single(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public void TaskQueueViews_RegisterNewTaskPanelsAndBindToViewModels()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var taskQueueView = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));
        Assert.Contains("taskVm:SwitchThemeModuleViewModel", taskQueueView);
        Assert.Contains("taskViews:SwitchThemeSettingsView", taskQueueView);
        Assert.Contains("taskVm:OperProgressModuleViewModel", taskQueueView);
        Assert.Contains("taskViews:OperProgressSettingsView", taskQueueView);

        var switchThemeView = File.ReadAllText(
            Path.Combine(root, "App", "Features", "TaskQueue", "SwitchThemeSettingsView.axaml"));
        Assert.Contains("{Binding ThemesText, UpdateSourceTrigger=LostFocus}", switchThemeView);
        Assert.Contains("{Binding Texts[SwitchTheme.ThemesTip]}", switchThemeView);
        Assert.DoesNotContain("主题名称", switchThemeView);

        var operProgressView = File.ReadAllText(
            Path.Combine(root, "App", "Features", "TaskQueue", "OperProgressSettingsView.axaml"));
        Assert.Contains("ItemsSource=\"{Binding Plans}\"", operProgressView);
        Assert.Contains("{Binding Name, UpdateSourceTrigger=LostFocus}", operProgressView);
        Assert.Contains("{Binding Elite}", operProgressView);
        Assert.Contains("{Binding SkillLevel}", operProgressView);
        Assert.Contains("{Binding Mastery1}", operProgressView);
        Assert.Contains("{Binding Role}", operProgressView);
        Assert.Contains("OnAddPlanClick", operProgressView);
        Assert.DoesNotContain("<CheckBox Content=", operProgressView, StringComparison.Ordinal);

        var awardView = File.ReadAllText(
            Path.Combine(root, "App", "Features", "TaskQueue", "AwardSettingsView.axaml"));
        Assert.Contains("{Binding SignInEvent}", awardView);

        var infrastView = File.ReadAllText(
            Path.Combine(root, "App", "Features", "TaskQueue", "InfrastSettingsView.axaml"));
        Assert.Contains("{Binding FiammettaRecoveryEnabled}", infrastView);
        Assert.Contains("{Binding FiammettaTarget1}", infrastView);
        Assert.Contains("{Binding UsePinusSylvestris}", infrastView);
        Assert.Contains("{Binding UseAbyssalHunter}", infrastView);
        Assert.Contains("{Binding UseWeeklySchedule}", infrastView);
        Assert.Contains("{Binding WeeklyScheduleMonday}", infrastView);
    }

    [Fact]
    public void Localization_HasTaskTypeExpansionKeys_ForAllSupportedLanguages()
    {
        var text = new LocalizedTextMap();
        var languages = new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr" };
        var requiredKeys = new[]
        {
            "TaskQueue.Module.SwitchTheme",
            "TaskQueue.Module.OperProgress",
            "SwitchTheme.Title",
            "SwitchTheme.Themes",
            "SwitchTheme.ThemesTip",
            "SwitchTheme.Placeholder",
            "SwitchTheme.EmptyHint",
            "OperProgress.Title",
            "OperProgress.Plans",
            "OperProgress.OperatorName",
            "OperProgress.OperatorNameTip",
            "OperProgress.AddPlan",
            "OperProgress.NameRequired",
            "OperProgress.MoveUp",
            "OperProgress.MoveDown",
            "OperProgress.Remove",
            "OperProgress.EmptyHint",
            "OperProgress.NotSet",
            "OperProgress.Mastery1",
            "OperProgress.Mastery2",
            "OperProgress.Mastery3",
            "OperProgress.UnknownOperator",
            "Game.Role.Pioneer",
            "Game.Role.Warrior",
            "Game.Role.Tank",
            "Game.Role.Sniper",
            "Game.Role.Caster",
            "Game.Role.Medic",
            "Game.Role.Support",
            "Game.Role.Special",
            "Award.SignInEvent",
            "Award.SignInEventTip",
            "TaskQueue.Module.DepotMaintain",
            "DepotMaintain.Title",
            "DepotMaintain.Plans",
            "DepotMaintain.AddPlan",
            "DepotMaintain.EmptyHint",
            "DepotMaintain.UpdateDepot",
            "DepotMaintain.SkipDuringActivity",
            "DepotMaintain.SkipDuringResourceCollection",
            "DepotMaintain.OnlyFirstInsufficientPlan",
            "DepotMaintain.SortByProgress",
            "DepotMaintain.CountDualChipAsChipPack",
            "DepotMaintain.UseAutoSeries",
            "DepotMaintain.EnableUseMedicine",
            "DepotMaintain.EnableUseStone",
            "DepotMaintain.UseExpiringMedicine",
            "DepotMaintain.Stage",
            "DepotMaintain.Drop",
            "DepotMaintain.DropCount",
            "DepotMaintain.MedicineCount",
            "DepotMaintain.StoneCount",
            "DepotMaintain.PlanUseMedicine",
            "DepotMaintain.PlanUseStone",
            "DepotMaintain.Inventory",
            "DepotMaintain.NotSelected",
            "DepotMaintain.Remove",
            "DepotMaintain.MoveUp",
            "DepotMaintain.MoveDown",
        };

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
    public async Task ExpandDepotMaintain_BuildsDepotThenFightChains()
    {
        // WPF dev-v2 38b2e9e4f2: DepotMaintain expands into 1 Depot chain + 1 Fight chain
        // per insufficient plan, with drops = the missing amount and times = int.MaxValue.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var parameters = new DepotMaintainParams
        {
            UpdateDepot = true,
            Plans =
            [
                new DepotMaintainPlan { Stage = "1-7", DropId = "30012", DropCount = 10, UseMedicine = true, MedicineCount = 2 },
                new DepotMaintainPlan { Stage = "1-7", DropId = "30013", DropCount = 5 },
            ],
        }.ToJson();
        var depotCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["30012"] = 4 };

        var expansion = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            parameters,
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            depotCounts);

        Assert.False(expansion.Skipped);
        Assert.Equal(3, expansion.Tasks.Count);
        Assert.Equal("Depot", expansion.Tasks[0].Type);
        Assert.Equal("Fight", expansion.Tasks[1].Type);
        Assert.Equal("Fight", expansion.Tasks[2].Type);

        var firstFight = Assert.IsType<JsonObject>(JsonNode.Parse(expansion.Tasks[1].ParamsJson));
        Assert.Equal("1-7", firstFight["stage"]!.GetValue<string>());
        Assert.Equal(6, firstFight["drops"]!["30012"]!.GetValue<int>());
        Assert.Equal(int.MaxValue, firstFight["times"]!.GetValue<int>());
        Assert.Equal(1, firstFight["series"]!.GetValue<int>());
        Assert.Equal(2, firstFight["medicine"]!.GetValue<int>());
        Assert.Equal(0, firstFight["expiring_medicine"]!.GetValue<int>());

        var secondFight = Assert.IsType<JsonObject>(JsonNode.Parse(expansion.Tasks[2].ParamsJson));
        Assert.Equal(5, secondFight["drops"]!["30013"]!.GetValue<int>());
        Assert.Equal(2, expansion.PlanDecisions.Count(decision => decision.Outcome == DepotMaintainPlanOutcome.Enqueued));
    }

    [Fact]
    public async Task ExpandDepotMaintain_AutoSeriesAndExpiringMedicine()
    {
        // WPF 0e99c81442 (Series = UseAutoSeries ? 0 : 1) and 948f23f8ae (expiring
        // medicine window = 2 days, expiring_medicine = 9999).
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var parameters = new DepotMaintainParams
        {
            UpdateDepot = false,
            UseAutoSeries = true,
            UseExpiringMedicine = true,
            Plans = [new DepotMaintainPlan { Stage = "1-7", DropId = "30012", DropCount = 5 }],
        }.ToJson();

        var expansion = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            parameters,
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            new Dictionary<string, int>(StringComparer.Ordinal) { ["30012"] = 1 });

        var fight = Assert.Single(expansion.Tasks);
        var fightParams = Assert.IsType<JsonObject>(JsonNode.Parse(fight.ParamsJson));
        Assert.Equal(0, fightParams["series"]!.GetValue<int>());
        Assert.Equal(9999, fightParams["expiring_medicine"]!.GetValue<int>());
        Assert.Equal(DepotMaintainParams.ExpiringMedicineDays, fightParams["medicine_expire_days"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExpandDepotMaintain_EmptyPlans_OnlyRefreshesDepotOrSkips()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

        var refreshOnly = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            new DepotMaintainParams { UpdateDepot = true }.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            new Dictionary<string, int>(StringComparer.Ordinal));
        Assert.False(refreshOnly.Skipped);
        var depotTask = Assert.Single(refreshOnly.Tasks);
        Assert.Equal("Depot", depotTask.Type);

        var nothing = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            new DepotMaintainParams { UpdateDepot = false }.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            new Dictionary<string, int>(StringComparer.Ordinal));
        Assert.True(nothing.Skipped);
        Assert.Equal(DepotMaintainSkipReason.NoPlans, nothing.SkipReason);
        Assert.Empty(nothing.Tasks);
    }

    [Fact]
    public async Task ExpandDepotMaintain_SkipsWholeTaskDuringActivityOrResourceCollection()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var parameters = new DepotMaintainParams
        {
            UpdateDepot = true,
            SkipDuringActivity = true,
            SkipDuringResourceCollection = true,
            Plans = [new DepotMaintainPlan { Stage = "1-7", DropId = "30012", DropCount = 5 }],
        }.ToJson();

        var activityState = BuildStageState(
            utcNow,
            new StageActivityStage(
                "CW-5",
                "CW-5",
                null,
                new StageActivityWindow("tip", "CW", utcNow.AddHours(-1), utcNow.AddHours(24))));
        var skippedForActivity = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            parameters,
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            activityState,
            new Dictionary<string, int>(StringComparer.Ordinal));
        Assert.True(skippedForActivity.Skipped);
        Assert.Equal(DepotMaintainSkipReason.ActivityOpen, skippedForActivity.SkipReason);
        Assert.Empty(skippedForActivity.Tasks);

        var resourceState = BuildStageState(
            utcNow,
            new StageActivityStage(
                "resource",
                "resource",
                null,
                new StageActivityWindow("tip", "resource", utcNow.AddHours(-1), utcNow.AddHours(24), IsResourceCollection: true)));
        var skippedForResource = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            parameters,
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            resourceState,
            new Dictionary<string, int>(StringComparer.Ordinal));
        Assert.True(skippedForResource.Skipped);
        Assert.Equal(DepotMaintainSkipReason.ResourceCollectionOpen, skippedForResource.SkipReason);
        Assert.Empty(skippedForResource.Tasks);
    }

    [Fact]
    public async Task ExpandDepotMaintain_OnlyFirstInsufficientPlanStopsAfterFirstDispatch()
    {
        // WPF e7ef27745e: only the first insufficient plan is dispatched; later plans wait
        // for the next run.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var parameters = new DepotMaintainParams
        {
            UpdateDepot = true,
            OnlyFirstInsufficientPlan = true,
            Plans =
            [
                new DepotMaintainPlan { Stage = "1-7", DropId = "30012", DropCount = 5 },
                new DepotMaintainPlan { Stage = "1-7", DropId = "30013", DropCount = 5 },
            ],
        }.ToJson();

        var expansion = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            parameters,
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.Equal(2, expansion.Tasks.Count);
        Assert.Equal("Depot", expansion.Tasks[0].Type);
        Assert.Equal("Fight", expansion.Tasks[1].Type);
        Assert.Single(expansion.PlanDecisions, decision => decision.Outcome == DepotMaintainPlanOutcome.Enqueued);
    }

    [Fact]
    public async Task ExpandDepotMaintain_NoInventoryDataWithoutRefreshStopsFarming()
    {
        // WPF 0302381585: without inventory data farming stops. The task-local depot refresh
        // keeps the plans queued (WPF precheck falls back to a 0 cache), so the same task with
        // UpdateDepot still dispatches both plans.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var plans = new List<DepotMaintainPlan>
        {
            new() { Stage = "1-7", DropId = "30012", DropCount = 5 },
            new() { Stage = "1-7", DropId = "30013", DropCount = 5 },
        };

        var withoutRefresh = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            new DepotMaintainParams { UpdateDepot = false, Plans = plans }.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            new Dictionary<string, int>(StringComparer.Ordinal));
        Assert.True(withoutRefresh.Skipped);
        Assert.Empty(withoutRefresh.Tasks);
        Assert.Equal(2, withoutRefresh.PlanDecisions.Count(decision => decision.Outcome == DepotMaintainPlanOutcome.InventoryDataMissing));

        var withRefresh = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            new DepotMaintainParams { UpdateDepot = true, Plans = plans }.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            new Dictionary<string, int>(StringComparer.Ordinal));
        Assert.False(withRefresh.Skipped);
        Assert.Equal(3, withRefresh.Tasks.Count);
    }

    [Fact]
    public async Task ExpandDepotMaintain_CountDualChipAsChipPackUsesFormulaIndex()
    {
        // WPF be9cc84fd2: dual chips count toward the chip-pack target through their
        // crafting recipe (each dual chip consumes 2 chip packs).
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var depotCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["3263"] = 4 };
        var formulas = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
        {
            ["3263"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [ItemFormulaIndex.DualChipCatalystItemId] = 1,
                ["3262"] = 2,
            },
        };
        var plan = new DepotMaintainParams
        {
            UpdateDepot = false,
            Plans = [new DepotMaintainPlan { Stage = "1-7", DropId = "3262", DropCount = 10 }],
        };

        var plain = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            plan.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            depotCounts,
            itemFormulas: formulas);
        var plainFight = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(plain.Tasks).ParamsJson));
        Assert.Equal(10, plainFight["drops"]!["3262"]!.GetValue<int>());

        plan.CountDualChipAsChipPack = true;
        var converted = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            plan.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            depotCounts,
            itemFormulas: formulas);
        var convertedFight = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(converted.Tasks).ParamsJson));
        Assert.Equal(2, convertedFight["drops"]!["3262"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExpandDepotMaintain_SortByProgressDispatchesLowestAchievementFirst()
    {
        // WPF a7ee2c2bf7: SortByProgress orders the dispatched plans (not the stored list)
        // by achievement rate ascending.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var utcNow = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var depotCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["30012"] = 80 };
        var plans = new List<DepotMaintainPlan>
        {
            new() { Stage = "1-7", DropId = "30012", DropCount = 100 },
            new() { Stage = "1-7", DropId = "30013", DropCount = 100 },
        };

        var parameters = new DepotMaintainParams { UpdateDepot = false, SortByProgress = true, Plans = plans };
        var sorted = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            parameters.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            depotCounts);
        var firstSorted = Assert.IsType<JsonObject>(JsonNode.Parse(sorted.Tasks[0].ParamsJson));
        Assert.Equal(100, firstSorted["drops"]!["30013"]!.GetValue<int>());

        parameters.SortByProgress = false;
        var unsorted = TaskParamCompiler.ExpandDepotMaintain(
            "depot",
            parameters.ToJson(),
            profile,
            fixture.Config.CurrentConfig,
            utcNow,
            BuildStageState(utcNow),
            depotCounts);
        var firstUnsorted = Assert.IsType<JsonObject>(JsonNode.Parse(unsorted.Tasks[0].ParamsJson));
        Assert.Equal(20, firstUnsorted["drops"]!["30012"]!.GetValue<int>());
    }

    [Fact]
    public async Task AppendTasksFromCurrentProfile_DepotMaintainExpandsChainsAndMapsIds()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.DepotMaintain, "depot-maintain")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new DepotMaintainParams
            {
                UpdateDepot = true,
                Plans = [new DepotMaintainPlan { Stage = "1-7", DropId = "4001", DropCount = 500 }],
            }.ToJson())).Success);

        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.DepotResult] = JsonValue.Create("""{"4001":100}""");
        Assert.True((await fixture.Runtime.SessionService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        var appended = await fixture.Runtime.SessionService.AppendTasksFromCurrentProfileAsync();

        Assert.True(appended.Success);
        Assert.Equal(2, appended.Value);
        Assert.Equal(2, fixture.Bridge.AppendedTasks.Count);
        Assert.Equal("Depot", fixture.Bridge.AppendedTasks[0].Type);
        Assert.Equal("Fight", fixture.Bridge.AppendedTasks[1].Type);
        var fightParams = Assert.IsType<JsonObject>(JsonNode.Parse(fixture.Bridge.AppendedTasks[1].ParamsJson));
        Assert.Equal(400, fightParams["drops"]!["4001"]!.GetValue<int>());
        Assert.Equal(int.MaxValue, fightParams["times"]!.GetValue<int>());
        Assert.Equal([1, 2], fixture.Runtime.SessionService.GetCoreTaskIdsForQueueIndex(0));
    }

    private static StageActivityState BuildStageState(DateTime utcNow, params StageActivityStage[] extraStages)
    {
        var stages = new List<StageActivityStage>(StageActivityStage.CreatePermanentStages());
        stages.AddRange(extraStages);
        return new StageActivityState("Official", stages, utcNow, false);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            UnifiedSessionService session,
            TaskQueueFeatureService taskQueue,
            MAAUnifiedRuntime runtime,
            FakeBridge bridge)
        {
            Root = root;
            Config = config;
            Session = session;
            TaskQueue = taskQueue;
            Runtime = runtime;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public UnifiedSessionService Session { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public FakeBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync(string language = "en-us")
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
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
            config.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create(language);

            var bridge = new FakeBridge();
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
            var lifecycle = new NoOpAppLifecycleService();
            var postAction = new PostActionFeatureService(
                config,
                diagnostics,
                platform.PostActionExecutorService,
                bridge,
                lifecycle,
                new NoOpPostActionPromptService(),
                session,
                hasOtherMaaProcess: null);
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
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = postAction,
                AppLifecycleService = lifecycle,
            };
            return new TestFixture(root, config, session, taskQueue, runtime, bridge);
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary folder.
            }
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _taskId;

        public List<CoreTaskRequest> AppendedTasks { get; } = [];

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendedTasks.Add(task);
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
            await foreach (var callback in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
