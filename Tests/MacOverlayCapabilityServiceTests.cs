using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class MacOverlayCapabilityServiceTests
{
    [Fact]
    public async Task QueryTargetsAsync_ShouldAddPreviewTargetAndReturnMacTargets()
    {
        var service = new MacOverlayCapabilityService(
            new FakeMacWindowEnumerator(
            [
                new OverlayTarget(
                    "mac:0x2A",
                    "Arknights - emulator - 123",
                    IsPrimary: false,
                    NativeHandle: 0x2A,
                    ProcessId: 123,
                    ProcessName: "emulator",
                    WindowTitle: "Arknights"),
            ]));

        var result = await service.QueryTargetsAsync();

        Assert.True(result.Success);
        Assert.False(result.UsedFallback);
        Assert.Equal(PlatformExecutionMode.Native, result.ExecutionMode);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal("preview", result.Value[0].Id);
        Assert.Equal("mac:0x2A", result.Value[1].Id);
        Assert.Equal(0x2A, result.Value[1].NativeHandle);
    }

    [Fact]
    public async Task QueryTargetsAsync_WhenEnumeratorThrows_ShouldReturnPreviewFallback()
    {
        var service = new MacOverlayCapabilityService(new ThrowingMacWindowEnumerator());

        var result = await service.QueryTargetsAsync();

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(PlatformExecutionMode.Fallback, result.ExecutionMode);
        var target = Assert.Single(result.Value!);
        Assert.Equal("preview", target.Id);
    }

    [Fact]
    public async Task SelectTargetAsync_WhenMacTargetIsVisible_ShouldEmitPreviewFallbackState()
    {
        var service = new MacOverlayCapabilityService(new FakeMacWindowEnumerator([]));
        var emitted = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => emitted.Add(e);

        var bindResult = await service.BindHostWindowAsync(0x100, clickThrough: true, opacity: 0.75);
        var selectResult = await service.SelectTargetAsync("mac:0x2A");
        var visibleResult = await service.SetVisibleAsync(true);

        Assert.True(bindResult.Success);
        Assert.True(selectResult.Success);
        Assert.True(visibleResult.Success);
        Assert.True(visibleResult.UsedFallback);
        var state = Assert.Single(emitted);
        Assert.Equal(OverlayRuntimeMode.Preview, state.Mode);
        Assert.Equal("mac:0x2A", state.TargetId);
        Assert.True(state.UsedFallback);
        Assert.Equal(PlatformErrorCodes.OverlayPreviewMode, state.ErrorCode);
    }

    [Fact]
    public async Task SetVisibleAsync_WhenHiding_ShouldEmitHiddenState()
    {
        var service = new MacOverlayCapabilityService(new FakeMacWindowEnumerator([]));
        var emitted = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => emitted.Add(e);

        var result = await service.SetVisibleAsync(false);

        Assert.True(result.Success);
        Assert.False(result.UsedFallback);
        var state = Assert.Single(emitted);
        Assert.Equal(OverlayRuntimeMode.Hidden, state.Mode);
        Assert.False(state.Visible);
        Assert.Equal("preview", state.TargetId);
    }

    [Fact]
    public async Task SetVisibleAsync_WhenHostIsMissing_ShouldNotFlipVisibleState()
    {
        var service = new MacOverlayCapabilityService(new FakeMacWindowEnumerator([]));
        var emitted = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => emitted.Add(e);

        var visibleResult = await service.SetVisibleAsync(true);
        var selectResult = await service.SelectTargetAsync("mac:0x2A");

        Assert.False(visibleResult.Success);
        Assert.Equal(PlatformErrorCodes.OverlayHostNotBound, visibleResult.ErrorCode);
        Assert.True(selectResult.Success);
        Assert.Empty(emitted);
    }

    private sealed class FakeMacWindowEnumerator(IReadOnlyList<OverlayTarget> targets)
        : MacOverlayCapabilityService.IMacWindowEnumerator
    {
        public IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId)
            => targets;
    }

    private sealed class ThrowingMacWindowEnumerator : MacOverlayCapabilityService.IMacWindowEnumerator
    {
        public IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId)
            => throw new InvalidOperationException("CoreGraphics unavailable");
    }
}
