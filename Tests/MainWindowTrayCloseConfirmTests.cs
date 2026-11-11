using Avalonia.Controls;
using MAAUnified.App.Views;

namespace MAAUnified.Tests;

public sealed class MainWindowTrayCloseConfirmTests
{
    // WPF parity (frozen TaskQueueViewModel.ConfirmExit + owner-less HandyControl
    // MessageBox): a cancelled tray Exit/Restart confirmation never changes the
    // main-window presentation. Avalonia's ShowDialog(owner) force-shows the
    // owner, so the cancel path must restore the captured pre-confirm state.
    [Theory]
    [InlineData(false, WindowState.Normal, MainWindow.CloseConfirmCancelRestore.HiddenToTray)]
    [InlineData(false, WindowState.Minimized, MainWindow.CloseConfirmCancelRestore.HiddenToTray)]
    [InlineData(true, WindowState.Minimized, MainWindow.CloseConfirmCancelRestore.Minimized)]
    [InlineData(true, WindowState.Normal, MainWindow.CloseConfirmCancelRestore.None)]
    [InlineData(true, WindowState.Maximized, MainWindow.CloseConfirmCancelRestore.None)]
    public void ResolveCloseConfirmCancelRestore_ShouldMatchWpfPresentationSemantics(
        bool wasVisible,
        WindowState previousWindowState,
        MainWindow.CloseConfirmCancelRestore expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ResolveCloseConfirmCancelRestore(wasVisible, previousWindowState));
    }

    [Fact]
    public void TrayCloseConfirm_ShouldReHideToTrayAfterCancelledConfirm()
    {
        var content = ReadMainWindowSource();

        var dispatchStart = content.IndexOf(
            "private async Task DispatchTrayCommandAsync(",
            StringComparison.Ordinal);
        Assert.True(dispatchStart >= 0, "DispatchTrayCommandAsync should exist.");
        var dispatchBody = content.Substring(dispatchStart);
        Assert.Contains(
            "await ConfirmTrayCloseAsync(confirmScope, source, cancellationToken)",
            dispatchBody,
            StringComparison.Ordinal);

        var confirmStart = content.IndexOf(
            "private async Task<bool> ConfirmTrayCloseAsync(",
            StringComparison.Ordinal);
        Assert.True(confirmStart >= 0, "ConfirmTrayCloseAsync should exist.");
        var confirmBody = content.Substring(confirmStart);

        // The cancel path must restore the pre-confirm presentation state, which
        // re-hides a hidden-to-tray window via HideMainWindowToTrayCoreAsync so
        // the HideTray tray-menu entry stays removed while the window is hidden.
        var cancelledIndex = confirmBody.IndexOf("cancelled", StringComparison.Ordinal);
        var restoreIndex = confirmBody.IndexOf(
            "RestorePresentationAfterCancelledCloseConfirmAsync(",
            StringComparison.Ordinal);
        Assert.True(cancelledIndex >= 0, "The cancel diagnostic should be recorded.");
        Assert.True(restoreIndex > cancelledIndex, "The cancel path must restore the pre-confirm presentation state.");

        var restoreStart = content.IndexOf(
            "private async Task RestorePresentationAfterCancelledCloseConfirmAsync(",
            StringComparison.Ordinal);
        Assert.True(restoreStart >= 0, "RestorePresentationAfterCancelledCloseConfirmAsync should exist.");
        var restoreBody = content.Substring(restoreStart);
        Assert.Contains(
            "CloseConfirmCancelRestore.HiddenToTray when IsVisible",
            restoreBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "await HideMainWindowToTrayCoreAsync(cancellationToken);",
            restoreBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "CloseConfirmCancelRestore.Minimized when IsVisible && WindowState != WindowState.Minimized",
            restoreBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CloseConfirmPaths_ShouldSharePendingReentrancyGuard()
    {
        var content = ReadMainWindowSource();

        // WPF parity (frozen TaskQueueViewModel.ConfirmExit Closing guard): both
        // the window-chrome close path and the tray Exit/Restart path must treat
        // a second close request during a pending confirmation as not confirmed.
        var closingStart = content.IndexOf(
            "private async void OnWindowClosing(",
            StringComparison.Ordinal);
        Assert.True(closingStart >= 0, "OnWindowClosing should exist.");
        var closingBody = content.Substring(closingStart);
        Assert.Contains(
            "_closeRequestPending || _trayCloseConfirmationPending",
            closingBody,
            StringComparison.Ordinal);

        var confirmStart = content.IndexOf(
            "private async Task<bool> ConfirmTrayCloseAsync(",
            StringComparison.Ordinal);
        Assert.True(confirmStart >= 0, "ConfirmTrayCloseAsync should exist.");
        var confirmBody = content.Substring(confirmStart);
        Assert.Contains(
            "_closeRequestPending || _trayCloseConfirmationPending",
            confirmBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "ignored-pending-close-confirmation",
            confirmBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "_trayCloseConfirmationPending = true;",
            confirmBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "_trayCloseConfirmationPending = false;",
            confirmBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowClose_ShouldHideTrayIconBeforeRuntimeDispose()
    {
        var content = ReadMainWindowSource();

        var closedStart = content.IndexOf(
            "private async void OnWindowClosed(",
            StringComparison.Ordinal);
        Assert.True(closedStart >= 0, "OnWindowClosed should exist.");
        var closedBody = content.Substring(
            closedStart,
            content.IndexOf("private void OnInAppNotificationRequested(", StringComparison.Ordinal) - closedStart);

        // WPF parity (frozen RootView.xaml:167 destroys the NotifyIcon together
        // with the main window): the window-close cleanup must hide the tray
        // icon instead of leaving it visible and its menu openable for the
        // whole runtime-dispose window. Tray command/menu handlers must already
        // be detached at that point so a teardown-time click cannot dispatch.
        var unsubscribeIndex = closedBody.IndexOf(
            "vm.PlatformCapabilityService.TrayCommandInvoked -= OnTrayCommandInvoked;",
            StringComparison.Ordinal);
        var hideIndex = closedBody.IndexOf(
            "TrayService.SetVisibleAsync(false, CancellationToken.None)",
            StringComparison.Ordinal);
        Assert.True(unsubscribeIndex >= 0, "Tray command handlers must be detached on window close.");
        Assert.True(hideIndex > unsubscribeIndex, "The tray icon must be hidden when the main window closes.");
    }

    [Fact]
    public void TrayCloseConfirm_ConfirmedPath_ShouldKeepSaveThenExecuteOrdering()
    {
        var content = ReadMainWindowSource();

        var dispatchStart = content.IndexOf(
            "private async Task DispatchTrayCommandAsync(",
            StringComparison.Ordinal);
        Assert.True(dispatchStart >= 0, "DispatchTrayCommandAsync should exist.");
        var dispatchBody = content.Substring(dispatchStart);

        var confirmIndex = dispatchBody.IndexOf("ConfirmTrayCloseAsync(", StringComparison.Ordinal);
        var saveIndex = dispatchBody.IndexOf("CompleteConfigurationSavesBeforeCloseAsync(", StringComparison.Ordinal);
        var executeIndex = dispatchBody.IndexOf("ExecuteTrayCommandAsync(", StringComparison.Ordinal);
        Assert.True(confirmIndex >= 0, "The tray confirm should run first.");
        Assert.True(saveIndex > confirmIndex, "Configuration saves must follow the confirm.");
        Assert.True(executeIndex > saveIndex, "The tray command must execute after the saves.");
    }

    // WPF parity (frozen TaskQueueViewModel.ConfirmExit + owner-less
    // MessageBox): a pending close confirmation never changes the window, so
    // while the dialog service transiently force-shows/un-minimizes the owner
    // the presentation is transient until the user explicitly changes it.
    [Theory]
    [InlineData(false, WindowState.Normal, false, true)]
    [InlineData(false, WindowState.Minimized, false, true)]
    [InlineData(true, WindowState.Minimized, false, true)]
    [InlineData(true, WindowState.Normal, false, false)]
    [InlineData(true, WindowState.Maximized, false, false)]
    [InlineData(false, WindowState.Normal, true, false)]
    [InlineData(true, WindowState.Minimized, true, false)]
    public void IsTransientCloseConfirmPresentation_ShouldMatchWpfModalSemantics(
        bool wasVisible,
        WindowState previousWindowState,
        bool presentationChangedByUser,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.IsTransientCloseConfirmPresentation(
                wasVisible,
                previousWindowState,
                presentationChangedByUser));
    }

    [Fact]
    public void CancelledCloseConfirm_ShouldKeepUserChangedPresentation()
    {
        var content = ReadMainWindowSource();

        // The cancel restore must not clobber an explicit presentation change
        // the user made during the modal: frozen WPF never touches the window
        // in ConfirmExit, so a tray ForceShow / ShowGui hotkey persists through
        // a cancel.
        var restoreBody = ExtractMemberBody(
            content,
            "private async Task RestorePresentationAfterCancelledCloseConfirmAsync(");
        var intentIndex = restoreBody.IndexOf(
            "_closeConfirmPresentationChangedByUser",
            StringComparison.Ordinal);
        var switchIndex = restoreBody.IndexOf(
            "switch (ResolveCloseConfirmCancelRestore(",
            StringComparison.Ordinal);
        Assert.True(intentIndex >= 0, "The cancel restore must honor user presentation changes.");
        Assert.True(intentIndex < switchIndex, "The user-intent check must run before the snapshot restore.");

        var showBody = ExtractMemberBody(
            content,
            "private async Task ShowAndActivateMainWindowAsync(");
        Assert.Contains(
            "MarkCloseConfirmPresentationChangedByUser();",
            showBody,
            StringComparison.Ordinal);

        var hideBody = ExtractMemberBody(
            content,
            "private async Task HideMainWindowToTrayAsync(");
        Assert.Contains(
            "MarkCloseConfirmPresentationChangedByUser();",
            hideBody,
            StringComparison.Ordinal);

        // WPF parity (frozen MainWindowManager.SwitchWindowState): a hidden or
        // taskbar-minimized window is shown by the first ShowGui press, so a
        // transient close-confirm force-show must be treated as hidden.
        var toggleBody = ExtractMemberBody(
            content,
            "private async Task ToggleMainWindowFromHotkeyAsync(");
        var transientIndex = toggleBody.IndexOf(
            "IsCloseConfirmTransientPresentation",
            StringComparison.Ordinal);
        var showIndex = toggleBody.IndexOf(
            "await ShowAndActivateMainWindowAsync(cancellationToken);",
            StringComparison.Ordinal);
        Assert.True(transientIndex >= 0, "The ShowGui hotkey must evaluate the transient close-confirm state.");
        Assert.True(transientIndex < showIndex, "The transient check must gate the show branch.");
    }

    [Fact]
    public void WindowChromeCloseConfirm_ShouldRestorePresentationOnCancel()
    {
        var content = ReadMainWindowSource();
        var closingBody = ExtractMemberBody(content, "private async void OnWindowClosing(");

        var trackIndex = closingBody.IndexOf(
            "BeginCloseConfirmPresentationTracking(",
            StringComparison.Ordinal);
        var confirmIndex = closingBody.IndexOf(
            "await ConfirmCloseAsync(",
            StringComparison.Ordinal);
        Assert.True(trackIndex >= 0, "The chrome close path must capture the pre-confirm presentation.");
        Assert.True(trackIndex < confirmIndex, "The snapshot must be captured before the confirm dialog.");

        // WPF parity (frozen CanCloseAsync -> ConfirmExit owner-less
        // MessageBox): a cancelled chrome close leaves a taskbar-minimized
        // window minimized instead of the dialog-restored Normal state.
        var cancelledIndex = closingBody.IndexOf("cancelled", StringComparison.Ordinal);
        var restoreIndex = closingBody.IndexOf(
            "await RestorePresentationAfterCancelledCloseConfirmAsync(",
            StringComparison.Ordinal);
        Assert.True(cancelledIndex >= 0, "The cancel diagnostic should be recorded.");
        Assert.True(restoreIndex > cancelledIndex, "A cancelled chrome close must restore the pre-confirm presentation.");

        var endTrackIndex = closingBody.IndexOf(
            "EndCloseConfirmPresentationTracking();",
            StringComparison.Ordinal);
        var latchReleaseIndex = closingBody.IndexOf(
            "_closeRequestPending = false;",
            StringComparison.Ordinal);
        Assert.True(endTrackIndex >= 0, "The chrome close path must end presentation tracking.");
        Assert.True(endTrackIndex < latchReleaseIndex, "Presentation tracking must end before the close latch is released.");
    }

    [Fact]
    public void AbortedCloseConfirm_ShouldRestorePresentationBeforeRethrow()
    {
        var content = ReadMainWindowSource();

        // If the confirm throws after the dialog service force-showed the
        // owner, both close paths must restore the pre-confirm presentation
        // instead of stranding the window in the transient state.
        foreach (var signature in new[]
        {
            "private async void OnWindowClosing(",
            "private async Task<bool> ConfirmTrayCloseAsync(",
        })
        {
            var body = ExtractMemberBody(content, signature);
            var catchIndex = body.IndexOf("catch", StringComparison.Ordinal);
            var restoreIndex = body.IndexOf(
                "await TryRestorePresentationAfterAbortedCloseConfirmAsync(",
                StringComparison.Ordinal);
            var rethrowIndex = body.IndexOf("throw;", StringComparison.Ordinal);
            Assert.True(catchIndex >= 0, $"{signature} must catch confirm failures.");
            Assert.True(restoreIndex > catchIndex, $"{signature} must restore the presentation when the confirm aborts.");
            Assert.True(rethrowIndex > restoreIndex, $"{signature} must restore before rethrowing.");
        }
    }

    [Fact]
    public void TransientCloseConfirmPresentation_ShouldNotFlushInAppFallbackQueue()
    {
        var content = ReadMainWindowSource();

        // The bounded fallback queue exists to deliver notifications once
        // presentation is genuinely possible; a transient close-confirm
        // force-show that a cancel re-hides must not flush (and strand) them.
        var canPresentStart = content.IndexOf(
            "private bool CanPresentInAppNotifications",
            StringComparison.Ordinal);
        Assert.True(canPresentStart >= 0, "CanPresentInAppNotifications should exist.");
        var canPresentBody = content.Substring(canPresentStart, 500);
        Assert.Contains(
            "!IsCloseConfirmTransientPresentation",
            canPresentBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrayCloseConfirm_ShouldHoldLatchThroughCancelRestore()
    {
        var content = ReadMainWindowSource();
        var confirmBody = ExtractMemberBody(
            content,
            "private async Task<bool> ConfirmTrayCloseAsync(");
        var restoreIndex = confirmBody.IndexOf(
            "await RestorePresentationAfterCancelledCloseConfirmAsync(",
            StringComparison.Ordinal);
        var latchReleaseIndex = confirmBody.IndexOf(
            "_trayCloseConfirmationPending = false;",
            StringComparison.Ordinal);
        Assert.True(restoreIndex >= 0, "The cancel path must restore the presentation.");
        Assert.True(
            latchReleaseIndex > restoreIndex,
            "The tray close latch must stay held until the cancel restore completes so a second tray Exit/Restart cannot snapshot the transient state.");
    }

    [Fact]
    public void CloseConfirm_ShouldGateOnStartRequestInclusiveIsRunning()
    {
        // R10-F1 wontfix lock-in: both close paths delegate to ConfirmCloseAsync,
        // which passes TaskQueuePage.IsRunning to the close-confirmation service.
        // IsRunning deliberately includes the start-request (Connecting) phase via
        // _isStartRequestActive. Frozen WPF ConfirmExit gates on AsstProxy.Running,
        // which flips true only on the first TaskChainStart, so WPF exits without
        // prompting while connecting; MAAUnified's prompt set is a protective
        // superset, and this divergence is locked by design.
        var content = ReadMainWindowSource();
        var confirmBody = ExtractMemberBody(
            content,
            "private Task<bool> ConfirmCloseAsync(");
        var serviceCallIndex = confirmBody.IndexOf(
            "_closeConfirmationService.ConfirmCloseAsync(",
            StringComparison.Ordinal);
        var isRunningIndex = confirmBody.IndexOf(
            "vm.TaskQueuePage.IsRunning,",
            StringComparison.Ordinal);
        Assert.True(serviceCallIndex >= 0, "ConfirmCloseAsync must delegate to the close-confirmation service.");
        Assert.True(
            isRunningIndex > serviceCallIndex,
            "ConfirmCloseAsync must pass TaskQueuePage.IsRunning as the running-task gate.");

        var pageSource = ReadTaskQueuePageViewModelSource();
        Assert.Contains(
            "public bool IsRunning => _isStartRequestActive || _isCoreQueueCleanupRequired || CurrentSessionState is SessionState.Running or SessionState.Stopping;",
            pageSource,
            StringComparison.Ordinal);
    }

    private static string ReadTaskQueuePageViewModelSource()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "ViewModels", "TaskQueue", "TaskQueuePageViewModel.cs");
        return File.ReadAllText(path);
    }

    private static string ExtractMemberBody(string content, string signature)
    {
        var start = content.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Member should exist: {signature}");
        var end = content.Length;
        foreach (var marker in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    protected " })
        {
            var next = content.IndexOf(marker, start + signature.Length, StringComparison.Ordinal);
            if (next >= 0 && next < end)
            {
                end = next;
            }
        }

        return content.Substring(start, end - start);
    }

    private static string ReadMainWindowSource()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml.cs");
        return File.ReadAllText(path);
    }
}
