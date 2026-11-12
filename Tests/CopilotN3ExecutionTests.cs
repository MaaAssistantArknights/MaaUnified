using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class CopilotN3ExecutionTests
{
    [Fact]
    public async Task CopilotFlow_StrictPump_ImportExecuteStopFeedback_ShouldCompleteByCallbackPump()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var filePath = fixture.CreateCopilotFile();
        fixture.ViewModel.FilePath = filePath;
        await fixture.ViewModel.ImportFromFileAsync();
        Assert.Single(fixture.ViewModel.Items);
        Assert.Equal(filePath, fixture.ViewModel.SelectedItem?.SourcePath);
        Assert.Equal(string.Empty, fixture.ViewModel.LastErrorMessage);
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Copilot.ImportFile"));

        await fixture.ViewModel.StartAsync();
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Queued", fixture.ViewModel.SelectedItem?.Status);
        Assert.Equal("Copilot", fixture.Runtime.SessionService.CurrentRunOwner);

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.Bridge.Publish(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.True(await WaitForAsync(() => fixture.ViewModel.SelectedItem?.Status == "Running"));

        await fixture.ViewModel.StopAsync();
        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Equal("Stopped", fixture.ViewModel.SelectedItem?.Status);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        fixture.Bridge.Publish(new CoreCallbackEvent(
            10004,
            "TaskChainStopped",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.True(await WaitForAsync(() => fixture.ViewModel.SelectedItem?.Status == "Stopped"));
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        await fixture.ViewModel.SendLikeAsync(true);
        Assert.Contains("点赞", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(string.Empty, fixture.ViewModel.LastErrorMessage);
        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("CopilotLikeGiven"));
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Copilot.Feedback"));
    }

    [Fact]
    public async Task CopilotFlow_InternalEntry_ImportExecuteStopFeedback_ShouldCompleteWithoutReflection()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var filePath = fixture.CreateCopilotFile();
        fixture.ViewModel.FilePath = filePath;
        await fixture.ViewModel.ImportFromFileAsync();
        Assert.Equal(string.Empty, fixture.ViewModel.LastErrorMessage);

        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.Equal("Running", fixture.ViewModel.SelectedItem?.Status);

        await fixture.ViewModel.StopAsync();
        Assert.Equal("Stopped", fixture.ViewModel.SelectedItem?.Status);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.Equal("Stopped", fixture.ViewModel.SelectedItem?.Status);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        await fixture.ViewModel.SendLikeAsync(false);
        Assert.Contains("点踩", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(string.Empty, fixture.ViewModel.LastErrorMessage);
    }

    [Fact]
    public async Task CopilotManualStop_WithScriptSettings_ShouldShareSingleEndScriptLaunch()
    {
        var postAction = new CountingPostActionFeatureService();
        await using var fixture = await CopilotN3Fixture.CreateAsync(postAction);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        if (fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            profile.Values[MAAUnified.Compat.Constants.ConfigurationKeys.CopilotWithScript] = JsonValue.Create(true);
            profile.Values[MAAUnified.Compat.Constants.ConfigurationKeys.ManualStopWithScript] = JsonValue.Create(true);
        }

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        await fixture.ViewModel.StopAsync();
        Assert.Equal(1, postAction.EndScriptExecuteCount);

        // WPF shares one launch slot per run: the late completion callback must not relaunch.
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.Equal(1, postAction.EndScriptExecuteCount);
    }

    [Fact]
    public async Task CopilotManualStop_WithoutManualStopWithScript_ShouldNotLaunchEndScript()
    {
        var postAction = new CountingPostActionFeatureService();
        await using var fixture = await CopilotN3Fixture.CreateAsync(postAction);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        if (fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            profile.Values[MAAUnified.Compat.Constants.ConfigurationKeys.CopilotWithScript] = JsonValue.Create(true);
        }

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        await fixture.ViewModel.StopAsync();
        Assert.Equal(0, postAction.EndScriptExecuteCount);
    }

    [Fact]
    public async Task CopilotCompletion_WithoutCopilotWithScript_ShouldNotLaunchEndScript()
    {
        var postAction = new CountingPostActionFeatureService();
        await using var fixture = await CopilotN3Fixture.CreateAsync(postAction);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(0, postAction.EndScriptExecuteCount);
    }

    [Fact]
    public async Task CopilotCompletion_WithCopilotWithScript_ShouldLaunchEndScriptOnce()
    {
        var postAction = new CountingPostActionFeatureService();
        await using var fixture = await CopilotN3Fixture.CreateAsync(postAction);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        if (fixture.Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            profile.Values[MAAUnified.Compat.Constants.ConfigurationKeys.CopilotWithScript] = JsonValue.Create(true);
        }

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(1, postAction.EndScriptExecuteCount);

        // A duplicate AllTasksCompleted for the finished run must not relaunch the script.
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.Equal(1, postAction.EndScriptExecuteCount);
    }

    [Fact]
    public async Task RuntimeCallbacks_UnsupportedLevel_ShouldNotUnlockMapOutdated_WpfParity()
    {
        // WPF AsstProxy 对齐：运行期 UnsupportedLevel 回调仅记录错误日志（WPF 另触发资源更新，属既有 non-goal），
        // 不解锁 MapOutdated——该成就在加载（FindMap 失败）/启动前校验失败时解锁
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10002,
            "SubTaskExtraInfo",
            $$$"""{"task_chain":"Copilot","task_id":{{{taskId}}},"what":"UnsupportedLevel","details":{}}""",
            DateTimeOffset.UtcNow));
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10003,
            "TaskChainCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("UseCopilot"));
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync("zh-cn");
        Assert.True(snapshot.Success);
        Assert.DoesNotContain(snapshot.Value!.Items, item => item.Id == "MapOutdated" && item.IsUnlocked);
    }

    [Fact]
    public async Task RuntimeCallbacks_ParadoxTaskChainCompleted_ShouldNotCountUseCopilot_WpfParity()
    {
        // WPF AsstProxy 对齐：isCopilotTaskChain 仅含 Copilot/SSSCopilot，ParadoxCopilot 完成不累计 UseCopilot
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "mem_12fce_1-obt/memory/level_memory_12fce_1": {
                    "code": "mem_12fce_1",
                    "levelId": "obt/memory/level_memory_12fce_1",
                    "name": "12F的悖论模拟",
                    "stageId": "mem_12fce_1"
                  }
                }
                """,
            battleDataJson: """
                {
                  "chars": {
                    "char_009_12fce": { "name": "12F", "profession": "CASTER" }
                  }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 2;
        await vm.ImportFromClipboardAsync("""{"stage_name":"mem_12fce_1","type":"ParadoxCopilot"}""");
        vm.UseCopilotList = true;
        await vm.StartAsync();

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainCompleted",
            $$"""{"task_chain":"ParadoxCopilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(0, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("UseCopilot"));
    }

    [Fact]
    public async Task SubmitLoadedFeedbackAsync_LikeSuccess_ShouldCountCopilotLikeGiven_WpfParity()
    {
        // WPF LikeWebJson 对齐：加载作业点赞成功累计 CopilotLikeGiven 成就
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","copilot_id":456}""");
        await vm.SubmitLoadedFeedbackAsync(true);
        Assert.True(await WaitForAsync(() => fixture.FeedbackHandler.CallCount == 1));

        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("CopilotLikeGiven"));
    }

    [Fact]
    public async Task SubmitLoadedFeedbackAsync_DislikeSuccess_ShouldNotCountCopilotLikeGiven_WpfParity()
    {
        // WPF DislikeWebJson 对齐：点踩成功不累计 CopilotLikeGiven 成就
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","copilot_id":457}""");
        await vm.SubmitLoadedFeedbackAsync(false);
        Assert.True(await WaitForAsync(() => fixture.FeedbackHandler.CallCount == 1));

        Assert.Equal(0, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("CopilotLikeGiven"));
    }

    [Fact]
    public async Task RuntimeCallbacks_TaskChainCompletedThenAllTasksCompleted_ShouldCountUseCopilotOnce_WpfParity()
    {
        // WPF 对齐：UseCopilot 成就进度仅 TaskChainCompleted 累计，AllTasksCompleted 不重复计数
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10003,
            "TaskChainCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.Equal(1, fixture.Runtime.AchievementTrackerService.GetProgressToGroup("UseCopilot"));
    }

    [Fact]
    public async Task AllTasksCompleted_CopilotChain_ShouldSendCompletionNotification_WpfParity()
    {
        // WPF AsstProxy.AllTasksCompleted 对齐（冻结 :1186、:1279-1282）：isMainTaskQueueAllCompleted 为 false 时
        // else-if isCopilotTaskChain（仅 Copilot/SSSCopilot，:1019）弹「完成任务 + 任务链名」Toast
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        fixture.ViewModel.SetLanguage("en-US");
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.NotificationTracker.NotificationCallCount == 1));
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task AllTasksCompleted_SssChain_ShouldSendCompletionNotification_WpfParity()
    {
        // WPF 对齐：isCopilotTaskChain 含 SSSCopilot（冻结 :1019），SSS 作业完成同样弹完成通知
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        fixture.ViewModel.SetLanguage("en-US");
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        await fixture.ViewModel.LoadCurrentFromClipboardAsync("""{"type":"SSS","stage_name":"sss-test","copilot_id":66}""");
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"SSSCopilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.NotificationTracker.NotificationCallCount == 1));
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task AllTasksCompleted_ParadoxChain_ShouldNotSendCompletionNotification_WpfParity()
    {
        // WPF 对齐：isCopilotTaskChain 不含 ParadoxCopilot（冻结 :1019），悖论模拟完成不弹通知
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "mem_12fce_1-obt/memory/level_memory_12fce_1": {
                    "code": "mem_12fce_1",
                    "levelId": "obt/memory/level_memory_12fce_1",
                    "name": "12F的悖论模拟",
                    "stageId": "mem_12fce_1"
                  }
                }
                """,
            battleDataJson: """
                {
                  "chars": {
                    "char_009_12fce": { "name": "12F", "profession": "CASTER" }
                  }
                }
                """);
        fixture.ViewModel.SetLanguage("en-US");
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        fixture.ViewModel.CopilotTabIndex = 2;
        await fixture.ViewModel.ImportFromClipboardAsync("""{"stage_name":"mem_12fce_1","type":"ParadoxCopilot"}""");
        fixture.ViewModel.UseCopilotList = true;
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"ParadoxCopilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs(null);
        Assert.Equal(0, fixture.NotificationTracker.NotificationCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task TaskChainError_ShouldUseInAppFallback_WhenUseNotifyDisabled()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues["GUI.UseNotify"] = JsonValue.Create(false);
        fixture.ViewModel.SetLanguage("en-US");
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "TaskChainError",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.NotificationTracker.NotificationCallCount == 1));
        Assert.False(fixture.NotificationTracker.LastNotification?.UseSystemNotification);
    }

    [Fact]
    public async Task TaskChainError_ButNotSubTaskError_ShouldSendNotification()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        fixture.ViewModel.SetLanguage("en-US");
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10000,
            "TaskChainError",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.True(await WaitForConditionAsync(() => fixture.NotificationTracker.NotificationCallCount == 1));
        Assert.Contains("error", fixture.NotificationTracker.LastTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<bool> predicate,
        int retry = 60,
        int delayMs = 20)
    {
        for (var attempt = 0; attempt < retry; attempt++)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return predicate();
    }

    [Fact]
    public async Task StartAsync_ShouldAppendWpfStyleStartLogs_WithoutRawCallbackDump()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var filePath = fixture.CreateCopilotFile();
        fixture.ViewModel.FilePath = filePath;
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        Assert.Contains(
            fixture.ViewModel.Logs,
            log => string.Equals(log.Content, "正在连接模拟器……", StringComparison.Ordinal));
        Assert.Contains(
            fixture.ViewModel.Logs,
            log => string.Equals(log.Content, "正在运行中……", StringComparison.Ordinal));

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));

        Assert.DoesNotContain(
            fixture.ViewModel.Logs,
            log => log.Content.Contains("TaskChainStart", StringComparison.Ordinal)
                || log.Content.Contains("\"task_id\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_BattleFormation_ShouldAppendUserFacingLog()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var filePath = fixture.CreateCopilotFile();
        fixture.ViewModel.FilePath = filePath;
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            20001,
            "SubTaskExtraInfo",
            $@"{{""task_chain"":""Copilot"",""task_id"":{taskId},""what"":""BattleFormation"",""details"":{{""formation"":[""能天使"",""银灰""]}}}}",
            DateTimeOffset.UtcNow));

        Assert.Contains(
            fixture.ViewModel.Logs,
            log => log.Content.Contains("开始编队", StringComparison.Ordinal)
                && log.Content.Contains("能天使", StringComparison.Ordinal)
                && log.Content.Contains("银灰", StringComparison.Ordinal));
        Assert.DoesNotContain(
            fixture.ViewModel.Logs,
            log => log.Content.Contains("SubTaskExtraInfo", StringComparison.Ordinal)
                || log.Content.Contains("\"formation\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopilotStart_WhenTaskQueueOwnsRun_ShouldBeBlocked()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        Assert.True(fixture.Runtime.SessionService.TryBeginRun("TaskQueue", out _));

        var filePath = fixture.CreateCopilotFile();
        fixture.ViewModel.FilePath = filePath;
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        Assert.Contains("运行所有者", fixture.ViewModel.LastErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public async Task CopilotStart_WhenNativeStartFails_ShouldRecoverQueueAndRequireReconnect()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        fixture.Bridge.StartResults.Enqueue(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.StartFailed, "start failed")));
        fixture.Bridge.StartResults.Enqueue(CoreResult<bool>.Ok(true));
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();

        await fixture.ViewModel.StartAsync();

        Assert.Equal(1, fixture.Bridge.RecoverTaintedTaskQueueCallCount);
        Assert.Empty(fixture.Bridge.ActiveQueuedTasks);
        Assert.Equal(SessionState.Idle, fixture.Runtime.SessionService.CurrentState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);

        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        await fixture.ViewModel.StartAsync();

        Assert.Equal(2, fixture.Bridge.StartCallCount);
        Assert.Single(fixture.Bridge.ActiveQueuedTasks);
        Assert.Equal(SessionState.Running, fixture.Runtime.SessionService.CurrentState);
    }

    [Fact]
    public async Task CopilotStart_ShouldApplyCurrentInstanceOptionsBeforeNativeStart()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        var profile = fixture.Runtime.ConfigurationService.CurrentConfig.Profiles[
            fixture.Runtime.ConfigurationService.CurrentConfig.CurrentProfile];
        profile.Values["TouchMode"] = "minitouch";
        profile.Values["KillAdbOnExit"] = true;
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();

        await fixture.ViewModel.StartAsync();

        Assert.Equal("minitouch", fixture.Bridge.LastInstanceOptions?.TouchMode);
        Assert.True(fixture.Bridge.LastInstanceOptions?.KillAdbOnExit is true);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
    }

    [Fact]
    public async Task TaskQueueStart_WhenCopilotOwnsRun_ShouldBeBlockedAndLogged()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);
        Assert.True((await fixture.Runtime.TaskQueueFeatureService.AddTaskAsync("StartUp", "startup-a")).Success);

        var taskQueue = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await taskQueue.InitializeAsync();

        Assert.True(fixture.Runtime.SessionService.TryBeginRun("Copilot", out _));
        await taskQueue.StartAsync();

        Assert.Contains("active run owner", taskQueue.LastErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Copilot", taskQueue.LastErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Bridge.StartCallCount);

        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.ErrorLogPath,
            "[TaskQueue.Start.RunOwner]"));
    }

    [Fact]
    public async Task Copilot_StopWithoutCallback_ShouldRecoverUiAndRunOwner()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var filePath = fixture.CreateCopilotFile();
        fixture.ViewModel.FilePath = filePath;
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.Equal("Running", fixture.ViewModel.SelectedItem?.Status);
        Assert.Equal("Copilot", fixture.Runtime.SessionService.CurrentRunOwner);

        await fixture.ViewModel.StopAsync();

        Assert.Equal(SessionState.Connected, fixture.Runtime.SessionService.CurrentState);
        Assert.Equal("Stopped", fixture.ViewModel.SelectedItem?.Status);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task Copilot_CallbackPayloadMalformed_ShouldWarnAndContinue()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var filePath = fixture.CreateCopilotFile();
        fixture.ViewModel.FilePath = filePath;
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            "{not-json}",
            DateTimeOffset.UtcNow));

        Assert.Equal("Running", fixture.ViewModel.SelectedItem?.Status);
        Assert.Contains(
            fixture.Runtime.LogService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.OrdinalIgnoreCase)
                && log.Message.Contains("Copilot callback payload parse failed", StringComparison.Ordinal));
        Assert.True(await WaitForLogContainsAsync(
            fixture.Runtime.DiagnosticsService.EventLogPath,
            "Copilot.Callback.Parse"));

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            3,
            "AllTasksCompleted",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.Equal("Success", fixture.ViewModel.SelectedItem?.Status);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task LoadCurrentFromFileAsync_WhenUseCopilotListEnabled_ShouldKeepLocalFileAsCurrentOnly()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;

        var filePath = fixture.CreateCopilotFile();
        await vm.LoadCurrentFromFileAsync(filePath);

        Assert.Empty(vm.Items);
        Assert.True(vm.HasLoadedCopilot);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_WhenUseCopilotListEnabled_ShouldAutoAddWpfStyleListItem()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;

        var payload = await File.ReadAllTextAsync(fixture.CreateCopilotFile());
        await vm.LoadCurrentFromClipboardAsync(payload);

        var item = Assert.Single(vm.Items);
        Assert.Equal("1-7", item.Name);
        Assert.Equal(string.Empty, item.SourcePath);
        Assert.Equal(payload, item.InlinePayload);
        Assert.True(item.IsChecked);
        Assert.False(item.IsRaid);
        Assert.Equal(0, item.TabIndex);
        Assert.True(vm.HasLoadedCopilot);
    }

    [Fact]
    public async Task StartAsync_WhenUseCopilotListEnabled_ShouldAppendWpfParityPayload()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;
        vm.UseSupportUnitUsage = true;
        vm.SupportUnitUsage = 3;
        vm.AddTrust = true;
        vm.IgnoreRequirements = true;
        vm.UseSanityPotion = true;
        vm.UseFormation = true;
        vm.FormationIndex = 2;
        vm.AddUserAdditional = true;
        vm.UserAdditional = """[{"name":"史尔特尔","skill":3,"module":1}]""";

        var filePath = fixture.CreateCopilotFile();
        await vm.LoadCurrentFromFileAsync(filePath);
        await vm.AddCurrentToListAsync(isRaid: false);
        await vm.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Copilot", appended.Type);
        var payload = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        var list = Assert.IsType<JsonArray>(payload["copilot_list"]);
        var first = Assert.IsType<JsonObject>(list[0]);
        Assert.Equal(filePath, first["filename"]?.GetValue<string>());
        // WPF dev-v2 c2021e694a：列表不再下发 stage_name；手动添加条目按用户输入的导航名下发起覆盖
        Assert.False(first.ContainsKey("stage_name"));
        Assert.Equal("1-7", first["nav_name_override"]?.GetValue<string>());
        Assert.False(first["is_raid"]?.GetValue<bool>() ?? true);
        Assert.True(payload["formation"]?.GetValue<bool>() ?? false);
        Assert.Equal(3, payload["support_unit_usage"]?.GetValue<int>());
        Assert.True(payload["add_trust"]?.GetValue<bool>() ?? false);
        Assert.True(payload["ignore_requirements"]?.GetValue<bool>() ?? false);
        Assert.True(payload["use_sanity_potion"]?.GetValue<bool>() ?? false);
        Assert.Equal(2, payload["formation_index"]?.GetValue<int>());
        var userAdditional = Assert.IsType<JsonArray>(payload["user_additional"]);
        Assert.Single(userAdditional);
    }

    [Fact]
    public async Task StartAsync_WhenUseCopilotListContainsLegacyItemWithoutTabIndex_ShouldNotReject_WpfParity()
    {
        // WPF dev-v2 d6601334f2：列表校验不再依赖 tab_index；旧条目（tab_index 缺失）按作业内容判定类型
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;
        vm.Items.Add(new CopilotItemViewModel("1-7", "主线/故事集/SideStory", fixture.CreateCopilotFile())
        {
            IsChecked = true,
            TabIndex = null,
        });

        await vm.StartAsync();

        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.True(vm.IsRunning);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int retries = 40, int delayMs = 25)
    {
        for (var i = 0; i < retries; i++)
        {
            Dispatcher.UIThread.RunJobs(null);
            if (condition())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    [Fact]
    public async Task SubTaskError_ShouldNotCompleteRunNorNotify_WpfParity()
    {
        // WPF 对齐：ProcSubTaskError 只记录日志，不结束运行、不改列表项状态、不发通知
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        Assert.Equal("Running", fixture.ViewModel.SelectedItem?.Status);

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10102,
            "SubTaskError",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}},"subtask":"ProcessTask","why":"boom"}""",
            DateTimeOffset.UtcNow));

        Assert.True(fixture.ViewModel.IsRunning);
        Assert.Equal("Running", fixture.ViewModel.SelectedItem?.Status);
        Assert.Equal("Copilot", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal(0, fixture.NotificationTracker.NotificationCallCount);
        Assert.Contains("SubTaskError", fixture.ViewModel.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorMissing_WithTwoOrMoreMissingGroups_ShouldUnlockIrreplaceable_WpfParity()
    {
        // WPF 对齐（冻结 AsstProxy.cs:1476-1480）：OperatorMissing 缺失干员分组 >= 2 时解锁 Irreplaceable 成就
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10002,
            "SubTaskError",
            $$$$"""{"task_chain":"Copilot","task_id":{{{{taskId}}}},"subtask":"BattleFormationTask","why":"OperatorMissing","details":{"opers":{"近卫":[{"name":"银灰"}],"狙击":[{"name":"能天使"},{"name":"空弦"}]}}}""",
            DateTimeOffset.UtcNow));

        Assert.Contains(fixture.ViewModel.Logs, log => log.Content.Contains("能天使 / 空弦", StringComparison.Ordinal));
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        Assert.True(snapshot.Success);
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "Irreplaceable" && item.IsUnlocked);
    }

    [Fact]
    public async Task OperatorMissing_WithSingleMissingGroup_ShouldNotUnlockIrreplaceable_WpfParity()
    {
        // WPF 对齐（冻结 AsstProxy.cs:1476-1480）：缺失干员分组 < 2 时仅记录日志，不解锁 Irreplaceable
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10002,
            "SubTaskError",
            $$$$"""{"task_chain":"Copilot","task_id":{{{{taskId}}}},"subtask":"BattleFormationTask","why":"OperatorMissing","details":{"opers":{"近卫":[{"name":"银灰"}]}}}""",
            DateTimeOffset.UtcNow));

        Assert.Contains(fixture.ViewModel.Logs, log => log.Content.Contains("近卫", StringComparison.Ordinal));
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        Assert.True(snapshot.Success);
        Assert.DoesNotContain(snapshot.Value!.Items, item => item.Id == "Irreplaceable" && item.IsUnlocked);
    }

    [Fact]
    public async Task StartStopButtons_ShouldTrackRunningState_WpfParity()
    {
        // WPF 对齐：Start 按钮仅 Idle 时可见，Stop 按钮仅运行中可见
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        Assert.True(fixture.ViewModel.ShowStartButton);
        Assert.False(fixture.ViewModel.ShowStopButton);

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        Assert.False(fixture.ViewModel.ShowStartButton);
        Assert.True(fixture.ViewModel.ShowStopButton);

        await fixture.ViewModel.StopAsync();

        Assert.True(await WaitForAsync(() => fixture.ViewModel.ShowStartButton && !fixture.ViewModel.ShowStopButton));
    }

    [Fact]
    public async Task ListRun_StageDropsSuccess_ShouldUncheckInOrderAndAutoLikeOncePerCopilotId()
    {
        // WPF 对齐：战斗成功按顺序取消勾选；同一作业 id 的全部条目都成功后才自动点赞一次
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        await fixture.ViewModel.ImportFromClipboardAsync(
            """{"stage_name":"1-7","copilot_id":456,"difficulty":3}""");
        Assert.Equal(2, fixture.ViewModel.Items.Count);
        Assert.All(fixture.ViewModel.Items, item => Assert.Equal(456, item.CopilotId));
        fixture.ViewModel.UseCopilotList = true;
        await fixture.ViewModel.StartAsync();
        Assert.Equal("Copilot", fixture.Runtime.SessionService.CurrentRunOwner);
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        PublishStageDropsSuccess(fixture.ViewModel, taskId);

        Assert.True(await WaitForAsync(() => !fixture.ViewModel.Items[0].IsChecked));
        Assert.True(fixture.ViewModel.Items[1].IsChecked);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs(null);
        Assert.Equal(0, fixture.FeedbackHandler.CallCount);

        PublishStageDropsSuccess(fixture.ViewModel, taskId);

        Assert.True(await WaitForAsync(() => !fixture.ViewModel.Items[1].IsChecked));
        Assert.True(await WaitForAsync(() => fixture.FeedbackHandler.CallCount == 1));
    }

    [Fact]
    public async Task StartAsync_WhenUseCopilotList_ShouldSendItemIndexAsId_WpfParity()
    {
        // WPF 对齐（4843310bec AsstCopilotTask.MultiTask.Index）：id 取列表全局序号，乱序重排后按新位置下发
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        AddCopilotListItems(vm, fixture.CreateCopilotFile(), ("A", 101), ("B", 102), ("C", 103));
        vm.Items.Move(0, 2);
        vm.UseCopilotList = true;

        await vm.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var list = Assert.IsType<JsonArray>(JsonNode.Parse(appended.ParamsJson)?["copilot_list"]);
        // dev-v2 c2021e694a 后列表不再下发 stage_name；顺序由 id 与列表项本身断言
        Assert.Equal(["B", "C", "A"], vm.Items.Select(item => item.Name).ToArray());
        Assert.Equal([0, 1, 2], list.Select(node => node!["id"]!.GetValue<int>()).ToArray());
        Assert.Equal([0, 1, 2], vm.Items.Select(item => item.Index).ToArray());
        Assert.All(list, node => Assert.False(((JsonObject)node!).ContainsKey("stage_name")));
    }

    [Fact]
    public async Task ListRun_WhenMiddleItemFailed_ShouldUncheckReportedItemOnly_WpfParity()
    {
        // WPF 对齐（4843310bec CopilotViewModel.CopilotTaskSuccess）：按 Core 回报的 id 取消勾选，
        // 中间项战斗失败后不能误取消第一个仍勾选（失败）的条目。
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        AddCopilotListItems(vm, fixture.CreateCopilotFile(), ("A", 101), ("B", 102), ("C", 103));
        vm.UseCopilotList = true;
        await vm.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        PublishCopilotListFileLoaded(vm, taskId, 0);
        PublishStageDropsSuccess(vm, taskId);
        Assert.True(await WaitForAsync(() => !vm.Items[0].IsChecked));

        // 中间项（id=1）战斗失败：只有加载回报，没有 StageDrops 成功；Core 继续加载第三项
        PublishCopilotListFileLoaded(vm, taskId, 1);
        PublishCopilotListFileLoaded(vm, taskId, 2);
        PublishStageDropsSuccess(vm, taskId);

        Assert.True(await WaitForAsync(() => !vm.Items[2].IsChecked));
        Assert.True(vm.Items[1].IsChecked);
        Assert.False(vm.Items[0].IsChecked);
    }

    [Fact]
    public async Task ListRun_OutOfOrderIds_ShouldLikeOnlyAfterAllItemsOfSameCopilotSucceed_WpfParity()
    {
        // WPF 对齐（4843310bec）：同一 copilotId 的全部列表条目都成功后点赞；Core 回报顺序不要求与列表一致
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        AddCopilotListItems(vm, fixture.CreateCopilotFile(), ("A", 456), ("B", 456), ("C", 456));
        vm.UseCopilotList = true;
        await vm.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        PublishCopilotListFileLoaded(vm, taskId, 2);
        PublishStageDropsSuccess(vm, taskId);
        Assert.True(await WaitForAsync(() => !vm.Items[2].IsChecked));
        Assert.True(vm.Items[0].IsChecked);
        Assert.True(vm.Items[1].IsChecked);

        PublishCopilotListFileLoaded(vm, taskId, 0);
        PublishStageDropsSuccess(vm, taskId);
        Assert.True(await WaitForAsync(() => !vm.Items[0].IsChecked));
        Assert.True(vm.Items[1].IsChecked);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs(null);
        Assert.Equal(0, fixture.FeedbackHandler.CallCount);

        PublishCopilotListFileLoaded(vm, taskId, 1);
        PublishStageDropsSuccess(vm, taskId);
        Assert.True(await WaitForAsync(() => !vm.Items[1].IsChecked));
        Assert.True(await WaitForAsync(() => fixture.FeedbackHandler.CallCount == 1));
    }

    [Fact]
    public async Task ListRun_StageDropsSuccess_WhenRequirementIgnored_ShouldSkipAutoLike()
    {
        // WPF 对齐：BattleFormationOperUnavailable 置位 HasRequirementIgnored 后抑制自动点赞
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        await fixture.ViewModel.ImportFromClipboardAsync(
            """{"stage_name":"1-7","copilot_id":789}""");
        var item = Assert.Single(fixture.ViewModel.Items);
        fixture.ViewModel.UseCopilotList = true;
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10002,
            "SubTaskExtraInfo",
            $$$"""{"task_chain":"Copilot","task_id":{{{taskId}}},"what":"BattleFormationOperUnavailable","details":{"oper_name":"阿米娅","requirement_type":"level"}}""",
            DateTimeOffset.UtcNow));
        PublishStageDropsSuccess(fixture.ViewModel, taskId);

        Assert.True(await WaitForAsync(() => !item.IsChecked));
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs(null);
        Assert.Equal(0, fixture.FeedbackHandler.CallCount);
    }

    [Fact]
    public async Task SubmitLoadedFeedbackAsync_SameIdTwice_ShouldRateOncePerSession_WpfParity()
    {
        // WPF 对齐：_recentlyRatedCopilotId 会话级去重，重新加载同一作业也不能重复点赞
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","copilot_id":456}""");
        Assert.True(vm.CouldLikeWebJson);
        await vm.SubmitLoadedFeedbackAsync(true);
        Assert.True(await WaitForAsync(() => fixture.FeedbackHandler.CallCount == 1));

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","copilot_id":456}""");
        Assert.True(vm.CouldLikeWebJson);
        await vm.SubmitLoadedFeedbackAsync(true);

        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs(null);
        Assert.Equal(1, fixture.FeedbackHandler.CallCount);
        Assert.Contains("本会话已对该作业提交过反馈", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitLoadedFeedbackAsync_DislikeThenLike_ShouldRateOncePerSession_WpfParity()
    {
        // WPF 对齐：RateCopilot 点踩成功后同样占用 _recentlyRatedCopilotId，反向点赞被拦截
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","copilot_id":457}""");
        await vm.SubmitLoadedFeedbackAsync(false);
        Assert.True(await WaitForAsync(() => fixture.FeedbackHandler.CallCount == 1));

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","copilot_id":457}""");
        await vm.SubmitLoadedFeedbackAsync(true);

        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs(null);
        Assert.Equal(1, fixture.FeedbackHandler.CallCount);
    }

    [Fact]
    public async Task ListRun_AutoLike_SecondRunOfSameId_ShouldNotRateAgain_WpfParity()
    {
        // WPF 对齐：_copilotIdList 只管单次运行去重，_recentlyRatedCopilotId 保证同一会话同一 id 自动点赞至多一次
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        await fixture.ViewModel.ImportFromClipboardAsync(
            """{"stage_name":"1-7","copilot_id":458}""");
        fixture.ViewModel.UseCopilotList = true;
        await fixture.ViewModel.StartAsync();
        var taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10001,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        PublishStageDropsSuccess(fixture.ViewModel, taskId);
        Assert.True(await WaitForAsync(() => fixture.FeedbackHandler.CallCount == 1));

        await fixture.ViewModel.StopAsync();

        // 第二次运行同一作业：运行内去重集合已重置，但会话级去重应阻止再次点赞
        fixture.ViewModel.Items[0].IsChecked = true;
        await fixture.ViewModel.StartAsync();
        taskId = fixture.Bridge.LastAppendedTaskId;
        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10002,
            "TaskChainStart",
            $$"""{"task_chain":"Copilot","task_id":{{taskId}}}""",
            DateTimeOffset.UtcNow));
        PublishStageDropsSuccess(fixture.ViewModel, taskId);

        Assert.True(await WaitForAsync(() => !fixture.ViewModel.Items[0].IsChecked));
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs(null);
        Assert.Equal(1, fixture.FeedbackHandler.CallCount);
    }

    [Fact]
    public async Task StartAsync_WhenListStageUnknown_ShouldFailWithWpfParityMessage()
    {
        // WPF 对齐：VerifyCopilotListTask 逐个读取作业内容并用地图数据校验，不支持的关卡拒绝启动
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        var resourceDir = await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "a001_01-activities/a001/level_a001_01": {
                    "code": "GT-1",
                    "levelId": "activities/a001/level_a001_01",
                    "name": "日正当中",
                    "stageId": "a001_01"
                  }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        // O-b 修复后未知关卡在入列时即被拒绝；此处直接注入遗留条目，覆盖启动前内容校验（VerifyCopilotListTask 对齐）
        fixture.ViewModel.Items.Add(new MAAUnified.App.ViewModels.Copilot.CopilotItemViewModel(
            "9-99",
            fixture.ViewModel.Types[0],
            sourcePath: null,
            inlinePayload: """{"stage_name":"9-99"}""")
        {
            IsChecked = true,
            TabIndex = 0,
        });
        fixture.ViewModel.UseCopilotList = true;
        await fixture.ViewModel.StartAsync();

        Assert.Contains("暂不支持", fixture.ViewModel.LastErrorMessage, StringComparison.Ordinal);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal(resourceDir, fixture.ViewModel.ResourceDirectoryOverride);
        // WPF VerifyCopilotListTask 对齐：关卡校验失败解锁 MapOutdated 成就
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "MapOutdated" && item.IsUnlocked);
    }

    [Fact]
    public async Task ImportToList_WhenStageUnknown_ShouldRejectEntryWithWpfParityError()
    {
        // WPF AddCopilotTaskToList 对齐：mapInfo is null（地图数据存在但关卡无法识别）时拒绝入列并打错误日志
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "a001_01-activities/a001/level_a001_01": {
                    "code": "GT-1",
                    "levelId": "activities/a001/level_a001_01",
                    "name": "日正当中",
                    "stageId": "a001_01"
                  }
                }
                """);

        await fixture.ViewModel.ImportFromClipboardAsync("""{"stage_name":"9-99"}""");

        Assert.Empty(fixture.ViewModel.Items);
        Assert.Contains(fixture.ViewModel.Logs, log =>
            log.Content.Contains("9-99", StringComparison.Ordinal)
            && log.Content.Contains("无法识别", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddCurrentToListAsync_WhenStageUnknown_ShouldFailWithWpfParityError()
    {
        // WPF AddCopilotTaskToList 对齐：手动添加未知关卡作业时拒绝入列
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "a001_01-activities/a001/level_a001_01": {
                    "code": "GT-1",
                    "levelId": "activities/a001/level_a001_01",
                    "name": "日正当中",
                    "stageId": "a001_01"
                  }
                }
                """);

        await fixture.ViewModel.LoadCurrentFromClipboardAsync("""{"stage_name":"9-99"}""");
        await fixture.ViewModel.AddCurrentToListAsync(false);

        Assert.Empty(fixture.ViewModel.Items);
        Assert.Contains("无法识别", fixture.ViewModel.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenListStageKnown_ShouldPassContentValidation()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "a001_01-activities/a001/level_a001_01": {
                    "code": "GT-1",
                    "levelId": "activities/a001/level_a001_01",
                    "name": "日正当中",
                    "stageId": "a001_01"
                  }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        await fixture.ViewModel.ImportFromClipboardAsync("""{"stage_name":"GT-1"}""");
        fixture.ViewModel.UseCopilotList = true;
        await fixture.ViewModel.StartAsync();

        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
    }

    [Fact]
    public async Task StartAsync_WhenListItemNameOverridden_ShouldSendNavNameOverride_WpfParity()
    {
        // WPF dev-v2 c2021e694a：仅用户覆盖关卡名时下发 nav_name_override
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "main_01-07": { "code": "1-7", "levelId": "main/level_main_01-07", "name": "暴君", "stageId": "main_01-07" }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;
        await vm.ImportFromClipboardAsync("""{"stage_name":"main_01-07"}""");
        var item = Assert.Single(vm.Items);
        vm.SelectedItem = item;
        vm.CopilotTaskName = "1-7-custom";

        await vm.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var listItem = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson)!["copilot_list"]![0]);
        Assert.Equal("1-7-custom", listItem["nav_name_override"]?.GetValue<string>());
        Assert.False(listItem.ContainsKey("stage_name"));
    }

    [Fact]
    public async Task StartAsync_WhenListMixedContentTypes_ShouldReject_WpfParity()
    {
        // WPF dev-v2 d6601334f2：按作业内容判定类型，混用两种具体类型拒绝启动
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "main_01-07": { "code": "1-7", "levelId": "main/level_main_01-07", "name": "暴君", "stageId": "main_01-07" },
                  "mem_12fce_1": { "code": "12F-1", "levelId": "mem/level_mem_12fce_1", "name": "12F", "stageId": "mem_12fce_1" }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;
        vm.Items.Add(new CopilotItemViewModel("1-7", vm.Types[0], inlinePayload: """{"stage_name":"1-7"}""")
        {
            IsChecked = true,
            TabIndex = null,
        });
        vm.Items.Add(new CopilotItemViewModel("12F-1", vm.Types[2], inlinePayload: """{"stage_name":"mem_12fce_1"}""")
        {
            IsChecked = true,
            TabIndex = null,
        });

        await vm.StartAsync();

        Assert.Contains("不支持混用", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.Contains("悖论模拟", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StartAsync_WhenItemTabIndexDiffersFromCurrentTab_ShouldNotReject_WpfParity()
    {
        // WPF dev-v2 d6601334f2：不再要求列表项 tab_index 与当前页签一致
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "main_01-07": { "code": "1-7", "levelId": "main/level_main_01-07", "name": "暴君", "stageId": "main_01-07" }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;
        vm.Items.Add(new CopilotItemViewModel("1-7", vm.Types[1], inlinePayload: """{"stage_name":"1-7"}""")
        {
            IsChecked = true,
            TabIndex = 1,
        });

        await vm.StartAsync();

        Assert.Single(fixture.Bridge.AppendedTasks);
    }

    [Fact]
    public async Task StartAsync_WhenParadoxOperNameIllegal_ShouldFailWithWpfParityMessage()
    {
        // WPF 对齐：VerifyParadoxTasks 校验悖论列表干员名合法性，非法干员名拒绝启动
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            battleDataJson: """
                {
                  "chars": {
                    "char_002_amiya": { "name": "阿米娅", "profession": "CASTER" }
                  }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 2;
        await vm.ImportFromClipboardAsync("""{"stage_name":"mem_12fce_1","type":"ParadoxCopilot"}""");
        vm.UseCopilotList = true;
        await vm.StartAsync();

        Assert.Contains("干员名不合法", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        // WPF VerifyParadoxTasks 对齐：干员名不合法解锁 MapOutdated 成就
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "MapOutdated" && item.IsUnlocked);
    }

    [Fact]
    public async Task StartAsync_WhenParadoxOperNameLegal_ShouldStartWithOperatorNamedItem()
    {
        // WPF 对齐：悖论条目名取干员名（overview stageId → battle_data），校验通过后启动
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "mem_12fce_1-obt/memory/level_memory_12fce_1": {
                    "code": "mem_12fce_1",
                    "levelId": "obt/memory/level_memory_12fce_1",
                    "name": "12F的悖论模拟",
                    "stageId": "mem_12fce_1"
                  }
                }
                """,
            battleDataJson: """
                {
                  "chars": {
                    "char_009_12fce": { "name": "12F", "profession": "CASTER" }
                  }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 2;
        await vm.ImportFromClipboardAsync("""{"stage_name":"mem_12fce_1","type":"ParadoxCopilot","difficulty":3}""");

        var item = Assert.Single(vm.Items);
        Assert.Equal("12F", item.Name);
        Assert.False(item.IsRaid);

        vm.UseCopilotList = true;
        await vm.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("ParadoxCopilot", appended.Type);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        // WPF 对齐（4843310bec AsstParadoxCopilotTask.MultiTask）：悖论列表条目带 id 与 filename
        var paradoxEntry = Assert.IsType<JsonObject>(
            Assert.Single(Assert.IsType<JsonArray>(JsonNode.Parse(appended.ParamsJson)?["list"])));
        Assert.Equal(0, paradoxEntry["id"]?.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(paradoxEntry["filename"]?.GetValue<string>()));
    }

    private static void AddCopilotListItems(
        CopilotPageViewModel viewModel,
        string filePath,
        params (string Name, int CopilotId)[] items)
    {
        foreach (var (name, copilotId) in items)
        {
            viewModel.Items.Add(new CopilotItemViewModel(name, "主线/故事集/SideStory", filePath)
            {
                CopilotId = copilotId,
                IsChecked = true,
                TabIndex = 0,
            });
        }
    }

    private static void PublishCopilotListFileLoaded(CopilotPageViewModel viewModel, int taskId, int copilotItemId)
    {
        viewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10004,
            "SubTaskExtraInfo",
            $$$"""{"task_chain":"Copilot","task_id":{{{taskId}}},"subtask":"CopilotTask","what":"CopilotListLoadTaskFileSuccess","details":{"file_name":"copilot.json","stage_name":"1-7","id":{{{copilotItemId}}}}}""",
            DateTimeOffset.UtcNow));
    }

    private static void PublishStageDropsSuccess(CopilotPageViewModel viewModel, int taskId)
    {
        viewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10005,
            "SubTaskStart",
            $$$"""{"task_chain":"Copilot","task_id":{{{taskId}}},"subtask":"ProcessTask","details":{"task":"StageDrops-Stars-3"}}""",
            DateTimeOffset.UtcNow));
    }

    private static async Task<bool> WaitForLogContainsAsync(string path, string expected, int retries = 40, int delayMs = 25)
    {
        for (var i = 0; i < retries; i++)
        {
            Dispatcher.UIThread.RunJobs(null);
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                if (content.Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    [Fact]
    public async Task ImportToList_WhenStageNameIsLevelId_ShouldNormalizeItemNameToStageCode()
    {
        // WPF 对齐：AddCopilotTaskToList 条目名取 DataHelper.FindMap(stage_name).Code，core 以 nav_name 拼 tasks.json 导航键
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "main_01-07": { "code": "1-7", "levelId": "main/level_main_01-07", "name": "暴君", "stageId": "main_01-07" }
                }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        await fixture.ViewModel.ImportFromClipboardAsync("""{"stage_name":"main_01-07"}""");
        var item = Assert.Single(fixture.ViewModel.Items);
        Assert.Equal("1-7", item.Name);

        fixture.ViewModel.UseCopilotList = true;
        await fixture.ViewModel.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var listItem = JsonNode.Parse(appended.ParamsJson)?["copilot_list"]?[0] as JsonObject;
        Assert.NotNull(listItem);
        // 自动入列未覆盖导航名：不下发 nav_name_override，由 Core 从作业内容推导
        Assert.False(listItem!.ContainsKey("nav_name_override"));
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_WhenStageNameIsLevelId_ShouldSetNavigationNameToStageCode()
    {
        // WPF 对齐：ParseCopilotAsync 置 CopilotTaskName = navigateName（关卡 code）
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            overviewJson: """
                {
                  "main_01-07": { "code": "1-7", "levelId": "main/level_main_01-07", "name": "暴君", "stageId": "main_01-07" }
                }
                """);

        await fixture.ViewModel.LoadCurrentFromClipboardAsync("""{"stage_name":"main_01-07"}""");

        Assert.Equal("1-7", fixture.ViewModel.CopilotTaskName);
    }

    [Fact]
    public void CanonicalizeCopilotPayload_ShouldZeroSkillByRarity_WpfParity()
    {
        // WPF ParseCopilotAsync 对齐：skill3&rarity<6 / skill2&rarity<4 / skill1&rarity<3 置 0 并告警
        var battleData = """
            {
              "chars": {
                "char_002_amiya": { "name": "阿米娅", "profession": "CASTER", "rarity": 5 },
                "char_010_chen": { "name": "陈", "profession": "WARRIOR", "rarity": 6 },
                "char_501_durin": { "name": "杜林", "profession": "CASTER", "rarity": 2 }
              }
            }
            """;
        var payload = """{"stage_name":"1-7","opers":[{"name":"阿米娅","skill":3},{"name":"陈","skill":3},{"name":"杜林","skill":1}],"groups":[{"name":"主力","opers":[{"name":"阿米娅","skill":2}]}]}""";

        var corrected = CopilotPageViewModel.CanonicalizeCopilotPayload(payload, battleData, out var warnings);

        var root = JsonNode.Parse(corrected)!.AsObject();
        // WPF dev-v2 d9457ebd91：技能 3 对阿米娅（char_002_amiya）放行；5★ 阿米娅技能 2 也满足 4★ 门槛
        Assert.Equal(3, root["opers"]![0]!["skill"]!.GetValue<int>());
        Assert.Equal(3, root["opers"]![1]!["skill"]!.GetValue<int>());
        Assert.Equal(0, root["opers"]![2]!["skill"]!.GetValue<int>());
        Assert.Equal(2, root["groups"]![0]!["opers"]![0]!["skill"]!.GetValue<int>());
        var warning = Assert.Single(
            warnings,
            warning => warning.Kind == CopilotPageViewModel.CopilotCanonicalizationWarningKind.UnsupportedSkill);
        Assert.Equal("杜林", warning.Name);
    }

    [Fact]
    public void CanonicalizeCopilotPayload_WhenSssPayload_ShouldSkip_WpfParity()
    {
        // WPF 对齐：ParseSSSCopilot 不做技能稀有度校验
        var battleData = """
            { "chars": { "char_002_amiya": { "name": "阿米娅", "profession": "CASTER", "rarity": 5 } } }
            """;
        var payload = """{"type":"SSS","stage_name":"sss-1","opers":[{"name":"阿米娅","skill":3}]}""";

        var corrected = CopilotPageViewModel.CanonicalizeCopilotPayload(payload, battleData, out var warnings);

        Assert.Equal(payload, corrected);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CanonicalizeCopilotPayload_WhenOperatorUnknown_ShouldTreatRarityAsMinusOne_WpfParity()
    {
        // WPF 对齐：DataHelper.GetCharacterByNameOrAlias(...)?.Rarity ?? -1，未知干员按 -1 处理
        var battleData = """
            { "chars": { "char_002_amiya": { "name": "阿米娅", "profession": "CASTER", "rarity": 5 } } }
            """;
        var payload = """{"stage_name":"1-7","opers":[{"name":"不存在干员","skill":3}]}""";

        var corrected = CopilotPageViewModel.CanonicalizeCopilotPayload(payload, battleData, out var warnings);

        var root = JsonNode.Parse(corrected)!.AsObject();
        Assert.Equal(0, root["opers"]![0]!["skill"]!.GetValue<int>());
        Assert.Single(warnings);
    }

    [Fact]
    public void CanonicalizeCopilotPayload_WhenSkillFieldMissing_ShouldTreatAsDefaultZero_WpfParity()
    {
        // WPF dev-v2 b2d8591974：Oper.Skill 默认 0（不指定技能），未写 skill 字段不再触发
        // skill1&rarity<3 清零告警，也不应凭空写入 skill 字段
        var battleData = """
            {
              "chars": {
                "char_501_durin": { "name": "杜林", "profession": "CASTER", "rarity": 2 },
                "char_002_amiya": { "name": "阿米娅", "profession": "CASTER", "rarity": 5 }
              }
            }
            """;
        var payload = """{"stage_name":"1-7","opers":[{"name":"杜林"},{"name":"阿米娅"}]}""";

        var corrected = CopilotPageViewModel.CanonicalizeCopilotPayload(payload, battleData, out var warnings);

        Assert.Equal(payload, corrected);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CanonicalizeCopilotPayload_ShouldMatchOperatorNameCaseInsensitive_WpfParity()
    {
        // WPF DataHelper 干员名索引为 OrdinalIgnoreCase：AMIYA 命中 char_002_amiya，
        // 技能 3 按阿米娅例外放行（未命中则稀有度按 -1 清零）
        var battleData = """
            { "chars": { "char_002_amiya": { "name": "阿米娅", "name_en": "Amiya", "profession": "CASTER", "rarity": 5 } } }
            """;
        var payload = """{"stage_name":"1-7","opers":[{"name":"AMIYA","skill":3}]}""";

        var corrected = CopilotPageViewModel.CanonicalizeCopilotPayload(payload, battleData, out var warnings);

        var root = JsonNode.Parse(corrected)!.AsObject();
        Assert.Equal(3, root["opers"]![0]!["skill"]!.GetValue<int>());
        Assert.DoesNotContain(
            warnings,
            warning => warning.Kind == CopilotPageViewModel.CopilotCanonicalizationWarningKind.UnsupportedSkill);
        // 技能 3 同时推导精英 2
        Assert.Contains(
            warnings,
            warning => warning.Kind == CopilotPageViewModel.CopilotCanonicalizationWarningKind.EliteEmpty
                && warning.RequiredElite == 2);
    }

    [Fact]
    public void CanonicalizeCopilotPayload_ShouldDeriveEliteFromSkillLevelAndModule_WpfParity()
    {
        // WPF dev-v2 92bf7c7962：按 skill-1 / skill_level / module 推导 requirements.elite，
        // 缺失补全（EliteEmpty）、过低抬高（EliteAdjust）
        var battleData = """
            {
              "chars": {
                "char_010_chen": { "name": "陈", "profession": "WARRIOR", "rarity": 6 },
                "char_003_kalts": { "name": "凯尔希", "profession": "MEDIC", "rarity": 6 }
              }
            }
            """;
        var payload = """
            {
              "stage_name": "1-7",
              "opers": [
                { "name": "陈", "skill": 3 },
                { "name": "凯尔希", "skill": 0, "requirements": { "elite": 0, "skill_level": 10 } }
              ],
              "groups": [
                { "name": "模块", "opers": [ { "name": "陈", "skill": 0, "requirements": { "module": 5 } } ] }
              ]
            }
            """;

        var corrected = CopilotPageViewModel.CanonicalizeCopilotPayload(payload, battleData, out var warnings);

        var root = JsonNode.Parse(corrected)!.AsObject();
        Assert.Equal(2, root["opers"]![0]!["requirements"]!["elite"]!.GetValue<int>());
        Assert.Equal(2, root["opers"]![1]!["requirements"]!["elite"]!.GetValue<int>());
        Assert.Equal(2, root["groups"]![0]!["opers"]![0]!["requirements"]!["elite"]!.GetValue<int>());
        Assert.Contains(
            warnings,
            warning => warning.Kind == CopilotPageViewModel.CopilotCanonicalizationWarningKind.EliteEmpty
                && warning.Name == "陈"
                && warning.RequiredElite == 2);
        Assert.Contains(
            warnings,
            warning => warning.Kind == CopilotPageViewModel.CopilotCanonicalizationWarningKind.EliteAdjust
                && warning.Name == "凯尔希"
                && warning.PreviousElite == 0
                && warning.RequiredElite == 2);
    }

    [Fact]
    public void CanonicalizeCopilotPayload_ShouldPreferLocationAndRectAndPreserveUnknownFields_WpfParity()
    {
        // WPF dev-v2 5994a8db49/62f60b9b98/1ca41b0847：动作同填干员与坐标时清 name/role 保坐标，
        // Click 同填 rect 与 location 时清 location；未知字段原样透传
        var battleData = """
            { "chars": { "char_010_chen": { "name": "陈", "profession": "WARRIOR", "rarity": 6 } } }
            """;
        var payload = """
            {
              "stage_name": "1-7",
              "unknown_root": { "keep": true },
              "opers": [ { "name": "陈", "skill": 3, "unknown_oper": 7 } ],
              "actions": [
                { "type": "Skill", "name": "陈", "role": "WARRIOR", "location": [ 1, 2 ], "unknown_action": "keep" },
                { "type": "Click", "rect": [ 10, 20, 30, 40 ], "location": [ 5, 6 ] }
              ]
            }
            """;

        var corrected = CopilotPageViewModel.CanonicalizeCopilotPayload(payload, battleData, out var warnings);

        var root = JsonNode.Parse(corrected)!.AsObject();
        Assert.NotNull(root["unknown_root"]);
        Assert.Equal(7, root["opers"]![0]!["unknown_oper"]!.GetValue<int>());
        var skillAction = Assert.IsType<JsonObject>(root["actions"]![0]);
        Assert.False(skillAction.ContainsKey("name"));
        Assert.False(skillAction.ContainsKey("role"));
        Assert.Equal([1, 2], skillAction["location"]!.AsArray().Select(node => node!.GetValue<int>()).ToArray());
        Assert.Equal("keep", skillAction["unknown_action"]!.GetValue<string>());
        var clickAction = Assert.IsType<JsonObject>(root["actions"]![1]);
        Assert.False(clickAction.ContainsKey("location"));
        Assert.Equal([10, 20, 30, 40], clickAction["rect"]!.AsArray().Select(node => node!.GetValue<int>()).ToArray());
        Assert.Contains(
            warnings,
            warning => warning.Kind == CopilotPageViewModel.CopilotCanonicalizationWarningKind.ActionWithBothLocationAndOperator);
        Assert.Contains(
            warnings,
            warning => warning.Kind == CopilotPageViewModel.CopilotCanonicalizationWarningKind.ActionWithBothRectAndLocation);
    }

    [Fact]
    public async Task StartAsync_WhenOperSkillUnsupported_ShouldExecuteCorrectedFileWithWarning()
    {
        // WPF 对齐：修正后的作业写缓存/临时文件后执行，并打 UnsupportedSkill 警告
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        await fixture.CreateResourceDirectoryAsync(
            battleDataJson: """
                { "chars": { "char_501_durin": { "name": "杜林", "profession": "CASTER", "rarity": 2 } } }
                """);
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        await fixture.ViewModel.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","opers":[{"name":"杜林","skill":1}]}""");
        await fixture.ViewModel.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var fileName = JsonNode.Parse(appended.ParamsJson)!["filename"]!.GetValue<string>();
        var executed = JsonNode.Parse(await File.ReadAllTextAsync(fileName))!.AsObject();
        Assert.Equal(0, executed["opers"]![0]!["skill"]!.GetValue<int>());
        Assert.Contains(fixture.ViewModel.Logs, log => log.Content.Contains("不支持技能", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_ShouldResolveVideoLinkMapUrlAndOutputJobInfo_WpfParity()
    {
        // WPF 对齐：documentation.details 中 av/bv 链接解析进 CopilotUrl，MapUrl 指向 map/{stageName}，作业信息写日志面板
        await using var fixture = await CopilotN3Fixture.CreateAsync();

        await fixture.ViewModel.LoadCurrentFromClipboardAsync(
            """{"stage_name":"1-7","documentation":{"title":"测试作业","details":"参考 BV1xx411c7mD 打法"},"opers":[{"name":"阿米娅","skill":2}]}""");

        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mD", fixture.ViewModel.CopilotUrl);
        Assert.Equal("https://map.ark-nights.com/map/1-7?coord_override=maa", fixture.ViewModel.MapUrl);
        Assert.Contains(fixture.ViewModel.Logs, log => log.Content.Contains("测试作业", StringComparison.Ordinal));
        Assert.Contains(fixture.ViewModel.Logs, log =>
            log.Content.Contains("阿米娅", StringComparison.Ordinal) && log.Content.Contains("技能", StringComparison.Ordinal));
        Assert.Contains(fixture.ViewModel.Logs, log => log.Content.Contains("共 1 名干员", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_WhenNoVideoLink_ShouldKeepPrtsPlusUrl_WpfParity()
    {
        await using var fixture = await CopilotN3Fixture.CreateAsync();

        await fixture.ViewModel.LoadCurrentFromClipboardAsync(
            """{"stage_name":"1-7","documentation":{"title":"测试作业","details":"无链接"},"opers":[{"name":"阿米娅","skill":2}]}""");

        Assert.Equal("https://prts.plus", fixture.ViewModel.CopilotUrl);
    }

    [Fact]
    public async Task LoadSssCopilot_ShouldSwitchToSecurityTabAndAllowStart_WpfParity()
    {
        // WPF 对齐：ParseSSSCopilot 置 CopilotTabIndex = 1，加载 SSS 作业后可直接启动
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        fixture.ViewModel.CopilotTabIndex = 0;

        await fixture.ViewModel.LoadCurrentFromClipboardAsync("""{"type":"SSS","stage_name":"sss-test","copilot_id":66}""");

        Assert.Equal(1, fixture.ViewModel.CopilotTabIndex);
        await fixture.ViewModel.StartAsync();
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("SSSCopilot", appended.Type);
    }

    [Fact]
    public async Task StartAsync_WhenLoopCheckedOnSssTabThenSwitchToMainTab_ShouldStillSendLoopTimes_WpfParity()
    {
        // WPF 对齐（冻结 CopilotViewModel.cs:2048 LoopTimes = Loop ? LoopTimes : 1）：不按页签门控；
        // 循环控件仅在保全(1)/其他活动(3)页签可见，但 Loop 值跨页签保留，切回主线页签启动单作业仍发送 LoopTimes
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        fixture.ViewModel.CopilotTabIndex = 1;
        fixture.ViewModel.Loop = true;
        fixture.ViewModel.LoopTimes = 5;
        fixture.ViewModel.CopilotTabIndex = 0;

        await fixture.ViewModel.LoadCurrentFromClipboardAsync(
            """{"stage_name":"1-7","opers":[{"name":"阿米娅","skill":2}]}""");
        await fixture.ViewModel.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var payload = JsonNode.Parse(appended.ParamsJson)!.AsObject();
        Assert.Equal(5, payload["loop_times"]!.GetValue<int>());
    }

    [Fact]
    public async Task TaskChainError_ShouldUnlockCopilotErrorAchievement_WpfParity()
    {
        // WPF 对齐：AsstProxy.TaskChainError 对 Copilot 解锁 CopilotError 成就
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        fixture.ViewModel.FilePath = fixture.CreateCopilotFile();
        await fixture.ViewModel.ImportFromFileAsync();
        await fixture.ViewModel.StartAsync();

        fixture.ViewModel.ApplyRuntimeCallback(new CoreCallbackEvent(
            10003,
            "TaskChainError",
            $$"""{"task_chain":"Copilot","task_id":{{fixture.Bridge.LastAppendedTaskId}}}""",
            DateTimeOffset.UtcNow));

        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        var item = Assert.Single(snapshot.Value!.Items, i => i.Id == "CopilotError");
        Assert.True(item.IsUnlocked);
    }

    [Fact]
    public async Task StartAsync_ListItemSourceMissingWithInlinePayload_ShouldFallbackToSnapshotWithoutSourcePathRewrite_WpfParity()
    {
        // 执行解析的 SourcePath 缺失但条目持有入列 InlinePayload 快照时回退快照执行
        // （不再报「作业文件不存在」），且一次性执行缓存路径不得写回 item.SourcePath
        // （WPF 列表项恒指向入列快照来源，执行不改变引用）
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);

        var vm = fixture.ViewModel;
        vm.UseCopilotList = true;
        var ghostPath = Path.Combine(fixture.Root, "ghost.json");
        const string inlinePayload = """{"stage_name":"1-7","minimum_required":"v4.0","actions":[{}]}""";
        vm.Items.Add(new CopilotItemViewModel("1-7", "主线/故事集/SideStory", ghostPath, inlinePayload)
        {
            IsChecked = true,
            TabIndex = 0,
        });

        await vm.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var payload = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        var first = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["copilot_list"])[0]);
        var executedPath = first["filename"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(executedPath));
        Assert.Contains("copilot-cache", executedPath, StringComparison.Ordinal);
        Assert.True(File.Exists(executedPath));
        Assert.Equal(ghostPath, vm.Items[0].SourcePath);
        Assert.Equal(inlinePayload, vm.Items[0].InlinePayload);
        Assert.DoesNotContain("作业文件不存在", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_SkillCorrectionCache_ShouldNotRewriteListItemSourcePath_WpfParity()
    {
        // 技能修正产物为一次性执行缓存；执行后列表项仍指向原文件（编辑原文件即更新列表作业语义，
        // 见第 13 轮 N-5 记录），缓存路径不得写回 SourcePath 并随持久化扩散
        await using var fixture = await CopilotN3Fixture.CreateAsync();
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(
            fixture.Runtime.ConnectFeatureService,
            fixture.ReadyAdbPath)).Success);
        await fixture.CreateResourceDirectoryAsync(battleDataJson: """
            {
              "chars": {
                "char_501_durin": { "name": "杜林", "profession": "CASTER", "rarity": 2 }
              }
            }
            """);

        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var filePath = Path.Combine(fixture.Root, "skillfix.json");
        await File.WriteAllTextAsync(
            filePath,
            """{"stage_name":"1-7","minimum_required":"v4.0","actions":[{}],"opers":[{"name":"杜林","skill":3}]}""");
        await vm.LoadCurrentFromFileAsync(filePath);
        await vm.AddCurrentToListAsync(isRaid: false);

        var item = Assert.Single(vm.Items);
        Assert.Equal(filePath, item.SourcePath);
        // 文件加载经 AddCurrentToListAsync 入列的条目仅持 SourcePath（InlinePayload 为空），
        // 批量导入条目才同持两者；本用例聚焦技能修正缓存不写回 SourcePath
        Assert.True(string.IsNullOrWhiteSpace(item.InlinePayload));

        await vm.StartAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var payload = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        var first = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["copilot_list"])[0]);
        var executedPath = first["filename"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(executedPath));
        Assert.NotEqual(filePath, executedPath);
        Assert.Contains("copilot-cache", executedPath, StringComparison.Ordinal);
        var corrected = JsonNode.Parse(await File.ReadAllTextAsync(executedPath!))!.AsObject();
        Assert.Equal(0, corrected["opers"]![0]!["skill"]!.GetValue<int>());
        Assert.Equal(filePath, item.SourcePath);
    }

    private sealed class CountingPostActionFeatureService : IPostActionFeatureService
    {
        public int EndScriptExecuteCount { get; private set; }

        public Task<UiOperationResult<PostActionConfig>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionConfig>.Ok(new PostActionConfig(), "Loaded."));

        public Task<UiOperationResult> SaveAsync(PostActionConfig config, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("Saved."));

        public Task<UiOperationResult<PostActionPreview>> GetCapabilityPreviewAsync(
            PostActionConfig config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionPreview>.Ok(
                new PostActionPreview(false, [], []),
                "Previewed."));

        public Task<UiOperationResult<PostActionPreview>> ValidateSelectionAsync(
            PostActionConfig config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult<PostActionPreview>.Ok(
                new PostActionPreview(false, [], []),
                "Validated."));

        public Task<UiOperationResult> ExecuteAfterCompletionAsync(
            PostActionExecutionContext context,
            PostActionConfig? configOverride = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("Post action executed."));

        public Task<UiOperationResult> ExecuteEndScriptAsync(
            bool manualStop,
            CancellationToken cancellationToken = default)
        {
            EndScriptExecuteCount++;
            return Task.FromResult(UiOperationResult.Ok("End script executed."));
        }
    }

    private sealed class CopilotN3Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _pumpCts = new();
        private readonly Task _pumpTask;

        private CopilotN3Fixture(
            string root,
            MAAUnifiedRuntime runtime,
            Bridge bridge,
            NotificationTrackingPlatformCapabilityService notificationTracker,
            SuccessfulFeedbackHandler feedbackHandler,
            string readyAdbPath)
        {
            Root = root;
            Runtime = runtime;
            Bridge = bridge;
            NotificationTracker = notificationTracker;
            FeedbackHandler = feedbackHandler;
            ReadyAdbPath = readyAdbPath;
            ViewModel = new CopilotPageViewModel(runtime);
            _pumpTask = runtime.SessionService.StartCallbackPumpAsync(_ => Task.CompletedTask, _pumpCts.Token);
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public Bridge Bridge { get; }

        public NotificationTrackingPlatformCapabilityService NotificationTracker { get; }

        public SuccessfulFeedbackHandler FeedbackHandler { get; }

        public string ReadyAdbPath { get; }

        public CopilotPageViewModel ViewModel { get; }

        public async Task<string> CreateResourceDirectoryAsync(string? overviewJson = null, string? battleDataJson = null)
        {
            var resourceDir = Path.Combine(Root, "resource");
            if (overviewJson is not null)
            {
                var overviewDir = Path.Combine(resourceDir, "Arknights-Tile-Pos");
                Directory.CreateDirectory(overviewDir);
                await File.WriteAllTextAsync(Path.Combine(overviewDir, "overview.json"), overviewJson);
            }

            if (battleDataJson is not null)
            {
                Directory.CreateDirectory(resourceDir);
                await File.WriteAllTextAsync(Path.Combine(resourceDir, "battle_data.json"), battleDataJson);
            }

            ViewModel.ResourceDirectoryOverride = resourceDir;
            return resourceDir;
        }

        public string CreateCopilotFile()
        {
            var path = Path.Combine(Root, "copilot.json");
            var payload = new JsonObject
            {
                ["copilot_id"] = 123,
                ["stage_name"] = "1-7",
                ["minimum_required"] = "v4.0",
                ["actions"] = new JsonArray(new JsonObject()),
            };
            File.WriteAllText(path, payload.ToJsonString());
            return path;
        }

        public static async Task<CopilotN3Fixture> CreateAsync(
            IPostActionFeatureService? postActionFeatureService = null)
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
            var readyAdbPath = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "copilot-n3-ready");

            var bridge = new Bridge();
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
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var notificationTracker = new NotificationTrackingPlatformCapabilityService(capability);
            var connect = new ConnectFeatureService(session, config, log, bridge, root);
            var achievementTracker = new AchievementTrackerService(config, root);
            var feedbackHandler = new SuccessfulFeedbackHandler();
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
                TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
                CopilotFeatureService = new CopilotFeatureService(new HttpClient(feedbackHandler)),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = notificationTracker,
                OverlayFeatureService = new OverlayFeatureService(notificationTracker),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, notificationTracker, diagnostics),
                AchievementTrackerService = achievementTracker,
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = postActionFeatureService ?? new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new CopilotN3Fixture(root, runtime, bridge, notificationTracker, feedbackHandler, readyAdbPath);
        }

        public async ValueTask DisposeAsync()
        {
            _pumpCts.Cancel();
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
                // expected on disposal
            }

            _pumpCts.Dispose();
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }

    private sealed class SuccessfulFeedbackHandler : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private sealed class Bridge : IMaaCoreBridge, IMaaCoreBridgeQueueRecovery
    {
        private readonly Channel<CoreCallbackEvent> _callbacks = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _nextTaskId = 1;

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public int LastAppendedTaskId { get; private set; }

        public List<CoreTaskRequest> AppendedTasks { get; } = [];

        public List<CoreTaskRequest> ActiveQueuedTasks { get; } = [];

        public Queue<CoreResult<bool>> StartResults { get; } = new();

        public CoreInstanceOptions? LastInstanceOptions { get; private set; }

        public int RecoverTaintedTaskQueueCallCount { get; private set; }

        public void Publish(CoreCallbackEvent callback)
        {
            _callbacks.Writer.TryWrite(callback);
        }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(
                new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(
            CoreConnectionInfo connectionInfo,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(
            CoreTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            AppendedTasks.Add(task);
            ActiveQueuedTasks.Add(task);
            LastAppendedTaskId = _nextTaskId++;
            return Task.FromResult(CoreResult<int>.Ok(LastAppendedTaskId));
        }

        public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(
            CoreInstanceOptions options,
            CancellationToken cancellationToken = default)
        {
            LastInstanceOptions = options;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            return Task.FromResult(StartResults.Count > 0
                ? StartResults.Dequeue()
                : CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> RecoverTaintedTaskQueueAsync(CancellationToken cancellationToken = default)
        {
            RecoverTaintedTaskQueueCallCount++;
            ActiveQueuedTasks.Clear();
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(
            CoreAttachWindowRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbacks.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _callbacks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
