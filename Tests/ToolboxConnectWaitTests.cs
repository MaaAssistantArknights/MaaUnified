using Avalonia.Threading;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Orchestration;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

/// <summary>
/// Reproduces the connection lifecycle race: starting a Toolbox tool while a connect is
/// already in flight must wait for that connect to settle instead of failing with
/// "Connection lifecycle already running" (WPF never surfaces that state as an error).
/// </summary>
public sealed class ToolboxConnectWaitTests
{
    [Fact]
    public async Task StartMiniGame_WhileSessionConnecting_ShouldWaitForInFlightConnect_AndNotIssueCompetingConnect()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        // Match the touch mode the service normalizes to (DefaultTouchMode), so the
        // in-flight connect's stored info compares equal to the Toolbox's current one.
        fixture.ConnectionState.TouchMode = "MaaFwAdb";
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        Dispatcher.UIThread.RunJobs(null);

        // An in-flight connect (e.g. startup auto-connect) blocks at the bridge.
        // Use the same connection info the Toolbox would build so the settled
        // connection matches the Toolbox's current settings.
        fixture.Bridge.BlockConnect = true;
        var inFlightConnect = fixture.Runtime.ConnectFeatureService.ValidateAndConnectAsync(
            fixture.ConnectionState.BuildCoreConnectionInfo(),
            CancellationToken.None);
        await fixture.Bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Connecting, fixture.Runtime.SessionService.CurrentState);

        // The user starts a mini-game while the connect is in flight.
        vm.MiniGameTaskName = "SS@Store@Begin";
        var dispatch = vm.StartMiniGameAsync();
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs(null);

        // The in-flight connect succeeds.
        fixture.Bridge.BlockConnect = false;
        fixture.Bridge.ConnectCompletion!.SetResult(CoreResult<bool>.Ok(true));
        Assert.True((await inFlightConnect).Success);

        // The dispatch must succeed via the settled connection — exactly one bridge
        // connect (no competing attempt) and the mini-game task appended.
        await dispatch.WaitAsync(TimeSpan.FromSeconds(15));
        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal(1, fixture.Bridge.ConnectCallCount);
        Assert.Contains(fixture.Bridge.AppendedTasks, task =>
            task.Name == "Toolbox.MiniGame" &&
            task.ParamsJson.Contains("SS@Store@Begin"));
    }

    [Fact]
    public async Task StartMiniGame_WhenInFlightConnectFails_ShouldFallBackToOwnConnectAttempt()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        fixture.ConnectionState.TouchMode = "MaaFwAdb";
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        Dispatcher.UIThread.RunJobs(null);

        // An in-flight connect that will fail.
        fixture.Bridge.BlockConnect = true;
        var inFlightConnect = fixture.Runtime.ConnectFeatureService.ValidateAndConnectAsync(
            fixture.ConnectionState.BuildCoreConnectionInfo(),
            CancellationToken.None);
        await fixture.Bridge.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Connecting, fixture.Runtime.SessionService.CurrentState);

        vm.MiniGameTaskName = "SS@Store@Begin";
        var dispatch = vm.StartMiniGameAsync();
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs(null);

        // The in-flight connect fails; subsequent connects go through normally.
        fixture.Bridge.BlockConnect = false;
        fixture.Bridge.ConnectCompletion!.SetResult(CoreResult<bool>.Fail(
            new CoreError(CoreErrorCode.ConnectFailed, "in-flight connect failed")));
        Assert.False((await inFlightConnect).Success);

        // The Toolbox must fall back to its own connect attempt and dispatch successfully.
        await dispatch.WaitAsync(TimeSpan.FromSeconds(15));
        Dispatcher.UIThread.RunJobs(null);

        Assert.Equal(2, fixture.Bridge.ConnectCallCount);
        Assert.Contains(fixture.Bridge.AppendedTasks, task => task.Name == "Toolbox.MiniGame");
    }
}
