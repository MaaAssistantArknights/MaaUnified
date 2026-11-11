using MAAUnified.App.Views;

namespace MAAUnified.Tests;

public sealed class MainWindowGlobalHotkeyDispatchTests
{
    [Fact]
    public void DispatchGlobalHotkeyCallback_WhenOnUiThread_RunsCallbackInline()
    {
        var ranInline = false;
        var posted = false;

        MainWindow.DispatchGlobalHotkeyCallback(
            isUiThread: true,
            postToUiThread: _ => posted = true,
            callback: () => ranInline = true);

        Assert.True(ranInline);
        Assert.False(posted);
    }

    [Fact]
    public void DispatchGlobalHotkeyCallback_WhenOffUiThread_PostsCallbackToUiThread()
    {
        var ranInline = false;
        Action? postedCallback = null;

        MainWindow.DispatchGlobalHotkeyCallback(
            isUiThread: false,
            postToUiThread: callback => postedCallback = callback,
            callback: () => ranInline = true);

        // Hotkey providers (SharpHook event loop, Linux portal D-Bus signals) raise on
        // background threads; the callback must not run inline on the provider thread.
        Assert.False(ranInline);
        Assert.NotNull(postedCallback);

        postedCallback!();
        Assert.True(ranInline);
    }
}
