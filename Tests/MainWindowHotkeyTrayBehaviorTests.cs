using Avalonia.Controls;
using MAAUnified.App.Views;

namespace MAAUnified.Tests;

public sealed class MainWindowHotkeyTrayBehaviorTests
{
    // WPF frozen-commit behavior: the ShowGui hotkey runs
    // MainWindowManager.SwitchWindowState(), which only minimizes the window;
    // hiding to the tray happens in MainWindowStateChanged and requires both
    // ShouldMinimizeToTray and ShouldUseTrayIcon.
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldHideWindowToTray_ShouldRequireBothTraySettings(
        bool useTray,
        bool minimizeToTray,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldHideWindowToTray(useTray, minimizeToTray));
    }

    [Fact]
    public void ShowGuiHotkey_ShouldMinimizeToTaskbarWhenTrayHidingIsNotConfigured()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml.cs");
        var content = File.ReadAllText(path);

        var toggleStart = content.IndexOf(
            "private async Task ToggleMainWindowFromHotkeyAsync",
            StringComparison.Ordinal);
        Assert.True(toggleStart >= 0, "ToggleMainWindowFromHotkeyAsync should exist.");
        var toggleBody = content.Substring(toggleStart);

        // The hotkey must not hide the window (removing it from the taskbar)
        // or force-show a temporary tray icon unless both tray settings allow
        // minimizing to the tray.
        Assert.Contains(
            "ShouldHideWindowToTray(vm.SettingsPage.UseTray, vm.SettingsPage.MinimizeToTray)",
            toggleBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowState = WindowState.Minimized;",
            toggleBody,
            StringComparison.Ordinal);
        Assert.True(
            toggleBody.IndexOf("ShouldHideWindowToTray(", StringComparison.Ordinal)
                < toggleBody.IndexOf("HideMainWindowToTrayAsync(", StringComparison.Ordinal),
            "The hide-to-tray path must be gated by ShouldHideWindowToTray.");
    }

    // WPF parity (frozen MaaHotKeyActionHandler.HandleLinkStart): the
    // BackgroundLinkStarted/BackgroundLinkStopped toast is shown only while the
    // main window is minimized; WPF windows hidden to the tray keep
    // WindowState.Minimized, so a hidden window matches too. The frozen WPF
    // close confirmation is an owner-less MessageBox that never changes window
    // state, so the gate keeps seeing the pre-confirm minimized/hidden state
    // while the confirm is open; MAAUnified's transient close-confirm force-show
    // (platform-necessary for Avalonia ShowDialog) must therefore also count as
    // background until the user explicitly changes the presentation.
    [Theory]
    [InlineData(true, WindowState.Normal, false, false)]
    [InlineData(true, WindowState.Maximized, false, false)]
    [InlineData(true, WindowState.Minimized, false, true)]
    [InlineData(false, WindowState.Normal, false, true)]
    [InlineData(false, WindowState.Minimized, false, true)]
    [InlineData(true, WindowState.Normal, true, true)]
    [InlineData(true, WindowState.Maximized, true, true)]
    [InlineData(true, WindowState.Minimized, true, true)]
    [InlineData(false, WindowState.Normal, true, true)]
    [InlineData(false, WindowState.Minimized, true, true)]
    public void ShouldNotifyBackgroundLinkStartToggle_ShouldMatchWpfMinimizedGate(
        bool isVisible,
        WindowState windowState,
        bool isCloseConfirmTransientPresentation,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldNotifyBackgroundLinkStartToggle(
                isVisible,
                windowState,
                isCloseConfirmTransientPresentation));
    }

    [Fact]
    public void LinkStartHotkey_ShouldToggleStartStopInsteadOfDispatchingPlainStart()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml.cs");
        var content = File.ReadAllText(path);

        var handlerStart = content.IndexOf(
            "private async void OnGlobalHotkeyTriggered",
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "OnGlobalHotkeyTriggered should exist.");
        var handlerBody = content.Substring(handlerStart);

        // The LinkStart hotkey must go through the start/stop toggle, not the
        // plain tray Start command (which is a state-guarded no-op while running).
        Assert.Contains(
            "await ExecuteLinkStartHotkeyAsync();",
            handlerBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DispatchTrayCommandAsync(TrayCommandId.Start, \"hotkey-linkstart\")",
            handlerBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinkStartHotkey_ShouldEmitUseNotifyGatedBackgroundToastOnlyWhileMinimized()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml.cs");
        var content = File.ReadAllText(path);

        var methodStart = content.IndexOf(
            "private async Task NotifyBackgroundLinkStartToggleAsync",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "NotifyBackgroundLinkStartToggleAsync should exist.");
        var methodBody = content.Substring(methodStart);

        Assert.Contains("ShouldNotifyBackgroundLinkStartToggle(IsVisible, WindowState, IsCloseConfirmTransientPresentation)", methodBody, StringComparison.Ordinal);
        Assert.Contains("\"BackgroundLinkStarted\"", methodBody, StringComparison.Ordinal);
        Assert.Contains("\"BackgroundLinkStopped\"", methodBody, StringComparison.Ordinal);
        Assert.Contains(
            "UseSystemNotification: vm.SettingsPage.UseNotify",
            methodBody,
            StringComparison.Ordinal);
    }

    // Round 9 R9-F1 (wontfix-by-design): unlike frozen WPF
    // (WindowManager.ShowWindow forces WindowState.Normal and ForceShow uses
    // SwShownormal, so a window hidden to the tray while Maximized always
    // returns Normal), MAAUnified deliberately preserves the user's window
    // state across a hide-to-tray/show cycle. Only a real OS-minimized window
    // is normalized on show.
    [Theory]
    [InlineData(WindowState.Normal, false)]
    [InlineData(WindowState.Maximized, false)]
    [InlineData(WindowState.FullScreen, false)]
    [InlineData(WindowState.Minimized, true)]
    public void ShouldNormalizeWindowStateOnShow_ShouldPreserveNonMinimizedStates(
        WindowState windowState,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldNormalizeWindowStateOnShow(windowState));
    }

    // Locks the hide side of R9-F1: the in-app minimize button and the ShowGui
    // hotkey hide-to-tray paths must not normalize the window state before
    // hiding, so a Maximized window stays Maximized while hidden and the show
    // path restores it Maximized.
    [Fact]
    public void HideToTrayFromMaximized_ShouldKeepWindowStateAcrossHideAndShow()
    {
        var root = GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Views", "MainWindow.axaml.cs");
        var content = File.ReadAllText(path);

        var minimizeButtonBody = ExtractMethodBody(content, "private async void OnWindowMinimizeRequested");
        Assert.Contains("e.Cancel = true;", minimizeButtonBody, StringComparison.Ordinal);
        Assert.Contains(
            "HideMainWindowToTrayAsync(requireMinimizedState: false)",
            minimizeButtonBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WindowState = WindowState.", minimizeButtonBody, StringComparison.Ordinal);

        var hotkeyBody = ExtractMethodBody(content, "private async Task ToggleMainWindowFromHotkeyAsync");
        Assert.Contains(
            "HideMainWindowToTrayAsync(requireMinimizedState: false",
            hotkeyBody,
            StringComparison.Ordinal);

        var hideCoreBody = ExtractMethodBody(content, "private async Task HideMainWindowToTrayCoreAsync");
        Assert.Contains("Hide();", hideCoreBody, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowState = WindowState.", hideCoreBody, StringComparison.Ordinal);

        var showBody = ExtractMethodBody(content, "private void ShowAndActivateMainWindow()");
        Assert.Contains("ShouldNormalizeWindowStateOnShow(WindowState)", showBody, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string content, string signature)
    {
        var start = content.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} should exist.");
        var braceStart = content.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"{signature} should have a body.");
        var depth = 0;
        for (var i = braceStart; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                depth++;
            }
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return content.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces in {signature}.");
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }
}
