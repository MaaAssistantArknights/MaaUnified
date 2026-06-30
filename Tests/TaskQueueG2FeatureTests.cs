using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class TaskQueueG2FeatureTests
{
    private static readonly byte[] SinglePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7+W0cAAAAASUVORK5CYII=");

    [Fact]
    public async Task StartAsync_ShouldFlushDirtyBoundModulesBeforeQueueAndStart()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "flush-before-start";

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        Assert.Equal(1, fixture.Bridge.StartCallCount);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var appendedParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal("flush-before-start", appendedParams["account_name"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartUpModule_SaveAsync_ShouldPersistMacUseBundledAdbToProfileAndReload(bool useBundledAdb)
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        Assert.True(fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile));
        profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(!useBundledAdb);

        var shared = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            AdbPath = fixture.ReadyAdbPath,
            MacUseBundledAdb = !useBundledAdb,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();

        vm.StartUpModule.MacUseBundledAdb = useBundledAdb;

        Assert.True(await vm.StartUpModule.SaveAsync());
        Assert.Equal(useBundledAdb, profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey]?.GetValue<bool>());

        var queueResult = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queueResult.Success);
        var task = Assert.Single(queueResult.Value!);
        var (dto, issues) = TaskParamCompiler.ReadStartUp(
            task,
            profile,
            fixture.Runtime.ConfigurationService.CurrentConfig,
            strict: true);
        Assert.Empty(issues);
        Assert.Equal(useBundledAdb, dto.MacUseBundledAdb);

        var reloadedShared = new ConnectionGameSharedStateViewModel();
        ConnectionGameProfileSync.ReadFromProfile(profile, reloadedShared, tolerateMissing: false);
        var reloadedVm = new TaskQueuePageViewModel(fixture.Runtime, reloadedShared);
        await reloadedVm.InitializeAsync();
        reloadedVm.SelectedTask = Assert.Single(reloadedVm.Tasks);
        await reloadedVm.WaitForPendingBindingAsync();

        Assert.Equal(useBundledAdb, reloadedVm.StartUpModule.MacUseBundledAdb);
    }

    [Fact]
    public async Task StartAsync_WhenAutoConnectFails_ShouldRaiseConnectFailedDialog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.ForceConnectFailure = true;

        var raised = new List<DialogErrorRaisedEvent>();
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, e) => raised.Add(e);
        var navigatedSection = string.Empty;
        var sharedState = new ConnectionGameSharedStateViewModel
        {
            RetryOnDisconnected = false,
            AllowAdbRestart = false,
            AllowAdbHardRestart = false,
        };
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            sharedState,
            navigateToSettingsSection: section => navigatedSection = section);
        await vm.InitializeAsync();

        await vm.StartAsync();

        var dialogError = Assert.Single(
            raised,
            error => string.Equals(error.Context, "TaskQueue.Start", StringComparison.Ordinal));
        Assert.Equal(UiErrorCode.ConnectFailed, dialogError.Result.Error?.Code);
        Assert.Equal(string.Empty, navigatedSection);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
    }

    [Fact]
    public async Task StopAsync_ShouldFlushDirtyBoundModulesBeforeStop()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SelectedTask = Assert.Single(vm.Tasks);
        await vm.WaitForPendingBindingAsync();
        vm.StartUpModule.AccountName = "before-start";

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        vm.StartUpModule.AccountName = "before-stop";

        await vm.StopAsync();

        Assert.Equal(1, fixture.Bridge.StopCallCount);
        var paramsResult = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(paramsResult.Success);
        Assert.Equal("before-stop", paramsResult.Value?["account_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task StartAndStopAsync_ShouldUpdateAchievementTracker()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        await vm.StopAsync();

        Assert.Equal(2, fixture.Runtime.AchievementTrackerService.GetProgress("TaskChainKing"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgress("MissionStartCount"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("UseDaily"));

        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync("zh-cn");
        Assert.True(snapshot.Success);
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "TacticalRetreat" && item.IsUnlocked);
    }

    [Fact]
    public async Task RunningState_ShouldBlockQueueMutationsAndEnabledToggle()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        Assert.True(vm.IsRunning);

        var beforeCount = vm.Tasks.Count;
        await vm.AddTaskAsync();
        Assert.Equal(beforeCount, vm.Tasks.Count);
        Assert.False(string.IsNullOrWhiteSpace(vm.LastErrorMessage));

        var originalEnabled = vm.Tasks[0].IsEnabled;
        vm.Tasks[0].IsEnabled = !originalEnabled;
        var reverted = await WaitForConditionAsync(() => vm.Tasks[0].IsEnabled == originalEnabled);
        Assert.True(reverted);
    }

    [Fact]
    public async Task StartAsync_ShouldEnterRunningUiStateWhileAutoConnectIsPending()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.ConnectDelay = TimeSpan.FromMilliseconds(200);
        fixture.Bridge.ConnectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var shared = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            ConnectConfig = TestConnectionFixtureSupport.ReadyConnectConfig,
            AdbPath = fixture.ReadyAdbPath,
            MacUseBundledAdb = false,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();

        var startTask = vm.StartAsync();
        await fixture.Bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(vm.IsRunning);
        Assert.False(vm.CanEdit);
        Assert.True(vm.CanToggleRun);
        Assert.True(vm.IsOwnRunActive);
        Assert.Equal(vm.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), vm.RunButtonText);

        await startTask;

        Assert.True(vm.IsRunning);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(SessionState.Running, vm.CurrentSessionState);
    }

    [Fact]
    public async Task StartAsync_AutoConnectFailure_ShouldReturnToEditableStateAndReportError()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        fixture.Bridge.ForceConnectFailure = true;

        var shared = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = TestConnectionFixtureSupport.ReadyConnectAddress,
            AdbPath = fixture.ReadyAdbPath,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await vm.InitializeAsync();

        await vm.StartAsync();

        Assert.False(vm.IsRunning);
        Assert.True(vm.CanEdit);
        Assert.True(vm.CanToggleRun);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Contains("连接失败", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildConnectFailureMessage_WhenConnectResultHasSpecificDiagnostic_ShouldKeepDiagnosticFirstLine()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(
            fixture.Runtime,
            new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "127.0.0.1:5554",
                ConnectConfig = "General",
                AdbPath = fixture.ReadyAdbPath,
            });
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        var diagnosticMessage = string.Join(
            Environment.NewLine,
            "无法连接到这个地址或端口。",
            "请确认模拟器已启动，连接地址和端口正确；常见 ADB 端口为 5555。");
        var connectResult = UiOperationResult.Fail(
            UiErrorCode.ConnectFailed,
            diagnosticMessage,
            "probe=tcp host=127.0.0.1 port=5554 timeoutMs=750");

        var message = Assert.IsType<string>(
            typeof(TaskQueuePageViewModel)
                .GetMethod("BuildConnectFailureMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [connectResult]));
        var firstLine = message
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First();
        var localized = DialogTextCatalog.LocalizeErrorResult(
            "zh-cn",
            UiOperationResult.Fail(UiErrorCode.ConnectFailed, message, connectResult.Error?.Details));

        Assert.Equal("无法连接到这个地址或端口。", firstLine);
        Assert.Equal("无法连接到这个地址或端口。", localized.Message);
        Assert.Equal(localized.Message, localized.Error?.Message);
        Assert.NotEqual("连接模拟器失败。", localized.Message);
        Assert.Contains("连接失败。请", message, StringComparison.Ordinal);
        Assert.DoesNotContain("连接回调：无法连接到这个地址或端口。", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zh-cn", "连接失败，正在尝试通过 ADB 重新连接。", "Connection failed")]
    [InlineData("en-us", "Connection failed. Trying to reconnect by ADB.", "连接失败")]
    public async Task ConnectionRecoveryAttemptLog_ShouldUseCurrentLanguageOnly(
        string language,
        string expected,
        string unexpected)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SetLanguage(language);

        typeof(TaskQueuePageViewModel)
            .GetMethod("AppendConnectionRecoveryAttemptLog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, ["正在尝试通过 ADB 重新连接。", "Trying to reconnect by ADB."]);

        var content = Assert.Single(vm.LogCards).PrimaryContent;
        Assert.Equal(expected, content);
        Assert.DoesNotContain(unexpected, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_TaskChainStart_WithTaskIndex_ShouldUpdateOnlyTargetTask()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Equal(2, vm.Tasks.Count);

        var callback = new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":1,"run_id":"run-g2"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Idle, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[1].Status);
    }

    [Fact]
    public async Task Callback_TaskChainStart_ShouldAppendWpfTaskStartLog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-log"}""",
            DateTimeOffset.UtcNow));

        var card = Assert.Single(vm.LogCards);
        Assert.Equal("开始任务: fight-a", card.PrimaryContent);
    }

    [Fact]
    public async Task LanguageSwitch_ShouldRefreshLiveTaskTextWithoutBacktrackingExistingRuntimeLogs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "Fight")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        var taskBefore = Assert.Single(vm.Tasks);
        var displayNameBefore = taskBefore.DisplayName;

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-language"}""",
            DateTimeOffset.UtcNow));

        var startLogBefore = Assert.Single(vm.LogCards).PrimaryContent;

        vm.SetLanguage("en-us");

        var taskAfter = Assert.Single(vm.Tasks);
        Assert.Equal("Combat", taskAfter.DisplayName);
        Assert.NotEqual(displayNameBefore, taskAfter.DisplayName);
        Assert.Equal(startLogBefore, Assert.Single(vm.LogCards).PrimaryContent);

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "TaskChainCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-language"}""",
            DateTimeOffset.UtcNow));

        var logEntries = vm.LogCards
            .SelectMany(card => card.Items)
            .ToArray();
        Assert.Equal(startLogBefore, logEntries[0].Content);
        Assert.Contains("Combat", logEntries[^1].Content, StringComparison.Ordinal);
        Assert.NotEqual(startLogBefore, logEntries[^1].Content);
    }

    [Fact]
    public async Task Callback_TaskChainCompleted_ShouldUseSeparateLogCardFromTaskStart()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "开始唤醒")).Success);
        fixture.Bridge.NextImageBytes = SinglePixelPng;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"StartUp","task_index":0,"run_id":"run-g2-startup-split"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "TaskChainCompleted",
            """{"task_chain":"StartUp","task_index":0,"run_id":"run-g2-startup-split"}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(2, vm.LogCards.Count);
        Assert.Contains("开始任务: 开始唤醒", vm.LogCards[0].PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("完成任务: 开始唤醒", vm.LogCards[1].PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitch_AfterFightSanitySnapshot_ShouldUseCurrentLanguageForFutureLogs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "Fight")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10010,
            "SubTaskExtraInfo",
            """{"what":"SanityBeforeStage","details":{"current_sanity":96,"max_sanity":135,"report_time":"2026-04-03 12:00:00.000"}}""",
            DateTimeOffset.UtcNow));

        vm.SetLanguage("en-us");

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10011,
            "TaskChainCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-sanity-language"}""",
            DateTimeOffset.UtcNow));

        var content = Assert.Single(vm.LogCards).PrimaryContent;
        Assert.Contains("Sanity:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("理智:", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_StageDrops_ShouldUseSeparateLogCardAndRequestThumbnail()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        fixture.Bridge.NextImageBytes = SinglePixelPng;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-stage-drops"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskExtraInfo",
            """{"task_chain":"Fight","task_index":0,"what":"StageDrops","details":{"stats":[{"itemName":"源岩","quantity":2,"addQuantity":1}],"stage":{"stageCode":"1-7"},"cur_times":1}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.Bridge.GetImageCallCount > 0));

        Assert.Equal(2, vm.LogCards.Count);
        Assert.Equal("开始任务: fight-a", vm.LogCards[0].PrimaryContent);
        Assert.Contains("1-7", vm.LogCards[1].PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("源岩", vm.LogCards[1].PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendSystemLog_WithTimestampedMultilineText_ShouldCreateSingleCard()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.AppendSystemLog("""
            10:31:01
            开始任务：信用收支
            10:32:22
            完成任务：访问好友
            """);

        var card = Assert.Single(vm.LogCards);
        Assert.Contains("10:31:01", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("开始任务：信用收支", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("10:32:22", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("完成任务：访问好友", card.PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_MallCompletedLogs_ShouldNotStickToPreviousCard()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "信用收支")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Mall","task_index":0,"run_id":"run-g2-mall-split"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskCompleted",
            """{"task_chain":"Mall","task_index":0,"sub_task":"ProcessTask","details":{"task":"VisitNextBlack"}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(2, vm.LogCards.Count);
        Assert.Contains("开始任务: 信用收支", vm.LogCards[0].PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("完成任务: 访问好友", vm.LogCards[1].PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_InfrastClueTasks_ShouldUpdateAchievementTracker()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            30001,
            "SubTaskCompleted",
            """{"task_chain":"Infrast","sub_task":"ProcessTask","details":{"task":"UnlockClues"}}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            30002,
            "SubTaskCompleted",
            """{"task_chain":"Infrast","sub_task":"ProcessTask","details":{"task":"SendClues"}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("ClueUse"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgress("ClueObsession"));
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("ClueSend"));
    }

    [Fact]
    public async Task Callback_InfrastConfirmButton_ShouldAttachThumbnailWithoutAddingPlaceholderLog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "base-a")).Success);
        fixture.Bridge.NextImageBytes = SinglePixelPng;

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Infrast","task_index":0,"run_id":"run-g2-infrast-thumb"}""",
            DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskExtraInfo",
            """{"task_chain":"Infrast","task_index":0,"what":"InfrastConfirmButton","details":{}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.Bridge.GetImageCallCount > 0));

        var card = Assert.Single(vm.LogCards);
        Assert.Single(card.Items);
        Assert.Equal("开始任务: base-a", card.PrimaryContent);
    }

    [Fact]
    public async Task Callback_UnmappedSubTaskStart_ShouldNotAppendGenericUserLog()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10002,
            "SubTaskStart",
            """{"task_chain":"Fight","task_index":0,"sub_task":"ProcessTask","details":{"task":"NotMappedInWpf"}}""",
            DateTimeOffset.UtcNow));

        Assert.Empty(vm.LogCards);
    }

    [Fact]
    public async Task UiLogService_NonDownloadMessages_ShouldNotLeakIntoTaskQueueLogCards()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        fixture.Runtime.LogService.Info("internal config trace");
        Dispatcher.UIThread.RunJobs(null);
        Assert.Empty(vm.LogCards);

        fixture.Runtime.LogService.Info("download 1/3");
        Dispatcher.UIThread.RunJobs(null);
        Assert.Empty(vm.LogCards);
        Assert.Contains("download", vm.DownloadLogEntry.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_TaskId_ShouldMapToCorrectQueueIndex()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        var callback = new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"StartUp","task_id":2,"run_id":"run-g2-task-id"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Idle, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[1].Status);
        Assert.Equal(1, vm.LastRuntimeStatus?.TaskIndex);

        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("resolveSource=task_id_map", eventLog);
    }

    [Fact]
    public async Task Callback_TaskIndexAndTaskIdConflict_ShouldPreferTaskIndex()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();

        var callback = new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"StartUp","task_index":0,"task_id":2,"run_id":"run-g2-conflict"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Idle, vm.Tasks[1].Status);
        Assert.Equal(0, vm.LastRuntimeStatus?.TaskIndex);

        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("resolveSource=task_index", eventLog);
    }

    [Fact]
    public async Task Callback_NoIndexWithDuplicateChain_ShouldUseHeuristicAndLogWarning()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;
        vm.Tasks[1].Status = TaskQueueItemStatus.Idle;

        var callback = new CoreCallbackEvent(
            20001,
            "SubTaskStart",
            """{"task_chain":"Fight","sub_task":"Stage","run_id":"run-g2-heuristic"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);
        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[1].Status);
        Assert.Equal(1, vm.LastRuntimeStatus?.TaskIndex);

        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("TaskQueue.Callback.ResolveTask", eventLog);
        Assert.Contains("resolveSource=chain_heuristic", eventLog);
    }

    [Fact]
    public async Task Callback_TaskChainError_ShouldSetErrorStatusAndSnapshot()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var callback = new CoreCallbackEvent(
            10000,
            "TaskChainError",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2"}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Error, vm.Tasks[0].Status);
        Assert.NotNull(vm.LastRuntimeStatus);
        Assert.Equal("TaskChainError", vm.LastRuntimeStatus!.Action);
        Assert.Equal(TaskQueueItemStatus.Error, vm.LastRuntimeStatus.Status);
    }

    [Fact]
    public async Task Callback_AllTasksCompleted_WithUseNotifyEnabled_ShouldSendSystemNotificationOncePerRunId()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.UseNotify] = JsonValue.Create("True");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;

        var callback = new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-notify"}""",
            DateTimeOffset.UtcNow);

        await InvokeCallbackAsync(vm, callback);
        await InvokeCallbackAsync(vm, callback);

        Assert.True(await WaitForConditionAsync(() => fixture.NotificationTracker.NotificationCallCount == 1));
        Assert.Equal(1, fixture.NotificationTracker.NotificationCallCount);
        Assert.False(string.IsNullOrWhiteSpace(fixture.NotificationTracker.LastTitle));
        Assert.False(string.IsNullOrWhiteSpace(fixture.NotificationTracker.LastMessage));
    }

    [Fact]
    public async Task Callback_TaskChainError_WithUseNotifyDisabled_ShouldNotSendSystemNotification()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.UseNotify] = JsonValue.Create("False");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10000,
            "TaskChainError",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-no-notify"}""",
            DateTimeOffset.UtcNow));

        await Task.Delay(50);

        Assert.Equal(0, fixture.NotificationTracker.NotificationCallCount);
    }

    [Fact]
    public async Task CallbackPayloadMalformed_ShouldWarnAndContinue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            "{not-json}",
            DateTimeOffset.UtcNow));

        Assert.Contains(
            fixture.Runtime.LogService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                && log.Message.Contains("TaskQueue callback payload parse failed", StringComparison.Ordinal));

        await InvokeCallbackAsync(vm, new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2-malformed"}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(TaskQueueItemStatus.Running, vm.Tasks[0].Status);
        var eventLog = await ReadEventLogAsync(fixture.Root);
        Assert.Contains("TaskQueue.Callback.Parse", eventLog);
    }

    [Fact]
    public async Task Callback_AllTasksCompleted_ShouldExecutePostActionOncePerRunId()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Fight", "fight-a")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.Tasks[0].Status = TaskQueueItemStatus.Running;

        var callback = new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            """{"task_chain":"Fight","task_index":0,"run_id":"run-g2"}""",
            DateTimeOffset.UtcNow);

        await InvokeCallbackAsync(vm, callback);
        await InvokeCallbackAsync(vm, callback);

        Assert.Equal(TaskQueueItemStatus.Success, vm.Tasks[0].Status);
        Assert.Equal(1, fixture.PostAction.ExecuteCount);
    }

    [Fact]
    public async Task StopAsync_ManualStop_ShouldClearAllTaskStatuses()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-a")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup-b")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        await vm.StartAsync();
        Assert.Equal(SessionState.Running, vm.CurrentSessionState);

        vm.Tasks[0].Status = TaskQueueItemStatus.Success;
        vm.Tasks[1].Status = TaskQueueItemStatus.Error;

        await vm.StopAsync();

        Assert.All(vm.Tasks, task => Assert.Equal(TaskQueueItemStatus.Idle, task.Status));
    }

    [Fact]
    public void DailyStageHint_ShouldFollowWpfStyleAndRenderDepotCounts()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"3231\":8,\"3261\":7,\"3232\":5,\"3262\":4}"}""");

        var hint = FightTaskModuleViewModel.BuildDailyResourceHint(
            "zh-cn",
            "Official",
            config,
            new DateTime(2026, 03, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("CE-6: 龙门币", hint);
        Assert.Contains("AP-5: 红票", hint);
        Assert.Contains("LS-6: 经验", hint);
        Assert.Contains("PR-A-1/2: 奶&盾芯片", hint);
        Assert.Contains("(库存 8 & 7 / 5 & 4)", hint);
        Assert.DoesNotContain("今日资源关卡：", hint);
        Assert.DoesNotContain("周一了", hint);
    }

    [Fact]
    public void DailyStageHint_ShouldOmitDepotCountsWhenInventoryIsIncomplete()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"3231\":8}"}""");

        var hint = FightTaskModuleViewModel.BuildDailyResourceHint(
            "zh-cn",
            "Official",
            config,
            new DateTime(2026, 03, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("PR-A-1/2: 奶&盾芯片", hint);
        Assert.DoesNotContain("(库存", hint);
    }

    private static async Task InvokeCallbackAsync(TaskQueuePageViewModel vm, CoreCallbackEvent callback)
    {
        var method = typeof(TaskQueuePageViewModel).GetMethod(
            "HandleCallbackCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(vm, [callback]) as Task;
        if (task is null)
        {
            throw new InvalidOperationException("HandleCallbackCoreAsync invocation returned null.");
        }

        await task;
    }

    private static async Task<string> ReadEventLogAsync(string root)
    {
        var path = Path.Combine(root, "debug", "avalonia-ui-events.log");
        for (var i = 0; i < 20; i++)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }

            await Task.Delay(10);
        }

        return string.Empty;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, int retry = 60, int delayMs = 20)
    {
        for (var i = 0; i < retry; i++)
        {
            Dispatcher.UIThread.RunJobs(null);
            if (predicate())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            MAAUnifiedRuntime runtime,
            TaskQueueFeatureService taskQueue,
            CapturingBridge bridge,
            CountingPostActionFeatureService postAction,
            NotificationTrackingPlatformCapabilityService notificationTracker,
            string readyAdbPath)
        {
            Root = root;
            Runtime = runtime;
            TaskQueue = taskQueue;
            Bridge = bridge;
            PostAction = postAction;
            NotificationTracker = notificationTracker;
            ReadyAdbPath = readyAdbPath;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public CapturingBridge Bridge { get; }

        public CountingPostActionFeatureService PostAction { get; }

        public NotificationTrackingPlatformCapabilityService NotificationTracker { get; }

        public string ReadyAdbPath { get; }

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
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            }
            var readyAdbPath = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "taskqueue-g2-ready");

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
            var notificationTracker = new NotificationTrackingPlatformCapabilityService(capability);
            var connect = new ConnectFeatureService(session, config, log, bridge, root);
            var postAction = new CountingPostActionFeatureService();
            var achievementTracker = new AchievementTrackerService(config, root);

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
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = notificationTracker,
                OverlayFeatureService = new OverlayFeatureService(notificationTracker),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, notificationTracker, diagnostics),
                AchievementTrackerService = achievementTracker,
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = postAction,
            };

            return new TestFixture(root, runtime, taskQueue, bridge, postAction, notificationTracker, readyAdbPath);
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

    private sealed class CapturingBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _taskId;
        private bool _connected;
        private bool _running;

        public List<CoreTaskRequest> AppendedTasks { get; } = [];

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public int GetImageCallCount { get; private set; }

        public TimeSpan ConnectDelay { get; set; }

        public bool ForceConnectFailure { get; set; }

        public TaskCompletionSource<bool>? ConnectStarted { get; set; }

        public byte[]? NextImageBytes { get; set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public async Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectStarted?.TrySetResult(true);
            if (ConnectDelay > TimeSpan.Zero)
            {
                await Task.Delay(ConnectDelay, cancellationToken);
            }

            _connected = !ForceConnectFailure && !string.IsNullOrWhiteSpace(connectionInfo.Address);
            return _connected
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "connect failed"));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendedTasks.Add(task);
            return Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            if (!_connected)
            {
                return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "not connected")));
            }

            _running = true;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            var wasRunning = _running;
            _running = false;
            return Task.FromResult(wasRunning
                ? CoreResult<bool>.Ok(true)
                : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StopFailed, "not running")));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, _connected, _running)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            GetImageCallCount++;
            return Task.FromResult(NextImageBytes is { Length: > 0 } bytes
                ? CoreResult<byte[]>.Ok(bytes.ToArray())
                : CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

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

    private sealed class CountingPostActionFeatureService : IPostActionFeatureService
    {
        public int ExecuteCount { get; private set; }

        public Task<UiOperationResult<PostActionConfig>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionConfig>.Ok(new PostActionConfig(), "Loaded."));

        public Task<UiOperationResult> SaveAsync(PostActionConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("Saved."));

        public Task<UiOperationResult<PostActionPreview>> GetCapabilityPreviewAsync(PostActionConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionPreview>.Ok(
                new PostActionPreview(false, [], []),
                "Previewed."));

        public Task<UiOperationResult<PostActionPreview>> ValidateSelectionAsync(PostActionConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionPreview>.Ok(
                new PostActionPreview(false, [], []),
                "Validated."));

        public Task<UiOperationResult> ExecuteAfterCompletionAsync(
            PostActionExecutionContext context,
            PostActionConfig? configOverride = null,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult(UiOperationResult.Ok("Post action executed."));
        }
    }
}
