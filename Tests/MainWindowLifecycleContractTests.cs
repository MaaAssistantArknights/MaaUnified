namespace MAAUnified.Tests;

public sealed class MainWindowLifecycleContractTests
{
    [Fact]
    public void CloseWatchdog_ShouldStartAfterTheWindowConfigurationSaveGate()
    {
        var source = ReadMainWindowSource();

        var saveGate = source.IndexOf(
            "CompleteConfigurationSavesBeforeCloseAsync(\"App.Shell.Window.Close.ConfigSave\")",
            StringComparison.Ordinal);
        var watchdog = source.IndexOf(
            "ArmCloseCompletionWatchdog(\"App.Shell.Window.Close.Watchdog\")",
            StringComparison.Ordinal);
        var exit = source.IndexOf(
            "ExitApplicationAsync(\"App.Shell.Window.Close.Exit\")",
            StringComparison.Ordinal);

        Assert.True(saveGate >= 0, "Window-close configuration save gate was not found.");
        Assert.True(watchdog > saveGate, "Window-close watchdog must start after configuration saves complete.");
        Assert.True(exit > watchdog, "Window-close watchdog must be active before application exit begins.");
    }

    [Fact]
    public void CloseWatchdog_ShouldStartOnlyWhenTrayCommandWillCloseTheApplication()
    {
        var source = ReadMainWindowSource();

        Assert.DoesNotContain(
            "ArmCloseCompletionWatchdog($\"{confirmScope}.Watchdog\")",
            source,
            StringComparison.Ordinal);

        var saveGate = source.IndexOf("$\"{confirmScope}.ConfigSave\"", StringComparison.Ordinal);
        var executeCommand = source.IndexOf("var action = await VM.ExecuteTrayCommandAsync", StringComparison.Ordinal);
        var watchdog = source.IndexOf("ArmCloseCompletionWatchdog($\"{exitScope}.Watchdog\")", StringComparison.Ordinal);
        var exit = source.IndexOf("ExitApplicationAsync(exitScope, cancellationToken)", StringComparison.Ordinal);

        Assert.True(saveGate >= 0, "Tray close configuration save gate was not found.");
        Assert.True(executeCommand > saveGate, "Tray command must not execute until configuration saves complete.");
        Assert.True(watchdog > executeCommand, "Tray watchdog must only start for a confirmed close action.");
        Assert.True(exit > watchdog, "Tray watchdog must be active before application exit begins.");
    }

    private static string ReadMainWindowSource()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        return File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml.cs"));
    }
}
