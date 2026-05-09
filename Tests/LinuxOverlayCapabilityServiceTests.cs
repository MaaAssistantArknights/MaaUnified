using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class LinuxOverlayCapabilityServiceTests
{
    [Fact]
    public void ParseWmctrlTargets_ShouldCreateLinuxWindowTargets()
    {
        var output = """
            0x03c00007  0 987654 emulator.Emulator host Arknights - MuMu Player
            0x03c00008  0 987655 org.telegram.desktop host Telegram
            """;

        var targets = LinuxOverlayCapabilityService.ParseWmctrlTargets(output, currentProcessId: 9999);

        Assert.Equal(3, targets.Count);
        Assert.Equal("preview", targets[0].Id);
        Assert.True(targets[0].IsPrimary);

        var game = targets[1];
        Assert.Equal("x11:0x3C00007", game.Id);
        Assert.False(game.IsPrimary);
        Assert.Equal(0x03c00007, game.NativeHandle);
        Assert.Equal(987654, game.ProcessId);
        Assert.Equal("Emulator", game.ProcessName);
        Assert.Equal("Arknights - MuMu Player", game.WindowTitle);
        Assert.Contains("Arknights - MuMu Player", game.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseWmctrlTargets_ShouldSkipCurrentProcessDuplicatesAndBlankTitles()
    {
        var output = """
            0x03c00007  0 987654 emulator.Emulator host Arknights
            0x03c00007  0 987654 emulator.Emulator host Duplicate
            0x03c00008  0 4321 self.Self host Current Process Window
            0x03c00009  0 987655 blank.Blank host    
            not-a-window
            """;

        var targets = LinuxOverlayCapabilityService.ParseWmctrlTargets(output, currentProcessId: 4321);

        Assert.Equal(2, targets.Count);
        Assert.Equal("preview", targets[0].Id);
        Assert.Equal("x11:0x3C00007", targets[1].Id);
        Assert.Equal("Arknights", targets[1].WindowTitle);
    }

    [Fact]
    public async Task QueryTargetsAsync_WhenWmctrlFails_ShouldReturnPreviewFallback()
    {
        var service = new LinuxOverlayCapabilityService(new FakeCommandRunner(
            new LinuxOverlayCapabilityService.CommandResult(1, string.Empty, "Cannot open display")));

        var result = await service.QueryTargetsAsync();

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(PlatformExecutionMode.Fallback, result.ExecutionMode);
        var target = Assert.Single(result.Value!);
        Assert.Equal("preview", target.Id);
    }

    [Fact]
    public async Task QueryTargetsAsync_ShouldPreferNativeX11Enumerator_WhenAvailable()
    {
        var service = new LinuxOverlayCapabilityService(
            new FakeX11WindowEnumerator(
            [
                new OverlayTarget("preview", "Preview + Logs", true),
                new OverlayTarget("x11:0x3C00007", "Arknights - emulator - 987654", false, 0x03c00007, 987654, "emulator", "Arknights"),
            ]),
            new ThrowingCommandRunner());

        var result = await service.QueryTargetsAsync();

        Assert.True(result.Success);
        Assert.False(result.UsedFallback);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal("x11:0x3C00007", result.Value[1].Id);
    }

    [Fact]
    public async Task QueryTargetsAsync_WhenNativeX11HasNoWindows_ShouldFallbackToWmctrl()
    {
        var output = "0x03c00007  0 987654 emulator.Emulator host Arknights";
        var service = new LinuxOverlayCapabilityService(
            new FakeX11WindowEnumerator([new OverlayTarget("preview", "Preview + Logs", true)]),
            new FakeCommandRunner(new LinuxOverlayCapabilityService.CommandResult(0, output, string.Empty)));

        var result = await service.QueryTargetsAsync();

        Assert.True(result.Success);
        Assert.False(result.UsedFallback);
        Assert.Equal("x11:0x3C00007", result.Value![1].Id);
    }

    private sealed class FakeCommandRunner(LinuxOverlayCapabilityService.CommandResult result)
        : LinuxOverlayCapabilityService.ICommandRunner
    {
        public Task<LinuxOverlayCapabilityService.CommandResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken)
        {
            Assert.Equal("wmctrl", fileName);
            Assert.Equal("-lpx", arguments);
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingCommandRunner : LinuxOverlayCapabilityService.ICommandRunner
    {
        public Task<LinuxOverlayCapabilityService.CommandResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("wmctrl should not be used");
    }

    private sealed class FakeX11WindowEnumerator(IReadOnlyList<OverlayTarget> targets)
        : LinuxOverlayCapabilityService.IX11WindowEnumerator
    {
        public IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId)
            => targets;
    }
}
