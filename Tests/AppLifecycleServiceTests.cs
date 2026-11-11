using MAAUnified.App;
using MAAUnified.Application.Services;

namespace MAAUnified.Tests;

public sealed class AppLifecycleServiceTests
{
    [Fact]
    public void BuildRestartArguments_PreservesUserArgumentsAndAddsWaitMarker()
    {
        var arguments = ProcessAppLifecycleService.BuildRestartArguments(
            ["--config", "Night profile", ProcessAppLifecycleService.RestartWaitForInstanceArgument]);

        Assert.Equal(["--config", "Night profile", Program.RestartWaitForInstanceArgument], arguments);
    }

    [Fact]
    public void BuildRestartArguments_ForArgumentlessRestart_OnlyAddsWaitMarker()
    {
        var arguments = ProcessAppLifecycleService.BuildRestartArguments([]);

        Assert.Equal([ProcessAppLifecycleService.RestartWaitForInstanceArgument], arguments);
    }

    [Fact]
    public void BuildRestartStartInfo_ShouldNotForwardCommandLineArguments()
    {
        // WPF parity: Bootstrapper.ShutdownAndRestartWithoutArgs starts the current
        // executable with no Arguments, regardless of the original command line.
        var startInfo = ProcessAppLifecycleService.BuildRestartStartInfo("/tmp/MAAUnified");

        Assert.Equal("/tmp/MAAUnified", startInfo.FileName);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }
}
