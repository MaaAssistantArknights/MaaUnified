using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class SessionActivityKindRegressionTests
{
    private static readonly string[] Languages = ["zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas"];

    private static readonly string[] TaskQueueConflictActions = ["Start", "RunOnce", "AccountSwitch"];

    private static readonly string[] ConflictKeys =
    [
        "TaskQueue.Error.SessionActivity.Timer.Start",
        "TaskQueue.Error.SessionActivity.Other.Start",
        "TaskQueue.Error.SessionActivity.Timer.RunOnce",
        "TaskQueue.Error.SessionActivity.Other.RunOnce",
        "TaskQueue.Error.SessionActivity.Timer.AccountSwitch",
        "TaskQueue.Error.SessionActivity.Other.AccountSwitch",
        "Copilot.Error.SessionActivity.TimerActive",
        "Copilot.Error.SessionActivity.OtherActive",
        "Toolbox.Error.SessionActivity.TimerActive",
        "Toolbox.Error.SessionActivity.OtherActive",
    ];

    [Fact]
    public void SessionActivityKind_ShouldDistinguishTimerFromOtherOperations()
    {
        var shared = new ConnectionGameSharedStateViewModel();

        using (var timer = shared.TryBeginExclusiveSessionOperation("Timer.ScheduledStart"))
        {
            Assert.NotNull(timer);
            Assert.True(shared.IsSessionOperationActive);
            Assert.True(shared.IsSessionOperationExclusiveActive);
            Assert.True(shared.IsTimerSessionOperationActive);
        }

        Assert.False(shared.IsSessionOperationActive);
        Assert.False(shared.IsTimerSessionOperationActive);

        using (var other = shared.TryBeginExclusiveSessionOperation("Other.Exclusive"))
        {
            Assert.NotNull(other);
            Assert.True(shared.IsSessionOperationExclusiveActive);
            Assert.False(shared.IsTimerSessionOperationActive);
        }

        using var normal = shared.BeginSessionOperation("Toolbox.Peep");
        Assert.True(shared.IsSessionOperationActive);
        Assert.False(shared.IsTimerSessionOperationActive);
    }

    [Theory]
    [InlineData(ToolboxToolKind.Recruit)]
    [InlineData(ToolboxToolKind.OperBox)]
    [InlineData(ToolboxToolKind.Depot)]
    [InlineData(ToolboxToolKind.Gacha)]
    [InlineData(ToolboxToolKind.MiniGame)]
    public async Task ToolboxNonPeepStarts_ShouldFailClosedUnderExclusiveTimerLease(ToolboxToolKind tool)
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        vm.GachaShowDisclaimer = false;
        using var exclusive = fixture.ConnectionState.TryBeginExclusiveSessionOperation("Timer.ScheduledStart");
        Assert.NotNull(exclusive);

        Assert.False(vm.CanStartRecruitRecognition);
        Assert.False(vm.CanStartOperBoxRecognition);
        Assert.False(vm.CanStartDepotRecognition);
        Assert.False(vm.CanStartGacha);
        Assert.False(vm.CanStartMiniGame);

        await StartToolAsync(vm, tool);

        Assert.Equal(0, fixture.Bridge.ConnectCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Contains(
            vm.Texts.GetOrDefault(
                "Toolbox.Error.SessionActivity.TimerActive",
                "A scheduled task is active. Toolbox cannot start until it finishes."),
            vm.LastErrorMessage,
            StringComparison.Ordinal);

        exclusive.Dispose();
        Assert.True(vm.CanStartRecruitRecognition);
        Assert.True(vm.CanStartOperBoxRecognition);
        Assert.True(vm.CanStartDepotRecognition);
        Assert.True(vm.CanStartGacha);
        Assert.True(vm.CanStartMiniGame);
    }

    [Theory]
    [InlineData("Toolbox.Peep")]
    [InlineData("Copilot.Start")]
    [InlineData("Settings.Connect.ScreenshotTest")]
    public async Task TaskQueueCentralAction_ShouldProjectStopAndRouteActiveLease(
        string operationName)
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var shared = new ConnectionGameSharedStateViewModel();
        IDisposable? activity = null;
        var stopCallCount = 0;
        var page = new TaskQueuePageViewModel(
            fixture.Runtime,
            shared,
            stopActiveSessionOperationAsync: async _ =>
            {
                stopCallCount++;
                activity?.Dispose();
                await Task.CompletedTask;
            });

        activity = shared.TryBeginSessionOperation(operationName);
        Assert.NotNull(activity);
        try
        {
            Assert.True(page.IsExternalSessionActivityActive);
            Assert.True(page.CanToggleRun);
            Assert.Equal(page.RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop"), page.RunButtonText);

            await page.ToggleRunAsync();

            Assert.Equal(1, stopCallCount);
            Assert.False(shared.IsSessionOperationActive);
            Assert.False(page.IsExternalSessionActivityActive);
            Assert.True(page.CanToggleRun);
            Assert.Equal(page.RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!"), page.RunButtonText);
            Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
            Assert.Empty(fixture.Bridge.AppendedTasks);
            Assert.Equal(0, fixture.Bridge.StartCallCount);
        }
        finally
        {
            activity?.Dispose();
        }
    }

    [Fact]
    public async Task ToolboxRecruit_ShouldReportOtherSessionActivityWithoutAppending()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        using var activity = fixture.ConnectionState.BeginSessionOperation("Settings.Connect.ScreenshotTest");

        Assert.False(vm.CanStartRecruitRecognition);
        await vm.StartRecruitAsync();

        Assert.Equal(0, fixture.Bridge.ConnectCallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Contains(
            vm.Texts.GetOrDefault(
                "Toolbox.Error.SessionActivity.OtherActive",
                "Another session operation is active. Toolbox cannot start until it is stopped or finishes."),
            vm.LastErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolboxRunningTool_ShouldKeepItsOwnStopEnabledAndReleaseActivity()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();

        Assert.True(vm.IsRecruitExecuting);
        Assert.True(fixture.ConnectionState.IsSessionOperationActive);
        Assert.True(vm.CanStartRecruitRecognition);
        Assert.False(vm.CanStartOperBoxRecognition);
        Assert.False(vm.CanStartDepotRecognition);
        Assert.False(vm.CanStartGacha);
        Assert.False(vm.CanStartMiniGame);

        await vm.StopActiveToolAsync();

        Assert.False(vm.IsRecruitExecuting);
        Assert.False(fixture.ConnectionState.IsSessionOperationActive);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanStartRecruitRecognition);
        Assert.True(vm.CanStartOperBoxRecognition);
        Assert.True(vm.CanStartDepotRecognition);
        Assert.True(vm.CanStartGacha);
        Assert.True(vm.CanStartMiniGame);
    }

    [Fact]
    public void TaskQueueCentralStop_ShouldBeWiredToShellLifecycleAndRunOwnerRoutes()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var taskQueue = File.ReadAllText(Path.Combine(
            root,
            "App",
            "ViewModels",
            "TaskQueue",
            "TaskQueuePageViewModel.cs"));
        var shell = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "MainShellViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));

        Assert.Contains("stopActiveSessionOperationAsync", taskQueue, StringComparison.Ordinal);
        Assert.Contains("StopActiveSessionOperationFromTaskQueueAsync", shell, StringComparison.Ordinal);
        Assert.Contains("TryCancelActiveLifecycleOperation(", shell, StringComparison.Ordinal);
        Assert.Contains("StopRunOwnerFromDialogAsync(currentOwner", shell, StringComparison.Ordinal);
        Assert.Contains("TaskQueuePage.StopAsync(cancellationToken, userInitiated: false)", shell, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanToggleRun}\"", view, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding RunButtonText}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueueConflictMessages_ShouldBeTimerAwareAcrossSixLanguages()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var connected = await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            fixture.ConnectionState.ConnectAddress,
            fixture.ConnectionState.ConnectConfig,
            fixture.ReadyAdbPath);
        Assert.True(connected.Success, connected.Message);

        var shared = new ConnectionGameSharedStateViewModel();
        var page = new TaskQueuePageViewModel(fixture.Runtime, shared);
        await page.InitializeAsync();
        await WaitUntilAsync(() => page.CurrentSessionState == SessionState.Connected);
        Assert.NotNull(page.SelectedTask);
        page.StartUpModule.AccountName = "verifier-account";

        using (var timer = shared.TryBeginExclusiveSessionOperation("Timer.ScheduledStart"))
        {
            Assert.NotNull(timer);
            foreach (var language in Languages)
            {
                page.SetLanguage(language);
                await AssertTaskQueueLanguageAsync(page, fixture.Runtime, language, "Timer");
            }
        }

        using var other = shared.BeginSessionOperation("Settings.Connect.ScreenshotTest");
        foreach (var language in Languages)
        {
            page.SetLanguage(language);
            await AssertTaskQueueLanguageAsync(page, fixture.Runtime, language, "Other");
        }

        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
    }

    [Fact]
    public async Task CopilotConflictMessages_ShouldBeTimerAwareAcrossSixLanguages()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var connected = await fixture.Runtime.ConnectFeatureService.ConnectAsync(
            fixture.ConnectionState.ConnectAddress,
            fixture.ConnectionState.ConnectConfig,
            fixture.ReadyAdbPath);
        Assert.True(connected.Success, connected.Message);

        var shared = new ConnectionGameSharedStateViewModel();
        var page = new CopilotPageViewModel(fixture.Runtime, connectionState: shared);

        using (var timer = shared.TryBeginExclusiveSessionOperation("Timer.ScheduledStart"))
        {
            Assert.NotNull(timer);
            foreach (var language in Languages)
            {
                page.SetLanguage(language);
                Assert.False(page.CanStart);
                await page.StartAsync();
                Assert.Equal(
                    page.Texts.GetOrDefault(
                        "Copilot.Error.SessionActivity.TimerActive",
                        "A scheduled task is active. Copilot cannot start until it finishes."),
                    page.LastErrorMessage);
            }
        }

        using var other = shared.BeginSessionOperation("Settings.Connect.ScreenshotTest");
        foreach (var language in Languages)
        {
            page.SetLanguage(language);
            Assert.False(page.CanStart);
            await page.StartAsync();
            Assert.Equal(
                page.Texts.GetOrDefault(
                    "Copilot.Error.SessionActivity.OtherActive",
                    "Another session operation is active. Copilot cannot start until it is stopped or finishes."),
                page.LastErrorMessage);
        }

        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public void ConflictMessages_ShouldExistInAllNormalLocalesAndPseudoLocalizeForPallas()
    {
        var manager = UiLocalizationResourceManager.Shared;
        foreach (var key in ConflictKeys)
        {
            foreach (var language in Languages)
            {
                var result = manager.Lookup(language, key);
                Assert.False(result.IsFallback, $"{language}:{key} fell back to {result.FallbackSource}.");
                Assert.NotEqual(key, result.Value);
                if (string.Equals(language, "pallas", StringComparison.Ordinal))
                {
                    Assert.StartsWith("[!! ", result.Value, StringComparison.Ordinal);
                    Assert.NotEqual(manager.Lookup("en-us", key).Value, result.Value);
                }
            }
        }

        foreach (var language in Languages)
        {
            foreach (var action in TaskQueueConflictActions)
            {
                Assert.NotEqual(
                    manager.Lookup(language, $"TaskQueue.Error.SessionActivity.Timer.{action}").Value,
                    manager.Lookup(language, $"TaskQueue.Error.SessionActivity.Other.{action}").Value);
            }

            Assert.NotEqual(
                manager.Lookup(language, "Copilot.Error.SessionActivity.TimerActive").Value,
                manager.Lookup(language, "Copilot.Error.SessionActivity.OtherActive").Value);
            Assert.NotEqual(
                manager.Lookup(language, "Toolbox.Error.SessionActivity.TimerActive").Value,
                manager.Lookup(language, "Toolbox.Error.SessionActivity.OtherActive").Value);
        }
    }

    private static async Task StartToolAsync(ToolboxPageViewModel vm, ToolboxToolKind tool)
    {
        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                await vm.StartRecruitAsync();
                break;
            case ToolboxToolKind.OperBox:
                await vm.StartOperBoxAsync();
                break;
            case ToolboxToolKind.Depot:
                await vm.StartDepotAsync();
                break;
            case ToolboxToolKind.Gacha:
                await vm.StartGachaAsync(once: true);
                break;
            case ToolboxToolKind.MiniGame:
                await vm.StartMiniGameAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tool), tool, null);
        }
    }

    private static async Task AssertTaskQueueLanguageAsync(
        TaskQueuePageViewModel page,
        MAAUnifiedRuntime runtime,
        string language,
        string activityKind)
    {
        page.SetLanguage(language);

        await page.StartAsync();
        Assert.Equal(ActivityText(page, activityKind, "Start"), page.LastErrorMessage);

        await page.RunSelectedTaskOnceAsync();
        Assert.Equal(ActivityText(page, activityKind, "RunOnce"), page.LastErrorMessage);

        await page.StartUpModule.RunAccountSwitchManualAsync();
        Assert.Equal(ActivityText(page, activityKind, "AccountSwitch"), page.LastErrorMessage);
        Assert.Null(runtime.SessionService.CurrentRunOwner);
    }

    private static string ActivityText(TaskQueuePageViewModel page, string activityKind, string action)
    {
        var fallback = (activityKind, action) switch
        {
            ("Timer", "RunOnce") => "A scheduled task is active. Wait for it to finish before running a single task.",
            ("Other", "RunOnce") => "Another session operation is active. Stop or wait for it before running a single task.",
            ("Timer", "AccountSwitch") => "A scheduled task is active. Wait for it to finish before switching accounts.",
            ("Other", "AccountSwitch") => "Another session operation is active. Stop or wait for it before switching accounts.",
            ("Timer", _) => "A scheduled task is active. Wait for it to finish before starting tasks.",
            _ => "Another session operation is active. Stop or wait for it before starting tasks.",
        };
        return page.Texts.GetOrDefault(
            $"TaskQueue.Error.SessionActivity.{activityKind}.{action}",
            fallback);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        int retry = 80,
        int delayMs = 25)
    {
        for (var index = 0; index < retry; index++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new TimeoutException("Condition was not reached in the expected time.");
    }
}
