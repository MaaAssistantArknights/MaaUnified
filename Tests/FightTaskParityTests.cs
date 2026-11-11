using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

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
    public async Task ConsumeCompletedFightOneShotParams_ShouldResetNullableFlagsToFalse()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "1-7",
            UseMedicine = null,
            Medicine = 5,
            UseStone = null,
            Stone = 2,
            EnableTimesLimit = null,
            Times = 7,
            EnableTargetDrop = null,
            DropId = "30012",
            DropCount = 3,
        })).Success);

        var consumed = await fixture.TaskQueue.ConsumeCompletedFightOneShotParamsAsync(0);

        Assert.True(consumed.Success);
        Assert.Equal(4, consumed.Value);
        var raw = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.False(raw["_ui_use_medicine"]?.GetValue<bool>());
        Assert.False(raw["_ui_use_stone"]?.GetValue<bool>());
        Assert.False(raw["_ui_enable_times_limit"]?.GetValue<bool>());
        Assert.False(raw["_ui_enable_target_drop"]?.GetValue<bool>());

        var compiled = TaskParamCompiler.CompileTask(
            fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue[0],
            fixture.Config.CurrentConfig.Profiles["Default"],
            fixture.Config.CurrentConfig,
            strict: true);
        Assert.Equal(0, compiled.Params["medicine"]?.GetValue<int>());
        Assert.Equal(0, compiled.Params["stone"]?.GetValue<int>());
        Assert.Equal(int.MaxValue, compiled.Params["times"]?.GetValue<int>());
        Assert.Null(compiled.Params["drops"]);
    }

    [Fact]
    public async Task ResetAllFightOneShotParams_ShouldResetEveryFightBeforeExit()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "first")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "second")).Success);

        for (var index = 0; index < 2; index++)
        {
            Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(index, new FightTaskParamsDto
            {
                Stage = "1-7",
                UseMedicine = null,
                UseStone = null,
                EnableTimesLimit = null,
                EnableTargetDrop = null,
                DropId = "30012",
                DropCount = 1,
            })).Success);
        }

        var reset = await fixture.TaskQueue.ResetAllFightOneShotParamsAsync();

        Assert.True(reset.Success);
        Assert.Equal(8, reset.Value);
        for (var index = 0; index < 2; index++)
        {
            var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(index)).Value!;
            Assert.False(parameters["_ui_use_medicine"]?.GetValue<bool>());
            Assert.False(parameters["_ui_use_stone"]?.GetValue<bool>());
            Assert.False(parameters["_ui_enable_times_limit"]?.GetValue<bool>());
            Assert.False(parameters["_ui_enable_target_drop"]?.GetValue<bool>());
        }
    }

    [Fact]
    public void CompileFight_ShouldPreferWpfReportingSettingsAndRemoveDisabledReporterIds()
    {
        var profile = new UnifiedProfile();
        profile.Values["Penguin.EnablePenguin"] = JsonValue.Create(true);
        profile.Values["Yituliu.EnableYituliu"] = JsonValue.Create(true);
        profile.Values["Penguin.Id"] = JsonValue.Create("wpf-penguin-id");
        profile.Values["Start.ClientType"] = JsonValue.Create("YoStarJP");
        profile.Values["EnablePenguin"] = JsonValue.Create(false);
        profile.Values["EnableYituliu"] = JsonValue.Create(false);
        profile.Values["PenguinId"] = JsonValue.Create("legacy-penguin-id");
        profile.Values["YituliuId"] = JsonValue.Create("legacy-yituliu-id");
        profile.Values["ClientType"] = JsonValue.Create("Official");

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto(), profile, new UnifiedConfig());
        var coreParams = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Fight, compiled.Params);

        Assert.True(coreParams["report_to_penguin"]?.GetValue<bool>());
        Assert.True(coreParams["report_to_yituliu"]?.GetValue<bool>());
        Assert.Equal("wpf-penguin-id", coreParams["penguin_id"]?.GetValue<string>());
        Assert.Equal("wpf-penguin-id", coreParams["yituliu_id"]?.GetValue<string>());
        Assert.Equal("JP", coreParams["server"]?.GetValue<string>());

        profile.Values["Penguin.EnablePenguin"] = JsonValue.Create(false);
        profile.Values["Yituliu.EnableYituliu"] = JsonValue.Create(false);
        var disabledCoreParams = TaskParamCompiler.BuildCoreParams(
            TaskModuleTypes.Fight,
            TaskParamCompiler.CompileFight(new FightTaskParamsDto(), profile, new UnifiedConfig()).Params);

        Assert.False(disabledCoreParams["report_to_penguin"]?.GetValue<bool>());
        Assert.False(disabledCoreParams["report_to_yituliu"]?.GetValue<bool>());
        Assert.Null(disabledCoreParams["penguin_id"]);
        Assert.Null(disabledCoreParams["yituliu_id"]);

        var defaults = TaskParamCompiler.CompileFight(new FightTaskParamsDto(), new UnifiedProfile(), new UnifiedConfig());
        Assert.True(defaults.Params["report_to_penguin"]?.GetValue<bool>());
        Assert.True(defaults.Params["report_to_yituliu"]?.GetValue<bool>());
    }

    [Fact]
    public async Task QueueEnabledTasks_FightBlankTargetDrop_ShouldAppendWithoutDrops()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "1-7",
            EnableTargetDrop = true,
            DropId = string.Empty,
            DropCount = 3,
        })).Success);

        var queued = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queued.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var coreParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.False(coreParams.ContainsKey("drops"));
    }

    [Theory]
    [InlineData("Annihilation", "Chernobog@Annihilation")]
    [InlineData("annihilation", "annihilation")]
    [InlineData("ANNIHILATION", "ANNIHILATION")]
    public async Task QueueEnabledTasks_ManualAnnihilationToken_ShouldOnlyRemapExactWpfToken(
        string stage,
        string expectedCoreStage)
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = stage,
            StagePlan = [stage],
            IsStageManually = true,
            UseCustomAnnihilation = true,
            AnnihilationStage = "Chernobog@Annihilation",
        })).Success);

        var queued = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queued.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var coreParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(expectedCoreStage, coreParams["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task FightModule_UseStoneToggle_ShouldResetMedicineToWpfBound()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(
            fixture.Runtime,
            new LocalizedTextMap { Language = "en-us" },
            (_, _) => Task.FromResult(true));

        // Hand-edited configs can hold values above the UI bound; WPF still resets
        // the medicine count to 999 whenever the stone toggle is not false.
        module.Medicine = 1500;
        module.UseStone = null;
        Assert.Equal(999, module.Medicine);

        module.Medicine = 1500;
        Assert.True(await module.RequestAllowUseStoneSaveAsync());
        module.UseStone = true;
        Assert.Equal(999, module.Medicine);
    }

    [Fact]
    public async Task FightModule_DropQuantityVisibility_ShouldFollowWpfIsSpecifiedDrops()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        // WPF binds the drop quantity controls to IsSpecifiedDrops: visible only
        // when EnableTargetDrop is exactly true.
        module.EnableTargetDrop = true;
        Assert.True(module.IsDropQuantityVisible);
        Assert.True(module.IsDropControlsEnabled);

        module.EnableTargetDrop = false;
        Assert.False(module.IsDropQuantityVisible);
        Assert.False(module.IsDropControlsEnabled);

        module.EnableTargetDrop = null;
        Assert.False(module.IsDropQuantityVisible);
        Assert.True(module.IsDropControlsEnabled);
    }

    [Fact]
    public void FightSettingsView_ShouldHideDropQuantityControlsUnlessTargetDropEnabled()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var view = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "FightSettingsView.axaml"));

        Assert.Contains(
            """
            IsVisible="{Binding IsDropQuantityVisible}"
                                   Text="{Binding DropCountLabelText}"
            """.ReplaceLineEndings("\n").Trim(),
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            """
            Value="{Binding DropCount}"
                                                 IsEnabled="{Binding IsDropControlsEnabled}"
                                                 IsVisible="{Binding IsDropQuantityVisible}"
            """.ReplaceLineEndings("\n").Trim(),
            view,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompileFight_BlankCustomAnnihilationStage_ShouldEmitEmptyStageLikeWpf()
    {
        // WPF assigns the configured custom annihilation stage unconditionally once
        // the resolved stage is exactly "Annihilation"; a hand-edited blank override
        // therefore reaches Core as an empty stage.
        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = "Annihilation",
            StagePlan = ["Annihilation"],
            UseCustomAnnihilation = true,
            AnnihilationStage = string.Empty,
        }, new UnifiedProfile(), new UnifiedConfig(), new DateTime(2025, 1, 6, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(string.Empty, compiled.Params["stage"]?.GetValue<string>());
        var coreParams = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Fight, compiled.Params);
        Assert.Equal(string.Empty, coreParams["stage"]?.GetValue<string>());
    }

    [Fact]
    public void FightSettingsView_ShouldUseWpfNumericBounds()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var view = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "FightSettingsView.axaml"));

        Assert.Contains("Maximum=\"999\"\n                                       Value=\"{Binding Times}\"", view, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"1145141919\"\n                                     Value=\"{Binding DropCount}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiImport_FlatDisabledCombat_ShouldNotQueueRemainingSanityFight()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Combat.IsChecked": false,
                  "MainFunction.Stage1": "1-7",
                  "Fight.UseRemainingSanityStage": true,
                  "Fight.RemainingSanityStage": "CE-6"
                }
              }
            }
            """);

        Assert.True((await fixture.Config.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false)).Success);
        var fights = fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue
            .Where(task => task.Type == TaskModuleTypes.Fight)
            .ToArray();
        Assert.All(fights, task => Assert.False(task.IsEnabled));

        var queued = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queued.Success);
        Assert.DoesNotContain(fixture.Bridge.AppendedTasks, static _ => true);
    }

    [Fact]
    public async Task GuiImport_LeadingAnnihilationAlternatePlan_ShouldSplitAndQueueInWpfOrder()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Combat.IsChecked": true,
                  "TaskQueue.Order.Combat": 0,
                  "GUI.UseAlternateStage": true,
                  "MainFunction.Stage1": "Annihilation",
                  "MainFunction.Stage2": "CE-6",
                  "Fight.UseRemainingSanityStage": false,
                  "Fight.RemainingSanityStage": "CE-6"
                }
              }
            }
            """);

        Assert.True((await fixture.Config.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false)).Success);
        var fights = fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue
            .Where(task => task.Type == TaskModuleTypes.Fight)
            .ToArray();
        Assert.Equal(["AnnihilationMode", "Fight", "RemainingSanityStage"], fights.Select(task => task.Name).ToArray());
        Assert.Equal([true, true, false], fights.Select(task => task.IsEnabled).ToArray());

        var queued = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queued.Success);
        var stages = fixture.Bridge.AppendedTasks
            .Select(task => Assert.IsType<JsonObject>(JsonNode.Parse(task.ParamsJson))["stage"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(["Annihilation", "CE-6"], stages);
    }

    [Theory]
    [InlineData("Official", "CN")]
    [InlineData("Bilibili", "CN")]
    [InlineData("YoStarEN", "US")]
    [InlineData("YoStarJP", "JP")]
    [InlineData("YoStarKR", "KR")]
    [InlineData("txwy", "ZH_TW")]
    public void CompileFight_ServerShouldFallBackFromClientType(string clientType, string expectedServer)
    {
        var profile = new UnifiedProfile();
        profile.Values["ClientType"] = JsonValue.Create(clientType);

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto(), profile, new UnifiedConfig());

        Assert.Equal(expectedServer, compiled.Params["server"]?.GetValue<string>());
    }

    [Fact]
    public void CompileFight_TargetDropQuantityMode_ShouldKeepConfiguredDropCount()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"30012\":240}"}""");

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 300,
            IsInventoryTarget = false,
        }, new UnifiedProfile(), config);

        Assert.Empty(compiled.Issues.Where(issue => issue.Blocking));
        var drops = Assert.IsType<JsonObject>(compiled.Params["drops"]);
        Assert.Equal(300, drops["30012"]?.GetValue<int>());
        Assert.False(compiled.Params["_ui_is_inventory_target"]?.GetValue<bool>());
    }

    [Fact]
    public void CompileFight_TargetInventoryMode_ShouldUseDepotDelta()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"30012\":240}"}""");

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 300,
            IsInventoryTarget = true,
        }, new UnifiedProfile(), config);

        Assert.Empty(compiled.Issues.Where(issue => issue.Blocking));
        var drops = Assert.IsType<JsonObject>(compiled.Params["drops"]);
        Assert.Equal(60, drops["30012"]?.GetValue<int>());
        Assert.True(compiled.Params["_ui_is_inventory_target"]?.GetValue<bool>());
    }

    [Fact]
    public void CompileFight_TargetInventoryModeWithoutDepot_ShouldWarnAndOmitDrops()
    {
        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 300,
            IsInventoryTarget = true,
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.DoesNotContain(compiled.Issues, issue => issue.Blocking);
        Assert.Contains(compiled.Issues, issue => issue.Code == "FightInventoryTargetDepotMissing");
        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend");
        Assert.Null(compiled.Params["drops"]);
        Assert.Equal(int.MaxValue, compiled.Params["times"]?.GetValue<int>());
        Assert.Equal("30012", compiled.Params["_ui_drop_id"]?.GetValue<string>());
        Assert.Equal(300, compiled.Params["_ui_drop_count"]?.GetValue<int>());
        Assert.True(compiled.Params["_ui_is_inventory_target"]?.GetValue<bool>());
    }

    [Fact]
    public void CompileFight_TargetInventoryModeMissingItem_ShouldTreatInventoryAsZero()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"30011\":240}"}""");

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 300,
            IsInventoryTarget = true,
        }, new UnifiedProfile(), config);

        Assert.DoesNotContain(compiled.Issues, issue => issue.Blocking);
        Assert.DoesNotContain(compiled.Issues, issue => issue.Code == "FightInventoryTargetItemMissing");
        var drops = Assert.IsType<JsonObject>(compiled.Params["drops"]);
        Assert.Equal(300, drops["30012"]?.GetValue<int>());
    }

    [Fact]
    public void CompileFight_TargetInventoryAlreadyReached_ShouldWarnAndOmitDrops()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"30012\":300}"}""");

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            EnableTargetDrop = true,
            DropId = "30012",
            DropCount = 300,
            IsInventoryTarget = true,
        }, new UnifiedProfile(), config);

        Assert.DoesNotContain(compiled.Issues, issue => issue.Blocking);
        Assert.Contains(compiled.Issues, issue => issue.Code == "FightInventoryTargetReached");
        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend");
        Assert.Null(compiled.Params["drops"]);
        Assert.Equal(int.MaxValue, compiled.Params["times"]?.GetValue<int>());
    }

    [Fact]
    public void CompileFight_UseExpireMedicineForActivity_ShouldExpandMedicineExpireDaysNearActivityEnd()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "resource", "gui"));
        File.WriteAllText(
            Path.Combine(root, "resource", "gui", "StageActivityV2.json"),
            """
            {
              "Official": {
                "sideStoryStage": {
                  "act": {
                    "Activity": {
                      "UtcExpireTime": "2026/07/03 00:00:00",
                      "TimeZone": 0
                    },
                    "Stages": []
                  }
                }
              }
            }
            """);

        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
            {
                UseExpireMedicineForActivity = true,
            }, new UnifiedProfile(), new UnifiedConfig(), new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(5, compiled.Params["medicine_expire_days"]?.GetValue<int>());
            Assert.Equal(9999, compiled.Params["expiring_medicine"]?.GetValue<int>());
            Assert.True(compiled.Params["_ui_use_expire_medicine_for_activity"]?.GetValue<bool>());
            Assert.Equal(2, compiled.Params["_ui_medicine_expire_days"]?.GetValue<int>());

            var task = new UnifiedTaskItem { Type = TaskModuleTypes.Fight, Params = compiled.Params };
            var (roundTrip, issues) = TaskParamCompiler.ReadFight(task, strict: true);
            Assert.Empty(issues);
            Assert.False(roundTrip.UseExpiringMedicine);
            Assert.Equal(2, roundTrip.MedicineExpireDays);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public void CompileFight_ExpiringMedicine_ShouldUseWpfCountAndCoreDayFields()
    {
        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            UseExpiringMedicine = true,
            ExpiringMedicine = 3,
            MedicineExpireDays = 3,
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.Equal(9999, compiled.Params["expiring_medicine"]?.GetValue<int>());
        Assert.Equal(3, compiled.Params["medicine_expire_days"]?.GetValue<int>());

        var task = new UnifiedTaskItem { Type = TaskModuleTypes.Fight, Params = compiled.Params };
        var (roundTrip, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        Assert.True(roundTrip.UseExpiringMedicine);
        Assert.Equal(9999, roundTrip.ExpiringMedicine);
        Assert.Equal(3, roundTrip.MedicineExpireDays);
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
        // Save-time normalization stores the execution-resolved stage. A leading
        // current/last plan entry resolves to the sentinel, which BuildCoreParams
        // sends to Core as an empty stage, matching frozen WPF GetFightStage.
        Assert.Equal(FightStageSelection.CurrentOrLast, task.Params["stage"]?.GetValue<string>());
        var coreParams = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Fight, task.Params);
        Assert.Equal(string.Empty, coreParams["stage"]?.GetValue<string>());
        Assert.True(task.Params["_ui_is_stage_manually"]?.GetValue<bool>());
        Assert.True(task.Params["_ui_use_alternate_stage"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_hide_unavailable_stage"]?.GetValue<bool>());
        Assert.Equal("Ignore", task.Params["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.True(task.Params["_ui_use_weekly_schedule"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_weekly_schedule_monday"]?.GetValue<bool>());
        Assert.Null(task.Params["_ui_use_medicine"]);
        Assert.False(task.Params["_ui_use_stone"]?.GetValue<bool>());
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

        Assert.Equal(FightStageSelection.CurrentOrLast, values[0]);
        Assert.Contains(FightStageSelection.CurrentOrLast, values);
        Assert.Contains("1-7", values);
        Assert.Contains("Annihilation", values);
        Assert.Contains("CE-6", values);
        Assert.DoesNotContain("GT-1", values);
        Assert.InRange(values.Length, 1, 40);
    }

    [Fact]
    public async Task FightModule_WeeklyScheduleAndHideUnavailableStage_ShouldRemainIndependent()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" })
        {
            HideUnavailableStage = true,
            UseWeeklySchedule = true,
        };

        Assert.True(module.HideUnavailableStage);
        Assert.True(module.UseWeeklySchedule);
        Assert.True(module.IsHideUnavailableStageEnabled);
    }

    [Theory]
    [InlineData("ce")]
    [InlineData("ls-6")]
    [InlineData("an")]
    public async Task FightModule_SingleManualStageInput_ShouldPreserveWpfInput(string input)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" })
        {
            IsStageManually = true,
        };

        module.EditableStageText = input;

        Assert.Equal(input, module.Stage);
        Assert.Equal(input, module.EditableStageText);
        Assert.Equal(input, Assert.Single(module.StagePlan).Stage);
    }

    [Theory]
    [InlineData("ce", "CE-6")]
    [InlineData("ls-6", "LS-6")]
    [InlineData("an", "Annihilation")]
    [InlineData("unknown", "unknown")]
    public async Task FightModule_AlternateManualStageInput_ShouldUseWpfAliasAndCandidateMatching(
        string input,
        string expected)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" })
        {
            IsStageManually = true,
            UseAlternateStage = true,
        };

        module.EditableStageText = input;

        Assert.Equal(expected, module.Stage);
        Assert.Equal(expected, Assert.Single(module.StagePlan).Stage);
    }

    [Fact]
    public async Task FightModule_AllowUseStoneSave_ShouldRequireWarningConfirmation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        WarningConfirmDialogRequest? request = null;
        var confirm = false;
        var texts = new LocalizedTextMap { Language = "en-us" };
        var module = new FightTaskModuleViewModel(
            fixture.Runtime,
            texts,
            (nextRequest, _) =>
            {
                request = nextRequest;
                return Task.FromResult(confirm);
            });

        Assert.False(await module.RequestAllowUseStoneSaveAsync());
        Assert.False(module.AllowUseStoneSave);
        Assert.NotNull(request);
        Assert.Equal(DialogTextCatalog.WarningDialogTitle("en-us"), request.Title);
        Assert.Equal(texts["Fight.AllowUseStoneSaveWarning"], request.Message);
        Assert.Equal(DialogTextCatalog.WarningDialogConfirmButton("en-us"), request.ConfirmText);
        Assert.Equal(DialogTextCatalog.WarningDialogCancelButton("en-us"), request.CancelText);

        confirm = true;
        Assert.True(await module.RequestAllowUseStoneSaveAsync());
        Assert.True(module.AllowUseStoneSave);
    }

    [Theory]
    [InlineData("ce")]
    [InlineData("ls-6")]
    [InlineData("an")]
    public async Task FightModule_SingleManualStageInput_ShouldPersistAndAppendWpfCoreStage(string input)
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight")).Success);
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await module.BindAsync(0);

        module.IsStageManually = true;
        module.EditableStageText = input;

        Assert.True(await module.SaveAsync());
        var stored = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal(input, stored["stage"]?.GetValue<string>());
        var storedPlan = Assert.IsType<JsonArray>(stored["_ui_stage_plan"]);
        Assert.Equal(input, Assert.Single(storedPlan)!.GetValue<string>());

        var queued = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queued.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var coreParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(input, coreParams["stage"]?.GetValue<string>());
        Assert.DoesNotContain(coreParams, pair => pair.Key.StartsWith("_ui_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FightModule_AlternateManualActivityDisplayAndCurrentOrLast_ShouldMatchWpfCandidates()
    {
        await using var fixture = await TestFixture.CreateAsync(coreVersion: "v9.9.9");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "cache", "gui"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "cache", "gui", "StageActivityV2.json"),
            """
            {
              "Official": {
                "sideStoryStage": {
                  "AT": {
                    "MinimumRequired": "v1.0.0",
                    "Activity": {
                      "UtcStartTime": "2020/01/01 00:00:00",
                      "UtcExpireTime": "2099/01/01 00:00:00"
                    },
                    "Stages": [
                      { "Display": "Activity Eight", "Value": "AT-8" }
                    ]
                  }
                }
              }
            }
            """);
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" })
        {
            IsStageManually = true,
            UseAlternateStage = true,
        };

        module.EditableStageText = "activity eight";
        Assert.Equal("AT-8", module.Stage);

        module.EditableStageText = string.Empty;
        Assert.Equal(FightStageSelection.CurrentOrLast, module.Stage);
        Assert.Equal(string.Empty, module.EditableStageText);
    }

    [Fact]
    public async Task FightModule_StageOptions_ShouldUseWpfLocalizedPermanentStageDisplay()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "zh-cn" });
        module.HideUnavailableStage = false;

        var ce = Assert.Single(
            module.StageOptions,
            option => string.Equals(option.Value, "CE-6", StringComparison.OrdinalIgnoreCase));
        var chip = Assert.Single(
            module.StageOptions,
            option => string.Equals(option.Value, "PR-A-1", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(AchievementTextCatalog.GetString("CE-6", "zh-cn", "CE-6"), ce.DisplayName);
        Assert.Equal(AchievementTextCatalog.GetString("PR-A-1", "zh-cn", "PR-A-1"), chip.DisplayName);
    }

    [Fact]
    public async Task FightModule_StageOptions_ShouldNotIncludeBulkStageManagerResourceStages()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "resource"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "resource", "stages.json"),
            """
            [
              { "code": "ACT-TEST-1" }
            ]
            """);

        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        module.HideUnavailableStage = false;
        module.RefreshStageOptions(forceReload: true);

        var values = module.StageOptions.Select(option => option.Value).ToArray();
        Assert.DoesNotContain("ACT-TEST-1", values);
        Assert.Contains("1-7", values);
    }

    [Fact]
    public async Task FightModule_StageOptions_ShouldNotInferActivityStagesFromTaskResources()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "resource", "tasks", "Stages"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "resource", "stages.json"),
            """
            [
              { "code": "MT-4", "stageId": "act42side_04_rep" },
              { "code": "MT-10", "stageId": "act42side_10_rep" },
              { "code": "GT-5", "stageId": "act4d0_05_perm" }
            ]
            """);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "resource", "tasks", "Stages", "MT.json"),
            """
            {
              "MT-10": { "algorithm": "JustReturn" },
              "MT-4": { "algorithm": "JustReturn" },
              "MT-OpenOpt": { "algorithm": "JustReturn" },
              "MT-10@SideStoryStage": { "text": ["MT-10"] }
            }
            """);

        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        module.HideUnavailableStage = false;
        module.RefreshStageOptions(forceReload: true);

        var values = module.StageOptions.Select(option => option.Value).ToArray();
        Assert.DoesNotContain("MT-10", values);
        Assert.DoesNotContain("MT-4", values);
        Assert.DoesNotContain("MT-OpenOpt", values);
        Assert.DoesNotContain("GT-5", values);
    }

    [Fact]
    public async Task FightModule_ClosedStageOptions_ShouldKeepSelectedStageButHideItFromTheDropdown()
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

        var hiddenOption = Assert.Single(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
        Assert.False(hiddenOption.IsVisible);
    }

    [Fact]
    public async Task FightModule_ClosedStageOptions_ShouldKeepUnselectedStageInSourceButHideIt()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        var closedStage = ResolveClosedWeeklyStage();

        Assert.NotEqual(closedStage, module.Stage);

        var hiddenOption = Assert.Single(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
        Assert.False(hiddenOption.IsOpen);
        Assert.False(hiddenOption.IsOutdated);
        Assert.False(hiddenOption.IsVisible);
    }

    [Fact]
    public async Task FightModule_MoveStagePlanEntry_ReordersAndSyncsPrimaryStage()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        module.Stage = "LS-6";
        module.AddStagePlanEntry();
        module.StagePlan[1].Stage = "CE-6";
        module.AddStagePlanEntry();
        module.StagePlan[2].Stage = "AP-5";

        module.MoveStagePlanEntry(1, 0);

        Assert.Equal(["CE-6", "LS-6", "AP-5"], module.StagePlan.Select(entry => entry.Stage).ToArray());
        Assert.Equal("CE-6", module.Stage);

        var reordered = module.StagePlan.Select(entry => entry.Stage).ToArray();
        module.MoveStagePlanEntry(-1, 0);
        module.MoveStagePlanEntry(0, -1);
        module.MoveStagePlanEntry(module.StagePlan.Count, 0);
        module.MoveStagePlanEntry(0, module.StagePlan.Count);
        module.MoveStagePlanEntry(0, 0);

        Assert.Equal(reordered, module.StagePlan.Select(entry => entry.Stage).ToArray());
        Assert.Equal("CE-6", module.Stage);
    }

    [Fact]
    public async Task FightModule_SelectedStageValue_ShouldUpdateSelectionAndRestoreAfterTransientEmpty()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        module.HideUnavailableStage = false;
        var moduleChanges = new List<string>();
        module.PropertyChanged += (_, e) => moduleChanges.Add(e.PropertyName ?? string.Empty);

        module.SelectedStageValue = "LS-6";

        Assert.Equal("LS-6", module.Stage);
        Assert.Equal("LS-6", module.SelectedStageOption?.Value);

        module.SelectedStageValue = null!;
        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal("LS-6", module.Stage);
        Assert.Contains(nameof(FightTaskModuleViewModel.SelectedStageValue), moduleChanges);

        moduleChanges.Clear();
        module.SelectedStageValue = string.Empty;
        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal("LS-6", module.Stage);
        Assert.Contains(nameof(FightTaskModuleViewModel.SelectedStageValue), moduleChanges);

        module.UseAlternateStage = true;
        module.AddStagePlanEntry();
        module.StagePlan[1].SelectedStageValue = "CE-6";
        var entryChanges = new List<string>();
        module.StagePlan[1].PropertyChanged += (_, e) => entryChanges.Add(e.PropertyName ?? string.Empty);

        Assert.Equal("CE-6", module.StagePlan[1].Stage);
        Assert.Equal("CE-6", module.StagePlan[1].SelectedStageOption?.Value);

        module.StagePlan[1].SelectedStageValue = null!;
        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal("CE-6", module.StagePlan[1].Stage);
        Assert.Contains(nameof(FightTaskModuleViewModel.StagePlanEntry.SelectedStageValue), entryChanges);

        entryChanges.Clear();
        module.StagePlan[1].SelectedStageValue = string.Empty;
        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal("CE-6", module.StagePlan[1].Stage);
        Assert.Contains(nameof(FightTaskModuleViewModel.StagePlanEntry.SelectedStageValue), entryChanges);
    }

    [Fact]
    public async Task FightModule_AddStagePlanEntry_ShouldRefreshSelectedStageOptions()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        module.HideUnavailableStage = false;
        module.UseAlternateStage = true;
        module.Stage = "LS-6";
        module.AddStagePlanEntry();
        module.StagePlan[1].Stage = "CE-6";
        module.AddStagePlanEntry();
        module.StagePlan[2].Stage = "CA-5";

        module.AddStagePlanEntry();

        Assert.Equal(["LS-6", "CE-6", "CA-5", FightStageSelection.CurrentOrLast], module.StagePlan.Select(entry => entry.Stage).ToArray());
        Assert.Equal("LS-6", module.StagePlan[0].SelectedStageOption?.Value);
        Assert.Equal("CE-6", module.StagePlan[1].SelectedStageOption?.Value);
        Assert.Equal("CA-5", module.StagePlan[2].SelectedStageOption?.Value);
        Assert.Equal(FightStageSelection.CurrentOrLast, module.StagePlan[3].SelectedStageOption?.Value);
        Assert.False(string.IsNullOrWhiteSpace(module.StagePlan[0].PreviewStageText));
        Assert.False(string.IsNullOrWhiteSpace(module.StagePlan[1].PreviewStageText));
        Assert.False(string.IsNullOrWhiteSpace(module.StagePlan[2].PreviewStageText));
        Assert.False(string.IsNullOrWhiteSpace(module.StagePlan[3].PreviewStageText));

        var options = module.StageOptions;
        var entryChanges = new List<string>();
        module.StagePlan[1].PropertyChanged += (_, e) => entryChanges.Add(e.PropertyName ?? string.Empty);
        module.StagePlan[1].SelectedStageOption = null;

        Assert.Equal("CE-6", module.StagePlan[1].Stage);
        Assert.Contains(nameof(FightTaskModuleViewModel.StagePlanEntry.SelectedStageOption), entryChanges);

        entryChanges.Clear();
        module.StagePlan[1].SelectedStageValue = "CA-5";

        Assert.Equal("CA-5", module.StagePlan[1].Stage);
        Assert.Same(options, module.StageOptions);
        Assert.Contains(nameof(FightTaskModuleViewModel.StagePlanEntry.SelectedStageValue), entryChanges);
    }

    [Fact]
    public async Task FightModule_StagePlanSelections_ShouldRecoverAfterStartupOptionRefresh()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        module.HideUnavailableStage = false;
        module.UseAlternateStage = true;
        module.Stage = "MT-8";
        module.AddStagePlanEntry();
        module.StagePlan[1].Stage = "MT-9";
        module.AddStagePlanEntry();
        module.StagePlan[2].Stage = "CA-5";
        var stageOptions = module.StageOptions;
        var selectedOptions = module.StagePlan
            .Select(entry => entry.SelectedStageOption)
            .Where(option => option is not null)
            .ToArray();

        module.RefreshStageOptions(forceReload: true);
        foreach (var entry in module.StagePlan)
        {
            entry.SelectedStageValue = null!;
        }

        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal(["MT-8", "MT-9", "CA-5"], module.StagePlan.Select(entry => entry.Stage).ToArray());
        Assert.Same(stageOptions, module.StageOptions);
        Assert.All(selectedOptions, option => Assert.Contains(module.StageOptions, candidate => ReferenceEquals(candidate, option)));
        Assert.Equal("MT-8", module.SelectedStageValue);
        Assert.Equal(["MT-8", "MT-9", "CA-5"], module.StagePlan.Select(entry => entry.SelectedStageValue).ToArray());
        Assert.All(module.StagePlan, entry => Assert.False(string.IsNullOrWhiteSpace(entry.PreviewStageText)));
    }

    [Fact]
    public async Task FightModule_StageSelections_ShouldRefreshSelectedItemsAfterLanguageAndResourceRefresh()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var texts = new LocalizedTextMap { Language = "en-us" };
        var module = new FightTaskModuleViewModel(fixture.Runtime, texts);
        module.HideUnavailableStage = false;
        module.UseAlternateStage = true;
        module.Stage = "LS-6";
        module.AddStagePlanEntry();
        module.StagePlan[1].Stage = "CE-6";

        var oldPrimary = module.SelectedStageOption;
        var oldAlternate = module.StagePlan[1].SelectedStageOption;
        Assert.NotNull(oldPrimary);
        Assert.NotNull(oldAlternate);

        texts.Language = "zh-cn";
        module.RefreshStageOptions(forceReload: true);
        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal("LS-6", module.Stage);
        Assert.Equal("LS-6", module.SelectedStageValue);
        Assert.Equal("LS-6", module.SelectedStageOption?.Value);
        Assert.NotSame(oldPrimary, module.SelectedStageOption);
        Assert.Equal("CE-6", module.StagePlan[1].Stage);
        Assert.Equal("CE-6", module.StagePlan[1].SelectedStageValue);
        Assert.Equal("CE-6", module.StagePlan[1].SelectedStageOption?.Value);
        Assert.NotSame(oldAlternate, module.StagePlan[1].SelectedStageOption);
        Assert.False(string.IsNullOrWhiteSpace(module.SelectedStageOption?.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(module.StagePlan[1].SelectedStageOption?.DisplayName));
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
        Assert.Equal(closedStage, module.SelectedStageOption?.Value);
        Assert.True(entry.IsClosed);
        Assert.False(entry.IsOutdated);
        Assert.Equal(string.Empty, entry.StatusText);
        var hiddenOption = Assert.Single(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
        Assert.False(hiddenOption.IsVisible);
    }

    [Fact]
    public async Task FightModule_OutdatedSelectedStage_ShouldBePreservedAsOutdatedOption()
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
        var option = Assert.Single(
            module.StageOptions,
            option => string.Equals(option.Value, outdatedStage, StringComparison.OrdinalIgnoreCase));
        Assert.Same(option, module.SelectedStageOption);
        Assert.True(option.IsOutdated);
        Assert.False(option.IsOpen);
        Assert.False(option.IsVisible);
        Assert.Equal(outdatedStage, option.DisplayName);
        Assert.True(entry.IsOutdated);
        Assert.Equal("(Outdated)", entry.StatusText);
    }

    [Fact]
    public async Task FightModule_MoveStagePlanEntry_ShouldPreserveOutdatedStageSelection()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new FightTaskModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        const string outdatedStage = "EXPIRED-ALT-STAGE";

        module.UseAlternateStage = true;
        module.Stage = "LS-6";
        module.AddStagePlanEntry();
        module.StagePlan[1].Stage = outdatedStage;

        module.MoveStagePlanEntry(1, 0);

        Assert.Equal([outdatedStage, "LS-6"], module.StagePlan.Select(entry => entry.Stage).ToArray());
        Assert.Equal(outdatedStage, module.Stage);
        Assert.Contains(
            module.StageOptions,
            option => string.Equals(option.Value, outdatedStage, StringComparison.OrdinalIgnoreCase) && option.IsOutdated);
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

        public static async Task<TestFixture> CreateAsync(string? coreVersion = null)
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
                ToolboxFeatureService = new ToolboxFeatureService(session, connect),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(config, diagnostics, platform.PostActionExecutorService),
                StageManagerFeatureService = new StageManagerFeatureService(
                    config,
                    root,
                    coreVersionResolver: () => coreVersion),
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
