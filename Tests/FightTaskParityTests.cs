using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.TaskQueue;
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
            Assert.Equal(5, compiled.Params["expiring_medicine"]?.GetValue<int>());
            Assert.True(compiled.Params["_ui_use_expire_medicine_for_activity"]?.GetValue<bool>());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
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

        Assert.Equal(FightStageSelection.CurrentOrLast, values[0]);
        Assert.Contains(FightStageSelection.CurrentOrLast, values);
        Assert.Contains("1-7", values);
        Assert.Contains("Annihilation", values);
        Assert.Contains("CE-6", values);
        Assert.DoesNotContain("GT-1", values);
        Assert.InRange(values.Length, 1, 40);
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
    public async Task FightModule_StageOptions_ShouldIncludeActiveTaskStageFallbacks()
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
        Assert.Contains("MT-10", values);
        Assert.Contains("MT-4", values);
        Assert.DoesNotContain("MT-OpenOpt", values);
        Assert.DoesNotContain("GT-5", values);
        Assert.True(Array.IndexOf(values, "MT-10") < Array.IndexOf(values, "1-7"));
        Assert.True(Array.IndexOf(values, "MT-10") < Array.IndexOf(values, "MT-4"));
    }

    [Fact]
    public async Task FightModule_ClosedStageOptions_ShouldKeepSelectedStageVisibleWhenUnavailableStagesAreHidden()
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

        Assert.Contains(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains(
            module.StageOptions,
            option => string.Equals(option.Value, closedStage, StringComparison.OrdinalIgnoreCase));
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
                StageManagerFeatureService = new StageManagerFeatureService(config, root),
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
