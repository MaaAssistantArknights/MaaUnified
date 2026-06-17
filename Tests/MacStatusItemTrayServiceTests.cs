using MAAUnified.Platform;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class MacStatusItemTrayServiceTests
{
    public MacStatusItemTrayServiceTests()
    {
        AvaloniaTestApplication.Ensure();
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateOneStatusItemWithCompleteMenu()
    {
        var interop = new FakeMacStatusItemInterop();
        var service = new MacStatusItemTrayService(interop);

        var result = await service.InitializeAsync("MAAUnified", TrayMenuText.Default);

        Assert.True(result.Success);
        Assert.False(result.UsedFallback);
        Assert.Equal("macos-appkit-statusitem", result.Provider);
        Assert.Equal("window-menu", service.Capability.FallbackMode);
        Assert.Equal(1, interop.CreateCount);
        Assert.Equal("MAAUnified", interop.Tooltips.Single());
        AssertMenuOrder(interop.LastMenu);
    }

    [Fact]
    public async Task InitializeAsync_WhenRepeated_ShouldRefreshTooltipAndMenuWithoutDuplicateStatusItem()
    {
        var interop = new FakeMacStatusItemInterop();
        var service = new MacStatusItemTrayService(interop);
        var localized = new TrayMenuText(
            Start: "開始",
            Stop: "停止",
            ForceShow: "強制顯示",
            HideTray: "隱藏托盤",
            ToggleOverlay: "切換 Overlay",
            SwitchLanguage: "切換語言",
            Restart: "重啟",
            Exit: "退出");

        var first = await service.InitializeAsync("MAAUnified", TrayMenuText.Default);
        var second = await service.InitializeAsync("Localized App", localized);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, interop.CreateCount);
        Assert.Equal("Localized App", interop.LastTooltip);
        Assert.Equal("開始", interop.FindLast(TrayCommandId.Start).Title);
        Assert.Equal("退出", interop.FindLast(TrayCommandId.Exit).Title);
    }

    [Fact]
    public async Task SetMenuStateAsync_ShouldSynchronizeEnabledFlags()
    {
        var interop = new FakeMacStatusItemInterop();
        var service = new MacStatusItemTrayService(interop);
        await service.InitializeAsync("MAAUnified", TrayMenuText.Default);

        var result = await service.SetMenuStateAsync(new TrayMenuState(
            StartEnabled: false,
            StopEnabled: true,
            OverlayEnabled: false,
            ForceShowEnabled: true,
            HideTrayEnabled: false));

        Assert.True(result.Success);
        Assert.False(interop.FindLast(TrayCommandId.Start).IsEnabled);
        Assert.True(interop.FindLast(TrayCommandId.Stop).IsEnabled);
        Assert.False(interop.FindLast(TrayCommandId.ToggleOverlay).IsEnabled);
        Assert.True(interop.FindLast(TrayCommandId.ForceShow).IsEnabled);
        Assert.False(interop.FindLast(TrayCommandId.HideTray).IsEnabled);
        Assert.True(interop.FindLast(TrayCommandId.Restart).IsEnabled);
        Assert.True(interop.FindLast(TrayCommandId.Exit).IsEnabled);
    }

    [Fact]
    public async Task MenuActionTag_ShouldRaiseMappedTrayCommand()
    {
        var interop = new FakeMacStatusItemInterop();
        var service = new MacStatusItemTrayService(interop);
        TrayCommandEvent? invoked = null;
        service.CommandInvoked += (_, e) => invoked = e;
        await service.InitializeAsync("MAAUnified", TrayMenuText.Default);

        interop.Emit((int)TrayCommandId.Restart);

        Assert.NotNull(invoked);
        Assert.Equal(TrayCommandId.Restart, invoked!.Command);
        Assert.Equal("macos-appkit-statusitem", invoked.Source);
    }

    [Fact]
    public async Task SetVisibleAsync_ShouldHideAndRestoreStatusItem()
    {
        var interop = new FakeMacStatusItemInterop();
        var service = new MacStatusItemTrayService(interop);
        await service.InitializeAsync("MAAUnified", TrayMenuText.Default);

        var hidden = await service.SetVisibleAsync(false);
        var restored = await service.SetVisibleAsync(true);

        Assert.True(hidden.Success);
        Assert.True(restored.Success);
        Assert.Equal([true, false, true], interop.VisibleCalls);
    }

    [Fact]
    public async Task ShowAsync_ShouldReturnNotificationFallbackSuccess()
    {
        var interop = new FakeMacStatusItemInterop();
        var service = new MacStatusItemTrayService(interop);

        var result = await service.ShowAsync("title", "message");

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(PlatformExecutionMode.Fallback, result.ExecutionMode);
        Assert.Equal(PlatformErrorCodes.TrayFallback, result.ErrorCode);
        Assert.Equal("macos-appkit-statusitem", result.Provider);
    }

    [Fact]
    public async Task ShutdownAsync_ShouldBeIdempotentAndRemoveStatusItemOnce()
    {
        var interop = new FakeMacStatusItemInterop();
        var service = new MacStatusItemTrayService(interop);
        await service.InitializeAsync("MAAUnified", TrayMenuText.Default);

        var first = await service.ShutdownAsync();
        var second = await service.ShutdownAsync();
        service.Dispose();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, interop.RemoveCount);
        Assert.True(interop.Disposed);
    }

    private static void AssertMenuOrder(IReadOnlyList<MacStatusMenuItem> menu)
    {
        Assert.Equal(9, menu.Count);
        Assert.Equal((int)TrayCommandId.Start, menu[0].Tag);
        Assert.Equal((int)TrayCommandId.Stop, menu[1].Tag);
        Assert.True(menu[2].IsSeparator);
        Assert.Equal((int)TrayCommandId.ForceShow, menu[3].Tag);
        Assert.Equal((int)TrayCommandId.HideTray, menu[4].Tag);
        Assert.Equal((int)TrayCommandId.ToggleOverlay, menu[5].Tag);
        Assert.Equal((int)TrayCommandId.Restart, menu[6].Tag);
        Assert.True(menu[7].IsSeparator);
        Assert.Equal((int)TrayCommandId.Exit, menu[8].Tag);
    }

    private sealed class FakeMacStatusItemInterop : IMacStatusItemInterop
    {
        private Action<int>? _menuAction;

        public bool IsRuntimeAvailable => true;

        public int CreateCount { get; private set; }

        public int RemoveCount { get; private set; }

        public bool Disposed { get; private set; }

        public List<string> Tooltips { get; } = [];

        public string LastTooltip => Tooltips.Last();

        public IReadOnlyList<MacStatusMenuItem> LastMenu { get; private set; } = [];

        public List<bool> VisibleCalls { get; } = [];

        public MacStatusItemHandle CreateStatusItem(
            string tooltip,
            IReadOnlyList<MacStatusMenuItem> menuItems,
            Action<int> menuAction)
        {
            CreateCount++;
            _menuAction = menuAction;
            Tooltips.Add(tooltip);
            LastMenu = menuItems.ToList();
            return new MacStatusItemHandle
            {
                StatusItem = (nint)CreateCount,
                Target = (nint)(CreateCount + 1000),
            };
        }

        public void UpdateTooltip(MacStatusItemHandle handle, string tooltip)
        {
            Tooltips.Add(tooltip);
        }

        public void UpdateMenu(MacStatusItemHandle handle, IReadOnlyList<MacStatusMenuItem> menuItems)
        {
            LastMenu = menuItems.ToList();
        }

        public void SetVisible(MacStatusItemHandle handle, bool visible)
        {
            VisibleCalls.Add(visible);
        }

        public void RemoveStatusItem(MacStatusItemHandle handle)
        {
            if (handle.StatusItem == nint.Zero)
            {
                return;
            }

            RemoveCount++;
            handle.StatusItem = nint.Zero;
            handle.Target = nint.Zero;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        public void Emit(int tag)
        {
            _menuAction?.Invoke(tag);
        }

        public MacStatusMenuItem FindLast(TrayCommandId command)
            => LastMenu.Single(item => !item.IsSeparator && item.Tag == (int)command);
    }
}
