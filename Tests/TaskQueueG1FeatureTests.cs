using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MAAUnified.App.Features.TaskQueue;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class TaskQueueG1FeatureTests
{
    [Fact]
    public async Task TaskQueuePage_InitializeOnEmptyQueue_ShouldSeedWpfDefaultTasks()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var expected = new[]
        {
            TaskModuleTypes.StartUp,
            TaskModuleTypes.Fight,
            TaskModuleTypes.Infrast,
            TaskModuleTypes.Recruit,
            TaskModuleTypes.Mall,
            TaskModuleTypes.Award,
            TaskModuleTypes.Roguelike,
            TaskModuleTypes.Reclamation,
        };

        Assert.Equal(expected.Length, vm.Tasks.Count);
        Assert.Equal(expected, vm.Tasks.Select(task => task.Type).ToArray());
        Assert.Equal(vm.Tasks.Count, vm.TaskPanels.Count);
        Assert.Equal(expected, vm.TaskPanels.Select(panel => panel.ModuleType).ToArray());
    }

    [Fact]
    public async Task TaskQueuePage_SetLanguage_ShouldKeepTextsAndRootTextsAligned()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                changedProperties.Add(e.PropertyName);
            }
        };
        var generalSettingsBefore = vm.RootTexts["TaskQueue.Root.GeneralSettings"];
        var advancedSettingsBefore = vm.RootTexts["TaskQueue.Root.AdvancedSettings"];

        vm.SetLanguage("en-us");

        Assert.Equal("en-us", vm.Texts.Language);
        Assert.Equal("en-us", vm.RootTexts.Language);
        Assert.Contains(nameof(TaskQueuePageViewModel.Texts), changedProperties);
        Assert.Contains(nameof(TaskQueuePageViewModel.RootTexts), changedProperties);
        Assert.NotEqual(generalSettingsBefore, vm.RootTexts["TaskQueue.Root.GeneralSettings"]);
        Assert.NotEqual(advancedSettingsBefore, vm.RootTexts["TaskQueue.Root.AdvancedSettings"]);
    }

    [Fact]
    public async Task TaskQueuePage_SetLanguage_ShouldRelocalizeDefaultTaskNamesAcrossLocales()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "理智作战")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Recruit, "公開招募")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "生息演算")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "剩余理智")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal("理智作战", vm.Tasks[0].DisplayName);
        Assert.Equal("自动公招", vm.Tasks[1].DisplayName);
        Assert.Equal("生息演算", vm.Tasks[2].DisplayName);
        Assert.Equal("剩余理智", vm.Tasks[3].DisplayName);

        vm.SetLanguage("en-us");

        Assert.Equal("Combat", vm.Tasks[0].DisplayName);
        Assert.Equal("Recruit", vm.Tasks[1].DisplayName);
        Assert.Equal("Reclamation", vm.Tasks[2].DisplayName);
        Assert.Equal("Remaining Sanity", vm.Tasks[3].DisplayName);
    }

    [Fact]
    public async Task TaskQueuePage_SetLanguage_ShouldNotifySelectedModuleTextsForImmediateRefresh()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "生息演算")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        var changedProperties = new List<string>();
        vm.ReclamationModule.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                changedProperties.Add(e.PropertyName);
            }
        };

        vm.SetLanguage("en-us");

        Assert.Contains(nameof(ReclamationModuleViewModel.Texts), changedProperties);
        Assert.Equal("Reclamation", vm.ReclamationModule.Texts["Reclamation.Title"]);
    }

    [Fact]
    public async Task TaskQueuePage_SetLanguage_ShouldRefreshSelectedTaskHostProjection()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.SetLanguage("en-us");

        Assert.Contains(nameof(TaskQueuePageViewModel.SelectedTaskSettingsViewModel), changedProperties);
        Assert.Contains(string.Empty, changedProperties);
    }

    [Fact]
    public async Task TaskQueuePage_SetLanguage_ShouldKeepPreloadedTaskSettingsStable()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "reclamation")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();
        Assert.Equal(1, vm.ReclamationModule.Mode);

        var rawParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        rawParams["mode"] = 0;
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, rawParams)).Success);

        vm.SetLanguage("en-us");
        await vm.WaitForPendingBindingAsync();

        Assert.Equal("en-us", vm.Texts.Language);
        Assert.Equal(1, vm.ReclamationModule.Mode);

        await vm.ReloadTasksAsync();
        Assert.Equal(0, vm.ReclamationModule.Mode);
    }

    [Fact]
    public async Task ReclamationModule_SaveArchiveMode_ShouldNormalizeHiddenClearStore()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "reclamation")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        vm.ReclamationModule.Mode = 1;
        vm.ReclamationModule.ClearStore = true;

        Assert.True(await vm.ReclamationModule.SaveAsync(), vm.ReclamationModule.LastErrorMessage);

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.False(parameters["clear_store"]?.GetValue<bool>());

        var validate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validate.Success, validate.Message);
        Assert.DoesNotContain(
            validate.Value!.Issues,
            issue => issue.Code == "ReclamationClearStoreIgnoredInArchive");
    }

    [Fact]
    public async Task ReclamationModule_RelaunchAnchorTheme_ShouldExposeRaModesOnly()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "reclamation")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        vm.ReclamationModule.Theme = "RelaunchAnchor";

        Assert.Equal(16, vm.ReclamationModule.Mode);
        Assert.Equal([16, 48, 32], vm.ReclamationModule.ModeOptions.Select(option => option.Value).ToArray());
        Assert.False(vm.ReclamationModule.IsArchiveSettingsEnabled);
        Assert.False(vm.ReclamationModule.ShowClearStore);

        vm.ReclamationModule.Mode = 48;
        Assert.Equal(48, vm.ReclamationModule.Mode);

        vm.ReclamationModule.Theme = "Tales";
        Assert.Equal(1, vm.ReclamationModule.Mode);
        Assert.Equal([0, 1], vm.ReclamationModule.ModeOptions.Select(option => option.Value).ToArray());
    }

    [Fact]
    public async Task TaskQueuePage_SetLanguage_ShouldForceOpenTaskSettingsHostToRecreateWithNewTexts()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "reclamation")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        EnsureAvaloniaApplication();
        var view = new ReclamationSettingsView
        {
            DataContext = vm.SelectedTaskSettingsViewModel,
        };
        PrepareView(view);

        var zhTexts = GetRenderedTexts(view);
        Assert.Contains(zhTexts, text => text.Contains("生息演算", StringComparison.Ordinal));
        Assert.Contains("生息演算主题", zhTexts);

        vm.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.SelectedTaskSettingsViewModel), StringComparison.Ordinal))
            {
                view.DataContext = vm.SelectedTaskSettingsViewModel;
            }
        };

        vm.SetLanguage("en-us");
        await vm.WaitForPendingBindingAsync();
        PrepareView(view);

        var enTexts = GetRenderedTexts(view);
        Assert.Contains(enTexts, text => text.Contains("Reclamation", StringComparison.Ordinal));
        Assert.Contains("Reclamation Algorithm Theme", enTexts);
        Assert.DoesNotContain("生息演算主题", enTexts);
    }

    [Fact]
    public async Task FightSettingsView_SetLanguageHostReset_ShouldRefreshGeneralModeLabels()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        EnsureAvaloniaApplication();
        var view = new FightSettingsView
        {
            DataContext = vm.SelectedTaskSettingsViewModel,
        };
        PrepareView(view);

        var zhTexts = GetRenderedTextsAndContent(view);
        Assert.Contains("使用药剂", zhTexts);
        Assert.Contains("使用源石*", zhTexts);
        Assert.Contains("指定次数", zhTexts);
        Assert.Contains("代理倍率", zhTexts);
        Assert.Contains("指定材料", zhTexts);
        Assert.Contains("目标数量", zhTexts);

        vm.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.SelectedTaskSettingsViewModel), StringComparison.Ordinal))
            {
                view.DataContext = vm.SelectedTaskSettingsViewModel;
            }
        };

        vm.SetLanguage("en-us");
        PrepareView(view);

        var enTexts = GetRenderedTextsAndContent(view);
        Assert.Contains("Use Sanity Potion", enTexts);
        Assert.Contains("Use Originium*", enTexts);
        Assert.Contains("Perform battles", enTexts);
        Assert.Contains("Series", enTexts);
        Assert.Contains("Material", enTexts);
        Assert.Contains("Drop count", enTexts);
        Assert.DoesNotContain("使用药剂", enTexts);
        Assert.DoesNotContain("使用源石*", enTexts);
        Assert.DoesNotContain("指定次数", enTexts);
        Assert.DoesNotContain("代理倍率", enTexts);
        Assert.DoesNotContain("指定材料", enTexts);
        Assert.DoesNotContain("目标数量", enTexts);
    }

    [Fact]
    public async Task TaskQueuePage_SetLanguage_ShouldRefreshRootChromeTextProperties()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var zhSelectAll = vm.SelectAllButtonText;
        var zhReclamation = vm.AddTaskMenuReclamationText;

        vm.SetLanguage("en-us");

        Assert.NotEqual(zhSelectAll, vm.SelectAllButtonText);
        Assert.NotEqual(zhReclamation, vm.AddTaskMenuReclamationText);
        Assert.Equal("Select all", vm.SelectAllButtonText);
        Assert.Equal("Reclamation", vm.AddTaskMenuReclamationText);
    }

    [Fact]
    public async Task TaskQueuePage_SelectTask_ShouldSettleDeferredBinding()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Recruit, "recruit")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = vm.Tasks[1];

        Assert.True(vm.CanEditSelectedTaskSettings);

        await vm.WaitForPendingBindingAsync();

        Assert.False(vm.IsSelectedTaskBindingPending);
        Assert.True(vm.CanEditSelectedTaskSettings);
        Assert.True(vm.RecruitModule.IsTaskBound);
    }

    [Fact]
    public async Task TaskQueuePage_RapidSelect_ShouldBindLatestTaskAndClearPendingAfterLatestBinding()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Recruit, "recruit")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = vm.Tasks[0];
        vm.SelectedTask = vm.Tasks[1];

        Assert.True(vm.CanEditSelectedTaskSettings);

        await vm.WaitForPendingBindingAsync();

        Assert.Same(vm.Tasks[1], vm.SelectedTask);
        Assert.Same(vm.TaskPanels[1], vm.SelectedTaskPanel);
        Assert.False(vm.IsSelectedTaskBindingPending);
        Assert.True(vm.RecruitModule.IsTaskBound);
        Assert.True(vm.TaskPanels.All(panel => panel.Module.IsTaskBound));
        Assert.Single(vm.TaskPanels.Where(panel => panel.IsSelected));
    }

    [HostRepoFact]
    public async Task TaskQueuePage_SetLanguage_ShouldKeepRoguelikeCoreCharStableAndRebuildLocalizedOptions()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue")).Success);
        EnsureRoguelikeCoreCharResourceFilesAvailable();

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        var (stableCoreChar, zhDisplayText, enDisplayText) = await ResolveCrossLanguageCoreCharAsync(vm);
        vm.RoguelikeModule.CoreCharDisplayText = zhDisplayText;
        Assert.Equal(stableCoreChar, vm.RoguelikeModule.CoreChar);
        Assert.Equal(zhDisplayText, vm.RoguelikeModule.CoreCharDisplayText);

        vm.SetLanguage("en-us");
        await vm.WaitForPendingBindingAsync();

        Assert.Equal(stableCoreChar, vm.RoguelikeModule.CoreChar);
        Assert.Equal(enDisplayText, vm.RoguelikeModule.CoreCharDisplayText);
        Assert.Contains(enDisplayText, vm.RoguelikeModule.CoreCharNameOptions);
        Assert.DoesNotContain(zhDisplayText, vm.RoguelikeModule.CoreCharNameOptions);
    }

    [HostRepoFact]
    public async Task RoguelikeCoreChar_LegacyDisplayText_ShouldNormalizeToStableValueAfterSave()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue")).Success);
        EnsureRoguelikeCoreCharResourceFilesAvailable();

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        var (stableCoreChar, _, enDisplayText) = await ResolveCrossLanguageCoreCharAsync(vm);
        vm.SetLanguage("en-us");
        await vm.WaitForPendingBindingAsync();

        var rawParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        rawParams["core_char"] = JsonValue.Create(enDisplayText);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, rawParams)).Success);

        await vm.ReloadTasksAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        Assert.Equal(stableCoreChar, vm.RoguelikeModule.CoreChar);
        Assert.Equal(enDisplayText, vm.RoguelikeModule.CoreCharDisplayText);

        Assert.True(await vm.RoguelikeModule.SaveAsync());

        var persisted = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal(stableCoreChar, persisted["core_char"]?.GetValue<string>());
    }

    [HostRepoFact]
    public async Task RoguelikeCoreChar_WhenBattleDataCacheWasPrimedEmpty_ShouldReloadAfterResourcesBecomeAvailable()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue")).Success);
        EnsureRoguelikeCoreCharResourceFilesAvailable();

        var originalCache = GetRoguelikeBattleDataCache();
        SetRoguelikeBattleDataCacheToEmpty();
        try
        {
            var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
            await vm.InitializeAsync();
            vm.SelectedTask = Assert.Single(vm.Tasks);
            await vm.WaitForPendingBindingAsync();

            Assert.NotEmpty(vm.RoguelikeModule.CoreCharOptions);
        }
        finally
        {
            RestoreRoguelikeBattleDataCache(originalCache);
        }
    }

    [Fact]
    public async Task SelectedTask_ShouldProjectTaskConfigVisibilityFlags()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = vm.Tasks[0];
        Assert.True(vm.IsStartUpTaskSelected);
        Assert.False(vm.IsFightTaskSelected);

        vm.SelectedTask = vm.Tasks[1];
        Assert.False(vm.IsStartUpTaskSelected);
        Assert.True(vm.IsFightTaskSelected);
        Assert.False(vm.IsNoTaskSelected);
    }

    [Fact]
    public async Task SelectedTask_SwitchingTasks_ShouldResetSettingsModeToGeneral()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Recruit, "recruit-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();
        Assert.True(vm.CanUseAdvancedSettings);
        Assert.True(vm.ShowSettingsModeSwitch);
        Assert.True(vm.IsGeneralSettingsSelected);

        vm.SelectAdvancedSettingsMode();
        Assert.True(vm.IsAdvancedSettingsSelected);
        Assert.False(vm.IsGeneralSettingsSelected);
        Assert.True(vm.FightModule.IsAdvancedMode);
        Assert.False(vm.FightModule.IsGeneralMode);

        vm.SelectedTask = vm.Tasks[1];
        await vm.WaitForPendingBindingAsync();
        Assert.True(vm.IsGeneralSettingsSelected);
        Assert.False(vm.IsAdvancedSettingsSelected);
        Assert.False(vm.FightModule.IsAdvancedMode);
        Assert.True(vm.FightModule.IsGeneralMode);
    }

    [Fact]
    public async Task SelectedTask_SelectAdvancedSettingsMode_ShouldRaiseModeSelectionPropertyChanges()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();

        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                changedProperties.Add(e.PropertyName);
            }
        };

        vm.SelectAdvancedSettingsMode();

        Assert.Contains(nameof(TaskQueuePageViewModel.IsAdvancedSettingsSelected), changedProperties);
        Assert.Contains(nameof(TaskQueuePageViewModel.IsGeneralSettingsSelected), changedProperties);
    }

    [Fact]
    public async Task SelectedTask_ModuleType_ShouldExposeAdvancedSettingsAvailabilityMatrix()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var moduleTypes = new[]
        {
            TaskModuleTypes.StartUp,
            TaskModuleTypes.Fight,
            TaskModuleTypes.Recruit,
            TaskModuleTypes.Infrast,
            TaskModuleTypes.Mall,
            TaskModuleTypes.Award,
            TaskModuleTypes.Roguelike,
            TaskModuleTypes.Reclamation,
            TaskModuleTypes.UserDataUpdate,
            TaskModuleTypes.Custom,
            TaskModuleTypes.PostAction,
        };

        for (var i = 0; i < moduleTypes.Length; i++)
        {
            Assert.True((await fixture.TaskQueue.AddTaskAsync(moduleTypes[i], $"task-{i}")).Success);
        }

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var expected = new Dictionary<string, (bool canAdvanced, bool showSwitch)>(StringComparer.OrdinalIgnoreCase)
        {
            [TaskModuleTypes.StartUp] = (false, false),
            [TaskModuleTypes.Fight] = (true, true),
            [TaskModuleTypes.Recruit] = (true, true),
            [TaskModuleTypes.Infrast] = (true, true),
            [TaskModuleTypes.Mall] = (true, true),
            [TaskModuleTypes.Award] = (false, false),
            [TaskModuleTypes.Roguelike] = (true, true),
            [TaskModuleTypes.Reclamation] = (true, true),
            [TaskModuleTypes.UserDataUpdate] = (false, false),
            [TaskModuleTypes.Custom] = (false, true),
            [TaskModuleTypes.PostAction] = (false, false),
        };

        foreach (var task in vm.Tasks)
        {
            vm.SelectedTask = task;
            await vm.WaitForPendingBindingAsync();

            var moduleType = TaskModuleTypes.Normalize(task.Type);
            Assert.True(expected.TryGetValue(moduleType, out var expectedState), $"Missing matrix expectation: {moduleType}");
            Assert.Equal(expectedState.canAdvanced, vm.CanUseAdvancedSettings);
            Assert.Equal(expectedState.showSwitch, vm.ShowSettingsModeSwitch);
            Assert.True(vm.IsGeneralSettingsSelected);
            Assert.False(vm.IsAdvancedSettingsSelected);

            vm.SelectAdvancedSettingsMode();
            Assert.Equal(expectedState.canAdvanced, vm.IsAdvancedSettingsSelected);
            Assert.Equal(!expectedState.canAdvanced, vm.IsGeneralSettingsSelected);
        }
    }

    [Fact]
    public async Task SelectedTask_UserDataUpdate_ShouldHideSettingsModeSwitch()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.UserDataUpdate, "user-data-update")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        Assert.True(vm.IsUserDataUpdateTaskSelected);
        Assert.False(vm.CanUseAdvancedSettings);
        Assert.False(vm.ShowSettingsModeSwitch);

        vm.SelectAdvancedSettingsMode();

        Assert.False(vm.IsAdvancedSettingsSelected);
        Assert.True(vm.IsGeneralSettingsSelected);
    }

    [Fact]
    public async Task TaskQueueFeatureService_SetAllAndInvertEnabled_ShouldUpdateWholeQueue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit-c", enabled: true)).Success);

        var disableAll = await fixture.TaskQueue.SetAllTasksEnabledAsync(false);
        Assert.True(disableAll.Success);

        var allDisabled = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(allDisabled.Success);
        Assert.NotNull(allDisabled.Value);
        Assert.All(allDisabled.Value!, task => Assert.False(task.IsEnabled));

        var invert = await fixture.TaskQueue.InvertTasksEnabledAsync();
        Assert.True(invert.Success);

        var allEnabled = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(allEnabled.Success);
        Assert.NotNull(allEnabled.Value);
        Assert.All(allEnabled.Value!, task => Assert.True(task.IsEnabled));
    }

    [Fact]
    public async Task TaskQueuePage_BatchAction_InverseMode_ShouldInvertWholeQueue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.InverseClearMode] = JsonValue.Create("ClearInverse");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.MainFunctionInverseMode] = JsonValue.Create(true);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b", enabled: false)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(vm.ShowBatchModeToggle);
        Assert.Equal(SelectionBatchMode.Inverse, vm.SelectionBatchMode);
        Assert.Equal("反选", vm.BatchActionText);

        await vm.ExecuteBatchActionAsync();
        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.False(queue.Value![0].IsEnabled);
        Assert.True(queue.Value[1].IsEnabled);
    }

    [Fact]
    public async Task TaskQueuePage_BatchAction_ClearMode_ShouldDisableWholeQueue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.InverseClearMode] = JsonValue.Create("ClearInverse");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.MainFunctionInverseMode] = JsonValue.Create(false);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b", enabled: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal(SelectionBatchMode.Clear, vm.SelectionBatchMode);
        Assert.Equal("清空", vm.BatchActionText);
        await vm.ExecuteBatchActionAsync();

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.All(queue.Value!, task => Assert.False(task.IsEnabled));
    }

    [Fact]
    public async Task TaskQueuePage_SelectAll_ShouldOnlyUpdateTaskEnabledState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b", enabled: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();

        vm.StartUpModule.AccountName = "dirty-before-select-all";
        Assert.True(vm.StartUpModule.IsDirty);

        await vm.SelectAllAsync(false);

        Assert.True(vm.StartUpModule.IsDirty);
        Assert.All(vm.Tasks, task => Assert.False(task.IsEnabled));

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.All(queue.Value!, task => Assert.False(task.IsEnabled));
        Assert.NotEqual("dirty-before-select-all", queue.Value![0].Params["account_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task TaskQueuePage_ToggleSelectionBatchMode_ShouldPersistLegacyKeys()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.InverseClearMode] = JsonValue.Create("ClearInverse");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.MainFunctionInverseMode] = JsonValue.Create(false);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Equal(SelectionBatchMode.Clear, vm.SelectionBatchMode);

        await vm.ToggleSelectionBatchModeAsync();

        Assert.Equal(SelectionBatchMode.Inverse, vm.SelectionBatchMode);
        Assert.True(vm.ShowBatchModeToggle);
        Assert.Equal("反选", vm.BatchActionText);
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        Assert.Equal("ClearInverse", profile.Values[ConfigurationKeys.InverseClearMode]?.GetValue<string>());
        Assert.True(profile.Values[ConfigurationKeys.MainFunctionInverseMode]?.GetValue<bool>());
    }

    [Fact]
    public async Task TaskQueuePage_InverseClearModeDisabled_ShouldFallbackToClearAndHideToggle()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.InverseClearMode] = JsonValue.Create("Clear");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.MainFunctionInverseMode] = JsonValue.Create(true);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.False(vm.ShowBatchModeToggle);
        Assert.Equal(SelectionBatchMode.Clear, vm.SelectionBatchMode);
        Assert.Equal("清空", vm.BatchActionText);
    }

    [Fact]
    public async Task TaskQueuePage_InverseClearModeSetToInverse_ShouldUseInverseAndHideToggle()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.InverseClearMode] = JsonValue.Create("Inverse");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.MainFunctionInverseMode] = JsonValue.Create(false);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.False(vm.ShowBatchModeToggle);
        Assert.Equal(SelectionBatchMode.Inverse, vm.SelectionBatchMode);
        Assert.Equal("反选", vm.BatchActionText);
    }

    [Fact]
    public async Task TaskQueuePage_StopAsync_ShouldMarkRunningTasksAsSkipped()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Single(vm.Tasks);
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;

        await vm.StopAsync();

        Assert.Equal(TaskQueueItemStatus.Skipped, vm.Tasks[0].Status);
    }

    [Fact]
    public async Task TaskQueuePage_SelectedTaskSwitch_ShouldFlushDirtyStartUpData()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();

        vm.StartUpModule.AccountName = "dirty-account";
        Assert.True(vm.StartUpModule.IsDirty);

        vm.SelectedTask = vm.Tasks[1];
        await vm.WaitForPendingBindingAsync();

        Assert.True(await vm.FlushConfigurationSavesForCloseAsync());

        var saved = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(saved.Success);
        Assert.Equal("dirty-account", saved.Value?["account_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task TaskQueuePage_MoveSelectedTask_ShouldFlushDirtyDataBeforeMove()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();

        vm.StartUpModule.AccountName = "dirty-before-move";
        Assert.True(vm.StartUpModule.IsDirty);

        await vm.MoveSelectedTaskAsync(1);
        await vm.WaitForPendingBindingAsync();

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.Equal("startup-a", queue.Value![1].Name);
        Assert.Equal("dirty-before-move", queue.Value[1].Params["account_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task TaskQueuePage_MoveSelectedTask_ShouldKeepPanelAndRebindToNewIndex()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();

        var selectedSettings = vm.SelectedTaskSettingsViewModel;

        await vm.MoveSelectedTaskToAsync(1);
        await vm.WaitForPendingBindingAsync();

        Assert.Same(selectedSettings, vm.SelectedTaskSettingsViewModel);
        Assert.Equal("startup-a", vm.Tasks[1].Name);
        Assert.Equal(1, vm.TaskPanels.Single(panel => ReferenceEquals(panel.Task, vm.SelectedTask)).TaskIndex);

        vm.StartUpModule.AccountName = "dirty-after-move";
        Assert.True(await vm.FlushConfigurationSavesForCloseAsync());

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.Equal("dirty-after-move", queue.Value![1].Params["account_name"]?.GetValue<string>());
        Assert.NotEqual("dirty-after-move", queue.Value[0].Params["account_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task TaskQueuePage_RemoveSelectedRoguelikeTask_ShouldNotPersistStaleTypedBinding()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[1];
        await vm.WaitForPendingBindingAsync();

        vm.RoguelikeModule.StartsCount = 2;
        Assert.True(vm.RoguelikeModule.IsDirty);

        await vm.RemoveSelectedTaskAsync();
        await vm.WaitForPendingBindingAsync();
        await Task.Delay(700);

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.Single(queue.Value!);
        Assert.Equal(TaskModuleTypes.StartUp, queue.Value[0].Type);

        var errorLog = File.Exists(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            ? await File.ReadAllTextAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            : string.Empty;
        Assert.DoesNotContain("[TaskQueue.Roguelike.Save]", errorLog, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskTypeMismatch", errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueuePage_RemoveSelectedMallTask_ShouldNotPersistStaleIndexedBinding()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Mall, "mall-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[1];
        await vm.WaitForPendingBindingAsync();

        vm.MallModule.VisitFriends = !vm.MallModule.VisitFriends;

        await vm.RemoveSelectedTaskAsync();
        await vm.WaitForPendingBindingAsync();
        await Task.Delay(700);

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.Single(queue.Value!);
        Assert.Equal(TaskModuleTypes.StartUp, queue.Value[0].Type);

        var errorLog = File.Exists(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            ? await File.ReadAllTextAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            : string.Empty;
        Assert.DoesNotContain("TaskModule.UpdateParams", errorLog, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskNotFound", errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueuePage_MoveSelectedRoguelikeTask_ShouldNotPersistStaleTypedBinding()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[1];
        await vm.WaitForPendingBindingAsync();

        vm.RoguelikeModule.StartsCount = 3;
        Assert.True(vm.RoguelikeModule.IsDirty);

        await vm.MoveSelectedTaskToAsync(0);
        await vm.WaitForPendingBindingAsync();
        await Task.Delay(700);

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.NotNull(queue.Value);
        Assert.Equal(2, queue.Value!.Count);
        Assert.Equal(TaskModuleTypes.Roguelike, queue.Value[0].Type);
        Assert.Equal(3, queue.Value[0].Params["starts_count"]?.GetValue<int>());

        var errorLog = File.Exists(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            ? await File.ReadAllTextAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath)
            : string.Empty;
        Assert.DoesNotContain("[TaskQueue.Roguelike.Save]", errorLog, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskTypeMismatch", errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoguelikeModule_RefreshGuiOptions_ShouldNotDirtyCleanModule()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        Assert.False(vm.RoguelikeModule.IsDirty);

        vm.RoguelikeModule.RefreshGuiDependentOptions();

        Assert.False(vm.RoguelikeModule.IsDirty);
    }

    [Fact]
    public async Task TaskQueuePage_TaskEnabledToggle_ShouldPersistToService()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.WaitForPendingBindingAsync();

        vm.Tasks[0].IsEnabled = false;

        var synced = await WaitForConditionAsync(async () =>
        {
            var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
            return queue.Success && queue.Value is not null && !queue.Value[0].IsEnabled;
        });
        Assert.True(synced);
    }

    [Fact]
    public async Task QueueMutations_SaveAsync_ShouldPersistToDisk()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();
        vm.RenameTargetName = "startup-renamed";
        await vm.RenameSelectedTaskAsync();
        await vm.MoveSelectedTaskAsync(1);
        await vm.SelectAllAsync(false);
        await vm.InverseSelectionAsync();
        await vm.SaveAsync();

        var log = new UiLogService();
        var reloaded = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            fixture.Root);
        await reloaded.LoadOrBootstrapAsync();

        var profile = reloaded.CurrentConfig.Profiles[reloaded.CurrentConfig.CurrentProfile];
        Assert.Equal(2, profile.TaskQueue.Count);
        Assert.Equal("fight-b", profile.TaskQueue[0].Name);
        Assert.Equal("startup-renamed", profile.TaskQueue[1].Name);
        Assert.All(profile.TaskQueue, task => Assert.True(task.IsEnabled));
    }

    [Fact]
    public async Task SelectedTask_RapidSwitch_ShouldNotLoseDirtyData()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "rapid-a";

        vm.SelectedTask = vm.Tasks[1];
        vm.SelectedTask = vm.Tasks[0];
        vm.SelectedTask = vm.Tasks[1];
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "rapid-b";

        vm.SelectedTask = vm.Tasks[0];
        vm.SelectedTask = vm.Tasks[1];
        vm.SelectedTask = vm.Tasks[0];
        await vm.WaitForPendingBindingAsync();

        Assert.True(await vm.FlushConfigurationSavesForCloseAsync());

        var firstParams = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(firstParams.Success);
        Assert.Equal("rapid-a", firstParams.Value?["account_name"]?.GetValue<string>());

        var secondParams = await fixture.TaskQueue.GetTaskParamsAsync(1);
        Assert.True(secondParams.Success);
        Assert.Equal("rapid-b", secondParams.Value?["account_name"]?.GetValue<string>());
    }

    private static async Task<bool> WaitForConditionAsync(Func<Task<bool>> predicate, int retry = 50, int delayMs = 20)
    {
        for (var i = 0; i < retry; i++)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static void PrepareView(Control view)
    {
        EnsureAvaloniaApplication();
        view.Measure(new Size(1280, 900));
        view.Arrange(new Rect(0, 0, 1280, 900));
        Dispatcher.UIThread.RunJobs(null);
    }

    private static IReadOnlyList<string> GetRenderedTexts(Control view)
    {
        Dispatcher.UIThread.RunJobs(null);
        return view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Concat(view.GetLogicalDescendants().OfType<TextBlock>())
            .Select(textBlock => textBlock.Text?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
    }

    private static IReadOnlyList<string> GetRenderedTextsAndContent(Control view)
    {
        Dispatcher.UIThread.RunJobs(null);
        var textBlocks = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Concat(view.GetLogicalDescendants().OfType<TextBlock>())
            .Select(textBlock => textBlock.Text?.Trim());
        var checkBoxes = view.GetVisualDescendants()
            .OfType<CheckBox>()
            .Concat(view.GetLogicalDescendants().OfType<CheckBox>())
            .Select(checkBox => checkBox.Content?.ToString()?.Trim());
        var buttons = view.GetVisualDescendants()
            .OfType<Button>()
            .Concat(view.GetLogicalDescendants().OfType<Button>())
            .Select(button => button.Content?.ToString()?.Trim());

        return textBlocks
            .Concat(checkBoxes)
            .Concat(buttons)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
    }

    private static void EnsureAvaloniaApplication()
    {
        if (Avalonia.Application.Current is not null)
        {
            return;
        }

        AvaloniaTestApplication.Ensure();
    }

    private static async Task<(string StableCoreChar, string ZhDisplayText, string EnDisplayText)> ResolveCrossLanguageCoreCharAsync(TaskQueuePageViewModel vm)
    {
        vm.SetLanguage("zh-cn");
        await vm.WaitForPendingBindingAsync();
        var zhOptions = vm.RoguelikeModule.CoreCharOptions;
        Assert.NotEmpty(zhOptions);

        vm.SetLanguage("en-us");
        await vm.WaitForPendingBindingAsync();
        var enOptionsByType = vm.RoguelikeModule.CoreCharOptions
            .Where(option => !string.IsNullOrWhiteSpace(option.Type))
            .GroupBy(option => option.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.Ordinal);

        var stableCoreChar = string.Empty;
        var zhDisplayText = string.Empty;
        var enDisplayText = string.Empty;
        foreach (var option in zhOptions)
        {
            if (string.IsNullOrWhiteSpace(option.Type)
                || string.IsNullOrWhiteSpace(option.DisplayName)
                || !enOptionsByType.TryGetValue(option.Type, out var localized)
                || string.IsNullOrWhiteSpace(localized)
                || string.Equals(option.DisplayName, localized, StringComparison.Ordinal))
            {
                continue;
            }

            stableCoreChar = option.Type;
            zhDisplayText = option.DisplayName;
            enDisplayText = localized;
            break;
        }

        Assert.False(
            string.IsNullOrWhiteSpace(stableCoreChar),
            "Expected at least one Roguelike CoreChar option whose zh-cn/en-us display text differs.");

        vm.SetLanguage("zh-cn");
        await vm.WaitForPendingBindingAsync();
        return (stableCoreChar, zhDisplayText, enDisplayText);
    }

    private static void EnsureRoguelikeCoreCharResourceFilesAvailable()
    {
        var repoRoot = ResolveRepoRoot();
        var sourceBattleData = Path.Combine(repoRoot, "resource", "battle_data.json");
        var sourceRecruitment = Path.Combine(repoRoot, "resource", "roguelike", "JieGarden", "recruitment.json");
        var targetBattleData = Path.Combine(AppContext.BaseDirectory, "resource", "battle_data.json");
        var targetRecruitment = Path.Combine(AppContext.BaseDirectory, "resource", "roguelike", "JieGarden", "recruitment.json");

        CopyResourceIfMissing(sourceBattleData, targetBattleData);
        CopyResourceIfMissing(sourceRecruitment, targetRecruitment);
    }

    private static void CopyResourceIfMissing(string sourcePath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Required test resource is missing: {sourcePath}");
        }

        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(sourcePath, targetPath, overwrite: false);
    }

    private static object? GetRoguelikeBattleDataCache()
    {
        return GetRoguelikeBattleDataCacheField().GetValue(null);
    }

    private static void SetRoguelikeBattleDataCacheToEmpty()
    {
        var battleDataCacheType = typeof(RoguelikeModuleViewModel).GetNestedType("BattleDataCache", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(RoguelikeModuleViewModel).FullName, "BattleDataCache");
        var emptyField = battleDataCacheType.GetField("Empty", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException(battleDataCacheType.FullName, "Empty");

        GetRoguelikeBattleDataCacheField().SetValue(null, emptyField.GetValue(null));
    }

    private static void RestoreRoguelikeBattleDataCache(object? cache)
    {
        GetRoguelikeBattleDataCacheField().SetValue(null, cache);
    }

    private static FieldInfo GetRoguelikeBattleDataCacheField()
    {
        return typeof(RoguelikeModuleViewModel).GetField("_battleDataCache", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(typeof(RoguelikeModuleViewModel).FullName, "_battleDataCache");
    }

    [Fact]
    public async Task RoguelikeModule_WhenCoreCharCleared_SupportFlagsAreNotPersisted()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new RoguelikeModuleViewModel(fixture.Runtime, new LocalizedTextMap());

        vm.CoreChar = "Amiya";
        vm.UseSupport = true;
        vm.UseNonfriendSupport = true;

        Assert.True(vm.CanUseSupport);
        Assert.True(vm.ShowUseNonfriendSupport);

        vm.CoreChar = string.Empty;

        Assert.False(vm.CanUseSupport);
        Assert.False(vm.UseSupport);
        Assert.False(vm.ShowUseNonfriendSupport);

        var dto = BuildRoguelikeDto(vm);
        Assert.False(dto.UseSupport);
        Assert.False(dto.UseNonfriendSupport);
    }

    private static RoguelikeTaskParamsDto BuildRoguelikeDto(RoguelikeModuleViewModel vm)
    {
        var method = typeof(RoguelikeModuleViewModel).GetMethod("BuildDto", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(typeof(RoguelikeModuleViewModel).FullName, "BuildDto");

        return Assert.IsType<RoguelikeTaskParamsDto>(method.Invoke(vm, null));
    }

    private static string ResolveRepoRoot()
    {
        return TestRepoLayout.GetHostRepoRoot();
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            TaskQueueFeatureService taskQueue,
            MAAUnifiedRuntime runtime,
            FakeBridge bridge)
        {
            Root = root;
            Config = config;
            TaskQueue = taskQueue;
            Runtime = runtime;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public FakeBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync(string language = "zh-cn")
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
            config.CurrentConfig.GlobalValues["GUI.Localization"] = language;

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
            var connectFeatureService = new ConnectFeatureService(session, config);
            var postActionFeatureService = new PostActionFeatureService(
                config,
                diagnostics,
                platform.PostActionExecutorService);

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
                PostActionFeatureService = postActionFeatureService,
            };

            return new TestFixture(root, config, taskQueue, runtime, bridge);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Bridge.DisposeAsync();

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // keep temporary folder for inspection when cleanup fails.
            }
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
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
