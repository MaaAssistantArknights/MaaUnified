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
    [Theory]
    [InlineData("0", 0, true)]
    [InlineData("1", 1, true)]
    [InlineData("6", 6, true)]
    [InlineData("7", 7, false)]
    public async Task TaskQueueGuide_VisibilityFollowsPersistedStep(
        string persistedStep,
        int expectedStep,
        bool expectedGuideVisible)
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] =
            JsonValue.Create(persistedStep);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] =
            JsonValue.Create(2);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.Equal(expectedStep, vm.GuideStepIndex);
        Assert.Equal(expectedGuideVisible, vm.IsGuideVisible);
        Assert.Equal(!expectedGuideVisible, vm.IsTaskQueueContentVisible);
        if (expectedStep < 7)
        {
            Assert.True(vm.IsGuideStep0Visible || vm.IsGuideStep1Visible || vm.IsGuideStep6Visible);
        }
    }

    [Fact]
    public async Task TaskQueueGuide_NextPreviousAndDonePersistStep()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("1");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True(await vm.PreviousGuideStepAsync());
        Assert.Equal(0, vm.GuideStepIndex);
        Assert.False(vm.CanGoToPreviousGuideStep);
        Assert.Equal(0, fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<int>());

        Assert.True(await vm.NextGuideStepAsync());
        Assert.Equal(1, vm.GuideStepIndex);
        Assert.True(vm.CanGoToPreviousGuideStep);
        Assert.Equal(1, fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<int>());

        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("6");
        var finalStepVm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        Assert.True(finalStepVm.IsGuideDoneVisible);
        Assert.False(finalStepVm.IsGuideNextVisible);

        Assert.True(await finalStepVm.CompleteGuideAsync());
        Assert.Equal(7, finalStepVm.GuideStepIndex);
        Assert.False(finalStepVm.IsGuideVisible);
        Assert.True(finalStepVm.IsTaskQueueContentVisible);

        var reloaded = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            fixture.Root);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal(7, reloaded.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<int>());
    }

    [Fact]
    public async Task TaskQueueGuide_ConfigurationContextReloadRefreshesPersistedStep()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        Assert.True(vm.IsGuideVisible);
        Assert.True(vm.IsGuideStep0Visible);

        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("6");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        await vm.ReloadConfigurationContextAsync();

        Assert.Equal(6, vm.GuideStepIndex);
        Assert.True(vm.IsGuideVisible);
        Assert.True(vm.IsGuideStep6Visible);
        Assert.True(vm.IsGuideDoneVisible);
    }

    [Fact]
    public async Task TaskQueueGuide_OpenTaskStepSuspendsWithoutLosingProgressAndResumeRestoresIt()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("5");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True(vm.OpenCurrentGuideStepTarget());
        Assert.False(vm.IsGuideVisible);
        Assert.True(vm.IsTaskQueueContentVisible);
        Assert.True(vm.CanResumeGuide);
        Assert.Equal("5", fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<string>());

        vm.ResumeGuide();

        Assert.True(vm.IsGuideVisible);
        Assert.False(vm.CanResumeGuide);
        Assert.True(vm.IsGuideStep5Visible);
    }

    [Fact]
    public async Task TaskQueueGuide_OpenSettingsStepNavigatesWithoutCompletingGuide()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("3");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        var sections = new List<string>();
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel(),
            navigateToSettingsSection: sections.Add);

        Assert.True(vm.OpenCurrentGuideStepTarget());
        Assert.Equal(["Connect"], sections);
        Assert.True(vm.IsGuideVisible);
        Assert.False(vm.CanResumeGuide);
        Assert.Equal("3", fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<string>());
    }

    [Fact]
    public async Task TaskQueueGuide_AgreementConfirmationShouldPersistSeparatelyAndKeepNextEnabled()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("0");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True(vm.IsGuideNextEnabled);
        Assert.True(await vm.NextGuideStepAsync());
        Assert.Equal(1, vm.GuideStepIndex);
        Assert.True(vm.IsGuideAgreementStep);
        Assert.True(vm.IsGuideAgreementTipVisible);
        Assert.True(vm.IsGuideNextEnabled);
        Assert.True(vm.GuideAgreementBadgeVisible);

        Assert.True(await vm.ConfirmGuideAgreementAsync());
        Assert.True(vm.IsGuideAgreementConfirmed);
        Assert.False(vm.IsGuideAgreementTipVisible);
        Assert.False(vm.GuideAgreementBadgeVisible);
        Assert.False(vm.CanConfirmGuideAgreement);

        Assert.True(await vm.NextGuideStepAsync());
        Assert.Equal(2, vm.GuideStepIndex);

        var reloaded = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            fixture.Root);
        await reloaded.LoadOrBootstrapAsync();
        Assert.True(reloaded.CurrentConfig.GlobalValues[ConfigurationKeys.GuideAgreementConfirmed]!.GetValue<bool>());
        Assert.Equal(2, reloaded.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<int>());
    }

    [Fact]
    public async Task TaskQueueGuide_TaskDemoShouldBeLockedUntilAgreementIsConfirmed()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("5");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True(vm.IsGuideTaskDemoStep);
        Assert.True(vm.IsGuideDemoLocked);
        Assert.False(vm.IsGuideNextEnabled);
        Assert.False(string.IsNullOrWhiteSpace(vm.GuideNextGateTip));
        Assert.False(vm.CompleteGuideDemoAction(SettingsGuideDemoAction.AddFight));

        for (var step = 5; step > 1; step--)
        {
            Assert.True(await vm.PreviousGuideStepAsync());
        }

        Assert.Equal(1, vm.GuideStepIndex);
        Assert.True(await vm.ConfirmGuideAgreementAsync());
        for (var step = 1; step < 5; step++)
        {
            Assert.True(await vm.NextGuideStepAsync());
        }

        Assert.Equal(5, vm.GuideStepIndex);
        Assert.False(vm.IsGuideDemoLocked);
        Assert.False(vm.IsGuideNextEnabled);
        foreach (var action in Enum.GetValues<SettingsGuideDemoAction>())
        {
            Assert.True(vm.CompleteGuideDemoAction(action));
        }

        Assert.True(vm.IsGuideDemoComplete);
        Assert.True(vm.IsGuideNextEnabled);
        Assert.True(await vm.NextGuideStepAsync());
        Assert.Equal(6, vm.GuideStepIndex);
    }

    [Fact]
    public async Task TaskQueueGuide_DemoActionShouldOperateOnRealQueue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("5");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.ReloadTasksAsync();
        for (var step = 5; step > 1; step--)
        {
            Assert.True(await vm.PreviousGuideStepAsync());
        }

        Assert.True(await vm.ConfirmGuideAgreementAsync());
        for (var step = 1; step < 5; step++)
        {
            Assert.True(await vm.NextGuideStepAsync());
        }

        Assert.True(vm.IsGuideTaskDemoStep);
        Assert.True(await vm.ExecuteGuideDemoActionAsync(SettingsGuideDemoAction.AddStartUp));
        var before = vm.Tasks.Count;

        Assert.True(await vm.ExecuteGuideDemoActionAsync(SettingsGuideDemoAction.AddFight));

        Assert.Equal(before + 1, vm.Tasks.Count);
        Assert.Contains(vm.Tasks, task => task.Type == TaskModuleTypes.Fight);
        Assert.True(vm.GuideDemoSteps.Single(step => step.Action == SettingsGuideDemoAction.AddFight).IsCompleted);
    }

    [Fact]
    public async Task TaskQueueGuide_SkipShouldRecordStepAndPersistCompletion()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("3");
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion] = JsonValue.Create(2);
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.True(await vm.SkipGuideAsync());
        Assert.Equal(7, vm.GuideStepIndex);
        Assert.False(vm.IsGuideVisible);
        Assert.Equal(
            3,
            fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideSkippedAtStep]!.GetValue<int>());
        Assert.True(fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideSkipped]!.GetValue<bool>());

        var reloaded = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            fixture.Root);
        await reloaded.LoadOrBootstrapAsync();
        Assert.True(reloaded.CurrentConfig.GlobalValues[ConfigurationKeys.GuideSkipped]!.GetValue<bool>());
        Assert.Equal(
            3,
            reloaded.CurrentConfig.GlobalValues[ConfigurationKeys.GuideSkippedAtStep]!.GetValue<int>());
        Assert.Equal(7, reloaded.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<int>());
    }

    [Fact]
    public async Task TaskQueueGuide_LegacyStepIndexShouldMigrateOnceToDevV2Order()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex] = JsonValue.Create("3");

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());

        Assert.Equal(5, vm.GuideStepIndex);
        Assert.True(vm.IsGuideStep5Visible);
        Assert.True(vm.IsGuideTaskDemoStep);
        Assert.Equal(
            5,
            fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepIndex]!.GetValue<int>());
        Assert.Equal(
            2,
            fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.GuideStepOrderVersion]!.GetValue<int>());

        var second = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        Assert.Equal(5, second.GuideStepIndex);
    }

    [Fact]
    public async Task TaskQueuePage_InitializeOnEmptyQueue_ShouldSeedWpfDefaultTasks()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // WPF dev-v2 66e003377c: Reclamation left the default queue (it is a toolbox mini-game
        // now), so the seeded order ends at Roguelike.
        var expected = new[]
        {
            TaskModuleTypes.StartUp,
            TaskModuleTypes.Fight,
            TaskModuleTypes.Infrast,
            TaskModuleTypes.Recruit,
            TaskModuleTypes.Mall,
            TaskModuleTypes.Award,
            TaskModuleTypes.Roguelike,
        };

        Assert.Equal(expected.Length, vm.Tasks.Count);
        Assert.Equal(expected, vm.Tasks.Select(task => task.Type).ToArray());
        Assert.Equal(vm.Tasks.Count, vm.TaskPanels.Count);
        Assert.Equal(expected, vm.TaskPanels.Select(panel => panel.ModuleType).ToArray());
    }

    [Fact]
    public async Task TaskQueuePage_InitializeOnEmptyQueue_ShouldSelectLastSeededTaskLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        // WPF seeds defaults through sequential add notifications; each add moves
        // the settings selection to the newly added row, so the final seeded task
        // (Roguelike after Reclamation moved to the toolbox) ends up selected and persisted.
        Assert.Equal(7, vm.Tasks.Count);
        Assert.Equal(TaskModuleTypes.Roguelike, vm.SelectedTask?.Type);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        Assert.Equal(vm.Tasks.Count - 1, profile.Values["TaskSelectedIndex"]?.GetValue<int>());
    }

    [Fact]
    public async Task TaskQueuePage_RemoveLastTask_ShouldKeepQueueEmptyUntilNextInitialization()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Single(vm.Tasks);

        await vm.RemoveSelectedTaskAsync();
        await vm.WaitForPendingBindingAsync();

        Assert.Empty(vm.Tasks);
        Assert.Null(vm.SelectedTask);

        // WPF keeps the session queue empty after deleting the final task; the
        // 200 ms autosave must persist the empty queue instead of reseeding.
        await Task.Delay(700);
        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.Empty(queue.Value!);
        var persisted = await ReadPersistedProfileAsync(fixture);
        Assert.Empty(Assert.IsType<JsonArray>(persisted["TaskQueue"]));

        // Defaults only return on the next page/application initialization.
        var reloaded = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await reloaded.InitializeAsync();
        Assert.Equal(7, reloaded.Tasks.Count);
        Assert.Equal(TaskModuleTypes.StartUp, reloaded.Tasks[0].Type);
        Assert.Equal(TaskModuleTypes.Roguelike, reloaded.SelectedTask?.Type);
    }

    [Fact]
    public async Task SettingsPage_InverseClearModeChange_ShouldPersistGloballyLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.InverseClearMode = "Inverse";

        Assert.True(await WaitForConditionAsync(
            () =>
            {
                var globalValues = fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues;
                return Task.FromResult(
                    globalValues.TryGetValue(ConfigurationKeys.InverseClearMode, out var node)
                    && string.Equals(node?.GetValue<string>(), "Inverse", StringComparison.Ordinal));
            },
            retry: 80,
            delayMs: 50));
        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        Assert.False(profile.Values.ContainsKey(ConfigurationKeys.InverseClearMode));
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
    public async Task ReclamationModule_SaveArchiveMode_ShouldPreserveHiddenClearStore()
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
        Assert.True(parameters["clear_store"]?.GetValue<bool>());

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
        Assert.Single(vm.TaskPanels, panel => panel.IsSelected);
    }

    [Fact]
    public async Task RoguelikeModule_HiddenModeGatedSettings_PersistAcrossModeAndThemeSwitches()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        vm.RoguelikeModule.Theme = "Sami";
        vm.RoguelikeModule.Mode = 4;
        vm.RoguelikeModule.Squad = "突击战术分队";
        vm.RoguelikeModule.StartWithEliteTwo = true;
        vm.RoguelikeModule.OnlyStartWithEliteTwo = true;
        vm.RoguelikeModule.FirstFloorFoldartalEnabled = true;
        vm.RoguelikeModule.FirstFloorFoldartal = "号角";
        vm.RoguelikeModule.ExpectedCollapsalParadigmsText = "范式A";
        Assert.True(await vm.RoguelikeModule.SaveAsync());

        // Switching to Exp mode hides the collectible-mode controls, exactly like WPF.
        vm.RoguelikeModule.Mode = 0;
        Assert.False(vm.RoguelikeModule.ShowStartWithEliteTwo);
        Assert.False(vm.RoguelikeModule.ShowFirstFloorFoldartalToggle);
        Assert.True(await vm.RoguelikeModule.SaveAsync());

        var stored = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal(0, stored["mode"]?.GetValue<int>());
        Assert.True(stored["start_with_elite_two"]?.GetValue<bool>());
        Assert.True(stored["only_start_with_elite_two"]?.GetValue<bool>());
        Assert.Equal("号角", stored["first_floor_foldartal"]?.GetValue<string>());
        Assert.Equal(
            ["范式A"],
            ((JsonArray)stored["expected_collapsal_paradigms"]!).Select(node => node!.GetValue<string>()));

        // The Core-facing compiled form stays gated while mode 0 is active.
        var gated = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.False(gated.Value!.CompiledParams.ContainsKey("start_with_elite_two"));
        Assert.False(gated.Value.CompiledParams.ContainsKey("first_floor_foldartal"));
        Assert.False(gated.Value.CompiledParams.ContainsKey("expected_collapsal_paradigms"));

        // Reloading and switching back restores the hidden intent like frozen WPF.
        await vm.ReloadTasksAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();
        Assert.True(vm.RoguelikeModule.StartWithEliteTwo);
        Assert.True(vm.RoguelikeModule.OnlyStartWithEliteTwo);
        Assert.True(vm.RoguelikeModule.FirstFloorFoldartalEnabled);
        Assert.Equal("号角", vm.RoguelikeModule.FirstFloorFoldartal);
        Assert.Equal("范式A", vm.RoguelikeModule.ExpectedCollapsalParadigmsText);

        vm.RoguelikeModule.Mode = 4;
        Assert.True(await vm.RoguelikeModule.SaveAsync());
        var restored = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(restored.Value!.CompiledParams["start_with_elite_two"]?.GetValue<bool>());
        Assert.True(restored.Value.CompiledParams["only_start_with_elite_two"]?.GetValue<bool>());
        Assert.Empty(Assert.IsType<JsonObject>(restored.Value.CompiledParams["collectible_mode_start_list"]));
        Assert.Equal("号角", restored.Value.CompiledParams["first_floor_foldartal"]?.GetValue<string>());
    }

    [Fact]
    public async Task RoguelikeModule_HiddenFoldartalControls_PersistAcrossThemeSwitch()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        vm.RoguelikeModule.Theme = "Sami";
        vm.RoguelikeModule.Mode = 4;
        vm.RoguelikeModule.Squad = "生活至上分队";
        vm.RoguelikeModule.FirstFloorFoldartalEnabled = true;
        vm.RoguelikeModule.FirstFloorFoldartal = "号角";
        vm.RoguelikeModule.StartFoldartalListEnabled = true;
        vm.RoguelikeModule.StartFoldartalListText = "火炬;涟漪";
        Assert.True(await vm.RoguelikeModule.SaveAsync());

        // Switching the theme hides the Sami foldartal controls; WPF keeps the text.
        vm.RoguelikeModule.Theme = "Sarkaz";
        Assert.False(vm.RoguelikeModule.ShowFirstFloorFoldartalToggle);
        Assert.True(await vm.RoguelikeModule.SaveAsync());

        var stored = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("Sarkaz", stored["theme"]?.GetValue<string>());
        Assert.Equal("号角", stored["first_floor_foldartal"]?.GetValue<string>());
        Assert.Equal(
            ["火炬", "涟漪"],
            ((JsonArray)stored["start_foldartal_list"]!).Select(node => node!.GetValue<string>()));

        // Sarkaz runs must not receive the Sami-only foldartal parameters.
        var gated = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.False(gated.Value!.CompiledParams.ContainsKey("first_floor_foldartal"));
        Assert.False(gated.Value.CompiledParams.ContainsKey("start_foldartal_list"));

        await vm.ReloadTasksAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();
        vm.RoguelikeModule.Theme = "Sami";
        Assert.Equal("号角", vm.RoguelikeModule.FirstFloorFoldartal);
        Assert.Equal("火炬;涟漪", vm.RoguelikeModule.StartFoldartalListText);
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
        var opers = Assert.IsType<JsonArray>(persisted["core_char_list"]);
        var first = Assert.IsType<JsonObject>(Assert.Single(opers));
        Assert.Equal(stableCoreChar, first["name"]?.GetValue<string>());
        Assert.DoesNotContain("core_char", persisted.Select(pair => pair.Key), StringComparer.Ordinal);
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
    public async Task TaskQueueFeatureService_SetAllAndInvertEnabled_ShouldMatchWpfBatchSelectionRules()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Recruit", "recruit-c", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue-d", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "reclamation-e", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Custom, "custom-f", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(4, null)).Success);

        var enableAll = await fixture.TaskQueue.SetAllTasksEnabledAsync(true);
        Assert.True(enableAll.Success);

        var selectedAll = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(selectedAll.Success);
        Assert.NotNull(selectedAll.Value);
        Assert.Collection(
            selectedAll.Value!,
            task => Assert.True(task.IsEnabled),
            task => Assert.True(task.IsEnabled),
            task => Assert.True(task.IsEnabled),
            task => Assert.False(task.IsEnabled),
            task => Assert.Null(task.IsEnabled),
            task => Assert.False(task.IsEnabled));

        var disableAll = await fixture.TaskQueue.SetAllTasksEnabledAsync(false);
        Assert.True(disableAll.Success);

        var allDisabled = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(allDisabled.Success);
        Assert.NotNull(allDisabled.Value);
        Assert.All(allDisabled.Value!, task => Assert.False(task.IsEnabled));

        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(0, true)).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(1, false)).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(2, null)).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(3, false)).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(4, null)).Success);
        Assert.True((await fixture.TaskQueue.SetTaskEnabledAsync(5, true)).Success);

        var invert = await fixture.TaskQueue.InvertTasksEnabledAsync();
        Assert.True(invert.Success);

        var inverted = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(inverted.Success);
        Assert.NotNull(inverted.Value);
        Assert.Collection(
            inverted.Value!,
            task => Assert.False(task.IsEnabled),
            task => Assert.True(task.IsEnabled),
            task => Assert.False(task.IsEnabled),
            task => Assert.False(task.IsEnabled),
            task => Assert.Null(task.IsEnabled),
            task => Assert.True(task.IsEnabled));
    }

    [Fact]
    public async Task TaskQueuePage_RightClickOneShot_DefaultMode_ShouldSetRunOnceState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step", enabled: false)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var task = Assert.Single(vm.Tasks);
        await vm.ToggleTaskEnabledOneShotAsync(task);

        Assert.Null(task.IsEnabled);
        Assert.Contains(vm.RootTexts["TaskQueue.Status.TaskEnabled.Once"], task.ToolTipText, StringComparison.Ordinal);
        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.Null(Assert.Single(queue.Value!).IsEnabled);
    }

    [Fact]
    public async Task TaskQueuePage_RightClickOneShot_InvertMode_ShouldSetSkipOnceState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[TaskQueueEnabledState.MainTasksInvertNullFunctionKey] = JsonValue.Create(true);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step", enabled: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var task = Assert.Single(vm.Tasks);
        await vm.ToggleTaskEnabledOneShotAsync(task);

        Assert.Null(task.IsEnabled);
        Assert.Contains(vm.RootTexts["TaskQueue.Status.TaskEnabled.SkipOnce"], task.ToolTipText, StringComparison.Ordinal);
        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        Assert.Null(Assert.Single(queue.Value!).IsEnabled);
    }

    [Fact]
    public async Task TaskQueuePage_AllTasksCompleted_DefaultMode_ShouldResetOneShotRunOnceToDisabled()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step", enabled: false)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var task = Assert.Single(vm.Tasks);
        await vm.ToggleTaskEnabledOneShotAsync(task);
        Assert.Null(task.IsEnabled);
        MapCoreTaskId(fixture.Runtime.SessionService, 1, 0);

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "AllTasksCompleted", """{"finished_tasks":[1]}""", DateTimeOffset.UtcNow));

        Assert.False(task.IsEnabled);
        Assert.False(fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue[0].IsEnabled);
        var persistedTask = await ReadPersistedTaskAsync(fixture);
        Assert.False(persistedTask["IsEnabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task TaskQueuePage_AllTasksCompleted_ShouldResetFightTaskAndParameterOneShotStates()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight", enabled: false)).Success);
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

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var task = Assert.Single(vm.Tasks);
        await vm.ToggleTaskEnabledOneShotAsync(task);
        Assert.Null(task.IsEnabled);
        MapCoreTaskId(fixture.Runtime.SessionService, 1, 0);

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "AllTasksCompleted", """{"finished_tasks":[1]}""", DateTimeOffset.UtcNow));

        Assert.False(task.IsEnabled);
        var persisted = fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue[0];
        Assert.False(persisted.IsEnabled);
        Assert.False(persisted.Params["_ui_use_medicine"]?.GetValue<bool>());
        Assert.False(persisted.Params["_ui_use_stone"]?.GetValue<bool>());
        Assert.False(persisted.Params["_ui_enable_times_limit"]?.GetValue<bool>());
        Assert.False(persisted.Params["_ui_enable_target_drop"]?.GetValue<bool>());
    }

    [Fact]
    public async Task TaskQueuePage_AllTasksCompleted_ShouldResetDisabledFightOneShotParams()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.SaveFightParamsAsync(0, new FightTaskParamsDto
        {
            Stage = "1-7",
            UseMedicine = null,
            UseStone = null,
            EnableTimesLimit = null,
            EnableTargetDrop = null,
        })).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var task = Assert.Single(vm.Tasks);
        Assert.False(task.IsEnabled);

        // WPF ResetAllTemporaryVariable clears Fight one-shot parameters on every
        // Fight task, including disabled ones, when all tasks complete.
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "AllTasksCompleted", """{"finished_tasks":[]}""", DateTimeOffset.UtcNow));

        var persisted = fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue[0];
        Assert.False(persisted.IsEnabled);
        Assert.False(persisted.Params["_ui_use_medicine"]?.GetValue<bool>());
        Assert.False(persisted.Params["_ui_use_stone"]?.GetValue<bool>());
        Assert.False(persisted.Params["_ui_enable_times_limit"]?.GetValue<bool>());
        Assert.False(persisted.Params["_ui_enable_target_drop"]?.GetValue<bool>());
    }

    [Fact]
    public async Task TaskQueuePage_AllTasksCompleted_InvertMode_ShouldResetOneShotSkipOnceToEnabled()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[TaskQueueEnabledState.MainTasksInvertNullFunctionKey] = JsonValue.Create(true);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step", enabled: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var task = Assert.Single(vm.Tasks);
        await vm.ToggleTaskEnabledOneShotAsync(task);
        Assert.Null(task.IsEnabled);
        MapCoreTaskId(fixture.Runtime.SessionService, 1, 0);

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "AllTasksCompleted", """{"finished_tasks":[1]}""", DateTimeOffset.UtcNow));

        Assert.True(task.IsEnabled);
        Assert.True(fixture.Config.CurrentConfig.Profiles["Default"].TaskQueue[0].IsEnabled);
        var persistedTask = await ReadPersistedTaskAsync(fixture);
        Assert.True(persistedTask["IsEnabled"]?.GetValue<bool>());
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
        fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile]
            .Values[ConfigurationKeys.InverseClearMode] = JsonValue.Create("Clear");
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
    public async Task TaskQueuePage_ToggleSelectionBatchMode_ShouldPersistInverseClearModeGloballyLikeWpf()
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
        Assert.Equal(
            "ClearInverse",
            fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.InverseClearMode]?.GetValue<string>());
        Assert.False(profile.Values.ContainsKey(ConfigurationKeys.InverseClearMode));
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
    public async Task RoguelikeModule_LanguageHotSwitch_ShouldRefreshLocalizedOptionsInPlace()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        var module = new RoguelikeModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "zh-cn" });

        Assert.Equal("界园", module.ThemeOptions.Single(option => option.Value == "JieGarden").Display);

        module.Theme = "Sami";
        module.Mode = 4;

        var themeOptions = module.ThemeOptions;
        var modeOptions = module.ModeOptions;
        var difficultyOptions = module.DifficultyOptions;
        var squadOptions = module.SquadOptions;
        var difficultyCurrent = difficultyOptions.Single(option => option.Value == -1);
        Assert.Equal("不切换 (-1)", difficultyCurrent.Display);

        module.Texts.Language = "en-us";

        // 语言热切换只刷新条目文本（RefreshLocalization），不重建列表、不影响选中值。
        Assert.Same(themeOptions, module.ThemeOptions);
        Assert.Same(modeOptions, module.ModeOptions);
        Assert.Same(difficultyOptions, module.DifficultyOptions);
        Assert.Same(squadOptions, module.SquadOptions);
        Assert.Equal("Sami", module.Theme);
        Assert.Equal(4, module.Mode);
        Assert.Equal("Sami", themeOptions.Single(option => option.Value == "Sami").Display);
        Assert.Equal(
            "Gain Experience Points, clear as many nodes as possible",
            modeOptions.Single(option => option.Value == 0).Display);
        Assert.Equal("Don't Switch (-1)", difficultyCurrent.Display);
        Assert.Equal("Gathering Squad", squadOptions.Single(option => option.Value == "集群分队").Display);
    }

    [Fact]
    public async Task RoguelikeModule_BlackFlowTheme_ShouldMarkNotAdaptedForNonOfficialClients()
    {
        await using var fixture = await TestFixture.CreateAsync(language: "zh-cn");
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.Values["ClientType"] = JsonValue.Create("YoStarEN");
        var module = new RoguelikeModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "zh-cn" });

        Assert.Contains(
            "未适配",
            module.ThemeOptions.Single(option => option.Value == "BlackFlow").Display,
            StringComparison.Ordinal);

        profile.Values["ClientType"] = JsonValue.Create("Official");
        module.RefreshGuiDependentOptions();

        Assert.Equal(
            "黑流树海",
            module.ThemeOptions.Single(option => option.Value == "BlackFlow").Display);
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
            return queue.Success && queue.Value is not null && queue.Value[0].IsEnabled is not true;
        });
        Assert.True(synced);
    }

    [Fact]
    public async Task TaskQueuePage_BatchEnabledChanges_ShouldKeepExcludedTasksInSyncWithProfile()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Roguelike, "rogue", enabled: false)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Reclamation, "reclamation", enabled: true)).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Custom, "custom", enabled: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.SelectAllAsync(true);
        Assert.Collection(
            vm.Tasks,
            task => Assert.True(task.IsEnabled),
            task => Assert.False(task.IsEnabled),
            task => Assert.True(task.IsEnabled),
            task => Assert.True(task.IsEnabled));

        await vm.InverseSelectionAsync();
        var queue = Assert.IsAssignableFrom<IReadOnlyList<UnifiedTaskItem>>(
            AssertSuccessfulValue(await fixture.TaskQueue.GetCurrentTaskQueueAsync()));
        Assert.Equal(queue.Select(static task => task.IsEnabled), vm.Tasks.Select(static task => task.IsEnabled));

        await vm.SelectAllAsync(false);
        queue = Assert.IsAssignableFrom<IReadOnlyList<UnifiedTaskItem>>(
            AssertSuccessfulValue(await fixture.TaskQueue.GetCurrentTaskQueueAsync()));
        Assert.All(vm.Tasks, task => Assert.False(task.IsEnabled));
        Assert.All(queue, task => Assert.False(task.IsEnabled));
    }

    [Fact]
    public async Task TaskQueuePage_QueueAndSelectionChanges_ShouldAutoSaveWithinWpfDelayWindow()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = vm.Tasks[1];
        vm.RenameTargetName = "fight-renamed";
        await vm.RenameSelectedTaskAsync();

        Assert.True(await WaitForConditionAsync(async () =>
        {
            var profile = await ReadPersistedProfileAsync(fixture);
            var queue = Assert.IsType<JsonArray>(profile["TaskQueue"]);
            var values = Assert.IsType<JsonObject>(profile["Values"]);
            return string.Equals(queue[1]?["Name"]?.GetValue<string>(), "fight-renamed", StringComparison.Ordinal)
                && values["TaskSelectedIndex"]?.GetValue<int>() == 1;
        }, retry: 30, delayMs: 20));
    }

    [Fact]
    public async Task TaskQueuePage_ProfileSelectionMinusOne_ShouldFallbackToFightLikeWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Fight, "fight")).Success);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values["TaskSelectedIndex"] = JsonValue.Create(-1);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal(TaskModuleTypes.Fight, vm.SelectedTask?.Type);
    }

    [Fact]
    public async Task TaskQueuePage_AddAndRemove_ShouldSelectWpfEquivalentTaskAndRequireUiConfirmation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.AddTaskAsync(TaskModuleTypes.Fight);
        Assert.Equal(TaskModuleTypes.Fight, vm.SelectedTask?.Type);

        await vm.RemoveSelectedTaskWithConfirmationAsync();
        Assert.Equal(2, vm.Tasks.Count);

        await vm.RemoveSelectedTaskAsync();
        Assert.Single(vm.Tasks);
        Assert.Equal(TaskModuleTypes.StartUp, vm.SelectedTask?.Type);
    }

    [Fact]
    public async Task TaskQueuePage_TaskEnabledToggle_WhenBoundModuleSaveFails_ShouldRollbackUiState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.StartUpModule.AccountName = "force-bound-module-save";
        Assert.True(vm.StartUpModule.IsDirty);
        Assert.True((await fixture.TaskQueue.RemoveTaskAsync(0)).Success);

        vm.Tasks[0].IsEnabled = false;

        Assert.True(await WaitForConditionAsync(
            () => Task.FromResult(vm.Tasks[0].IsEnabled is true),
            retry: 30,
            delayMs: 20));
    }

    [Fact]
    public async Task TaskQueuePage_CloseFlush_ShouldResetAndPersistOneShotEnabledState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.SingleStep, "single-step", enabled: false)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ToggleTaskEnabledOneShotAsync(Assert.Single(vm.Tasks));
        Assert.Null(vm.Tasks[0].IsEnabled);

        Assert.True(await vm.FlushConfigurationSavesForCloseAsync());
        Assert.False(vm.Tasks[0].IsEnabled);
        Assert.False((await ReadPersistedTaskAsync(fixture))["IsEnabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task TaskQueuePage_TypedModuleChange_ShouldPersistWithinUnifiedTwoHundredMillisecondDebounce()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.StartUp, "startup")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.StartUpModule.AccountName = "debounce-200ms";

        Assert.True(await WaitForConditionAsync(async () =>
        {
            var result = await fixture.TaskQueue.GetTaskParamsAsync(0);
            return result.Success
                && string.Equals(result.Value?["account_name"]?.GetValue<string>(), "debounce-200ms", StringComparison.Ordinal);
        }, retry: 19, delayMs: 20));
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
    public async Task ConfigLoad_LegacyAutoRestartOnDropKey_ShouldBeReadableThenDropped()
    {
        // WPF dev-v2 (01184e2484) removed the restart-on-drop switch. An existing config that
        // still carries the key must keep loading, and the migration drops the dead value.
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        profile.Values[ConfigurationKeys.AutoRestartOnDrop] = JsonValue.Create(true);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.AutoRestartOnDrop] = JsonValue.Create(true);
        await fixture.Config.SaveAsync();

        var reloaded = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            fixture.Root);
        var load = await reloaded.LoadOrBootstrapAsync();

        Assert.True(load.LoadedFromExistingConfig);
        Assert.True(reloaded.TryGetCurrentProfile(out var reloadedProfile));
        Assert.False(reloadedProfile.Values.ContainsKey(ConfigurationKeys.AutoRestartOnDrop));
        Assert.False(reloaded.CurrentConfig.GlobalValues.ContainsKey(ConfigurationKeys.AutoRestartOnDrop));
        var persisted = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "config", "avalonia.json"));
        Assert.DoesNotContain(nameof(ConfigurationKeys.AutoRestartOnDrop), persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigImport_LegacyAutoRestartOnDropKey_ShouldNotSurviveMigration()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Start.AutoRestartOnDrop": true,
                  "ConnectAddress": "127.0.0.1:5555"
                }
              }
            }
            """);

        var report = await fixture.Config.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: true);

        Assert.True(report.Success);
        Assert.Contains("gui.json", report.ImportedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.True(fixture.Config.TryGetCurrentProfile(out var profile));
        Assert.False(profile.Values.ContainsKey(ConfigurationKeys.AutoRestartOnDrop));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey(ConfigurationKeys.AutoRestartOnDrop));
        var persisted = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "config", "avalonia.json"));
        Assert.DoesNotContain(nameof(ConfigurationKeys.AutoRestartOnDrop), persisted, StringComparison.Ordinal);
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
    public async Task RoguelikeModule_WhenCoreCharCleared_PreservesWpfSupportFlags()
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
        Assert.True(vm.UseSupport);
        Assert.False(vm.ShowUseNonfriendSupport);

        var dto = BuildRoguelikeDto(vm);
        Assert.True(dto.UseSupport);
        Assert.True(dto.UseNonfriendSupport);

        vm.UseSupport = false;
        dto = BuildRoguelikeDto(vm);
        Assert.False(dto.UseSupport);
        Assert.True(dto.UseNonfriendSupport);
    }

    [Fact]
    public async Task RoguelikeModule_ThemeChanges_ClearUnavailableRewardsAndPreserveEnabledSeed()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new RoguelikeModuleViewModel(fixture.Runtime, new LocalizedTextMap());
        Assert.True(vm.CollectibleHotWater);

        vm.Mode = 4;
        vm.Theme = "Mizuki";
        vm.CollectibleDice = true;
        vm.StartWithSeedEnabled = true;
        vm.StartWithSeed = "seed,rogue_1,3";

        vm.Theme = "Sami";
        Assert.False(vm.ShowStartWithSeedToggle);
        Assert.True(vm.ShowStartWithSeedInput);
        Assert.False(vm.CollectibleDice);

        vm.Theme = "Mizuki";
        Assert.False(vm.CollectibleDice);
        Assert.Equal("seed,rogue_1,3", BuildRoguelikeDto(vm).StartWithSeed);
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

    private static async Task InvokeCallbackAsync(TaskQueuePageViewModel vm, CoreCallbackEvent callback)
    {
        var method = typeof(TaskQueuePageViewModel).GetMethod(
            "HandleCallbackCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method.Invoke(vm, [callback, GetActiveRunLease(vm)]) as Task;
        if (task is null)
        {
            throw new InvalidOperationException("HandleCallbackCoreAsync invocation returned null.");
        }

        await task;
    }

    private static RunOwnershipLease? GetActiveRunLease(TaskQueuePageViewModel vm)
    {
        var field = typeof(TaskQueuePageViewModel).GetField(
            "_activeRunLease",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var snapshot = field!.GetValue(vm);
        if (snapshot is null)
        {
            return null;
        }

        var leaseProperty = snapshot.GetType().GetProperty("Lease", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(leaseProperty);
        return (RunOwnershipLease)leaseProperty!.GetValue(snapshot)!;
    }

    private static void MapCoreTaskId(UnifiedSessionService sessionService, int taskId, int taskIndex)
    {
        var method = typeof(UnifiedSessionService).GetMethod(
            "SetTaskIdMapping",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(sessionService, [taskId, taskIndex]);
    }

    private static async Task<JsonObject> ReadPersistedTaskAsync(TestFixture fixture)
    {
        var persistedDefault = await ReadPersistedProfileAsync(fixture);
        var persistedQueue = Assert.IsType<JsonArray>(persistedDefault["TaskQueue"]);
        return Assert.IsType<JsonObject>(persistedQueue[0]);
    }

    private static async Task<JsonObject> ReadPersistedProfileAsync(TestFixture fixture)
    {
        var persistedPath = Path.Combine(fixture.Root, "config", "avalonia.json");
        var persisted = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(persistedPath)));
        var persistedProfiles = Assert.IsType<JsonObject>(persisted["Profiles"]);
        return Assert.IsType<JsonObject>(persistedProfiles["Default"]);
    }

    private static T AssertSuccessfulValue<T>(UiOperationResult<T> result)
    {
        Assert.True(result.Success, result.Message);
        return Assert.IsAssignableFrom<T>(result.Value);
    }

    [Fact]
    public async Task TaskQueueFeatureService_UpdateTaskParams_WhenSecondMappedCoreTaskFails_ShouldRollBackProfileAndAppliedSibling()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Clear();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.UserDataUpdate, "user-data-update")).Success);
        Assert.True((await fixture.Runtime.SessionService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);
        var appended = await fixture.Runtime.SessionService.AppendTasksFromCurrentProfileAsync();
        Assert.True(appended.Success);
        Assert.Equal(2, appended.Value);

        fixture.Bridge.SetTaskParamsResults.Enqueue(CoreResult<bool>.Ok(true));
        fixture.Bridge.SetTaskParamsResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NativeCallFailed, "second update failed")));

        var previousParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        var newParams = previousParams.DeepClone() as JsonObject ?? new JsonObject();
        newParams["update_oper_box"] = false;

        var result = await fixture.TaskQueue.UpdateTaskParamsAsync(0, newParams);

        Assert.False(result.Success);
        var currentParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.True(currentParams["update_oper_box"]!.GetValue<bool>());
        Assert.Equal(3, fixture.Bridge.SetTaskParamsCalls.Count);
        Assert.Equal(fixture.Bridge.SetTaskParamsCalls[0].TaskId, fixture.Bridge.SetTaskParamsCalls[2].TaskId);
        var expectedPreviousCoreParams = MAAUnified.Application.Services.TaskParams.TaskParamCompiler
            .BuildCoreParams(TaskModuleTypes.UserDataUpdate, previousParams)
            .ToJsonString();
        Assert.Equal(expectedPreviousCoreParams, fixture.Bridge.SetTaskParamsCalls[2].ParamsJson);
    }

    [Fact]
    public void TaskQueueItem_MultipleCoreTaskIds_AggregatePerChainStatus()
    {
        // WPF dev-v2 (9e846609aa/caec74ff1f/be31b86dbd): one queue row owns several core
        // chains; the row aggregates Error > InProgress > all Completed > Skipped > Idle
        // and keeps a per-chain completed counter instead of letting the last chain
        // overwrite the whole row.
        var vm = new TaskQueueItemViewModel("DepotMaintain", "depot", true);
        vm.SetTaskIds([11, 12]);

        Assert.Equal(2, vm.ChainCount);
        Assert.Equal(["Idle", "Idle"], vm.ChainStatuses);

        Assert.True(vm.TryApplyChainStatus(11, TaskQueueItemStatus.Running));
        Assert.True(vm.TryApplyChainStatus(12, TaskQueueItemStatus.Running));
        Assert.Equal(TaskQueueItemStatus.Running, vm.Status);

        Assert.True(vm.TryApplyChainStatus(11, TaskQueueItemStatus.Success));
        Assert.Equal(TaskQueueItemStatus.Running, vm.Status);
        Assert.Equal(1, vm.CompletedChainCount);

        Assert.True(vm.TryApplyChainStatus(12, TaskQueueItemStatus.Success));
        Assert.Equal(TaskQueueItemStatus.Success, vm.Status);
        Assert.Equal(2, vm.CompletedChainCount);

        // Error outranks a still-running sibling.
        vm.SetTaskIds([11, 12]);
        vm.TryApplyChainStatus(11, TaskQueueItemStatus.Success);
        vm.TryApplyChainStatus(12, TaskQueueItemStatus.Running);
        Assert.Equal(TaskQueueItemStatus.Running, vm.Status);
        vm.TryApplyChainStatus(12, TaskQueueItemStatus.Error);
        Assert.True(vm.IsStatusError);
        Assert.Equal(2, vm.CompletedChainCount);

        // All completed outranks a skipped sibling; Skipped applies only when nothing ran.
        vm.SetTaskIds([11, 12]);
        vm.TryApplyChainStatus(11, TaskQueueItemStatus.Success);
        vm.TryApplyChainStatus(12, TaskQueueItemStatus.Skipped);
        Assert.True(vm.IsStatusSkipped);
        Assert.Equal(1, vm.CompletedChainCount);

        // Unknown ids are not this row's chains: the caller falls back to the legacy path.
        Assert.False(vm.TryApplyChainStatus(99, TaskQueueItemStatus.Error));
        Assert.Equal(TaskQueueItemStatus.Skipped, vm.Status);
    }

    [Fact]
    public void TaskQueueItem_SingleChain_KeepsLegacyStatusBehavior()
    {
        var vm = new TaskQueueItemViewModel("Fight", "fight", true);
        vm.SetTaskIds([7]);

        Assert.True(vm.TryApplyChainStatus(7, TaskQueueItemStatus.Running));
        Assert.Equal(TaskQueueItemStatus.Running, vm.Status);
        Assert.True(vm.TryApplyChainStatus(7, TaskQueueItemStatus.Success));
        Assert.Equal(TaskQueueItemStatus.Success, vm.Status);
        Assert.Equal(1, vm.CompletedChainCount);

        // Rows that were never assigned chain ids keep the direct status writes.
        var legacy = new TaskQueueItemViewModel("Fight", "fight", true);
        Assert.Empty(legacy.TaskIds);
        legacy.Status = TaskQueueItemStatus.Error;
        Assert.True(legacy.IsStatusError);
        Assert.False(legacy.TryApplyChainStatus(1, TaskQueueItemStatus.Success));
    }

    [Fact]
    public async Task TaskQueuePage_MultiChainCallbacks_AggregateWithoutLastChainOverwrite()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.UserDataUpdate, "update", enabled: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var row = Assert.Single(vm.Tasks);
        row.SetTaskIds([1, 2]);
        MapCoreTaskId(fixture.Runtime.SessionService, 1, 0);
        MapCoreTaskId(fixture.Runtime.SessionService, 2, 0);

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "TaskChainStart", """{"task_chain":"OperBox","taskid":1}""", DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "TaskChainStart", """{"task_chain":"Depot","taskid":2}""", DateTimeOffset.UtcNow));
        Assert.Equal(TaskQueueItemStatus.Running, row.Status);

        // The first chain finishing must not collapse the row to Completed; the sibling
        // is still running.
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "TaskChainCompleted", """{"task_chain":"OperBox","taskid":1}""", DateTimeOffset.UtcNow));
        Assert.Equal(TaskQueueItemStatus.Running, row.Status);
        Assert.Equal(1, row.CompletedChainCount);

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(0, "TaskChainCompleted", """{"task_chain":"Depot","taskid":2}""", DateTimeOffset.UtcNow));
        Assert.Equal(TaskQueueItemStatus.Success, row.Status);
        Assert.Equal(2, row.CompletedChainCount);
    }

    [Fact]
    public async Task SessionService_MapsEveryAppendedChainToItsQueueIndex()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.UserDataUpdate, "update", enabled: true)).Success);
        Assert.True((await fixture.Runtime.SessionService.ConnectAsync("127.0.0.1:5555", "General", null)).Success);

        var appended = await fixture.Runtime.SessionService.AppendTasksFromCurrentProfileAsync();

        Assert.True(appended.Success);
        Assert.Equal(2, appended.Value);
        var ids = fixture.Runtime.SessionService.GetCoreTaskIdsForQueueIndex(0);
        Assert.Equal(2, ids.Count);
        Assert.Equal([1, 2], ids);
        Assert.Empty(fixture.Runtime.SessionService.GetCoreTaskIdsForQueueIndex(1));
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

        public List<(int TaskId, string ParamsJson)> SetTaskParamsCalls { get; } = new();

        public Queue<CoreResult<bool>> SetTaskParamsResults { get; } = new();

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));

        public Task<CoreResult<bool>> SetTaskParamsAsync(int taskId, string paramsJson, CancellationToken cancellationToken = default)
        {
            SetTaskParamsCalls.Add((taskId, paramsJson));
            return Task.FromResult(SetTaskParamsResults.Count > 0
                ? SetTaskParamsResults.Dequeue()
                : CoreResult<bool>.Ok(true));
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
