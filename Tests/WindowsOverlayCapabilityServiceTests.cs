using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class WindowsOverlayCapabilityServiceTests
{
    [Fact]
    public async Task SetVisibleAsync_WithSelectedTarget_ShouldAttachImmediatelyAndEmitShowNative()
    {
        var api = new FakeNativeApi();
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100));
        api.AddWindow(0x20, "Arknights", rect: new WindowsOverlayCapabilityService.OverlayRect(10, 20, 210, 320));
        var service = new WindowsOverlayCapabilityService(api);
        var events = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => events.Add(e);

        var bind = await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 0.85);
        var select = await service.SelectTargetAsync("hwnd:20");
        var visible = await service.SetVisibleAsync(true);

        Assert.True(bind.Success);
        Assert.True(select.Success);
        Assert.True(visible.Success);
        var showNative = Assert.Single(events, e => string.Equals(e.Action, "show-native", StringComparison.Ordinal));
        Assert.Equal(OverlayRuntimeMode.Native, showNative.Mode);
        Assert.True(showNative.Visible);
        Assert.Equal("hwnd:20", showNative.TargetId);
        Assert.NotEmpty(api.SetWindowPosRecords);
        Assert.True(api.ShowWindowCalls > 0);
    }

    [Fact]
    public async Task SelectTargetAsync_WhileVisible_ShouldAttachImmediatelyAndEmitTargetChange()
    {
        var api = new FakeNativeApi();
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100));
        api.AddWindow(0x20, "Arknights A", rect: new WindowsOverlayCapabilityService.OverlayRect(10, 20, 210, 320));
        api.AddWindow(0x30, "Arknights B", rect: new WindowsOverlayCapabilityService.OverlayRect(40, 50, 240, 360));
        var service = new WindowsOverlayCapabilityService(api);
        var events = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => events.Add(e);

        await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 0.85);
        await service.SelectTargetAsync("hwnd:20");
        await service.SetVisibleAsync(true);
        events.Clear();

        var result = await service.SelectTargetAsync("hwnd:30");

        Assert.True(result.Success);
        var targetChange = Assert.Single(events, e => string.Equals(e.Action, "target-change", StringComparison.Ordinal));
        Assert.Equal(OverlayRuntimeMode.Native, targetChange.Mode);
        Assert.True(targetChange.Visible);
        Assert.Equal("hwnd:30", targetChange.TargetId);
    }

    [Fact]
    public async Task SelectTargetAsync_WhileVisibleAndAttachFails_ShouldReturnOverlayAttachFailedAndEmitPreviewFallback()
    {
        var api = new FakeNativeApi();
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100));
        api.AddWindow(0x20, "Arknights A", rect: new WindowsOverlayCapabilityService.OverlayRect(10, 20, 210, 320));
        api.AddWindow(0x30, "Arknights B", rect: new WindowsOverlayCapabilityService.OverlayRect(40, 50, 240, 360));
        var service = new WindowsOverlayCapabilityService(api);
        var events = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => events.Add(e);

        await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 0.85);
        await service.SelectTargetAsync("hwnd:20");
        await service.SetVisibleAsync(true);
        events.Clear();
        api.FailSetWindowPos = true;

        var result = await service.SelectTargetAsync("hwnd:30");

        Assert.False(result.Success);
        Assert.Equal(PlatformErrorCodes.OverlayAttachFailed, result.ErrorCode);
        var fallback = Assert.Single(events, e => string.Equals(e.Action, "fallback-enter", StringComparison.Ordinal));
        Assert.Equal(OverlayRuntimeMode.Preview, fallback.Mode);
        Assert.True(fallback.Visible);
        Assert.Equal("preview", fallback.TargetId);
    }

    [Fact]
    public async Task SetVisibleAsync_WithPreviewTarget_ShouldShowPreviewHostAndEmitFallback()
    {
        var api = new FakeNativeApi();
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100));
        var service = new WindowsOverlayCapabilityService(api);
        var events = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => events.Add(e);

        var bind = await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 0.85);
        var select = await service.SelectTargetAsync("preview");
        var visible = await service.SetVisibleAsync(true);

        Assert.True(bind.Success);
        Assert.True(select.Success);
        Assert.True(visible.Success);
        var preview = Assert.Single(events, e => string.Equals(e.Action, "fallback-enter", StringComparison.Ordinal));
        Assert.Equal(OverlayRuntimeMode.Preview, preview.Mode);
        Assert.True(preview.Visible);
        Assert.Equal("preview", preview.TargetId);
        Assert.True(api.ShowWindowCalls > 0);
        Assert.NotEqual(0L, (long)(api.GetExStyle(0x10) & (nint)0x08000000));
    }

    [Fact]
    public async Task SyncNowForTesting_WhenTargetDisappears_ShouldEmitTargetLostAndSwitchToPreview()
    {
        var api = new FakeNativeApi();
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100));
        api.AddWindow(0x20, "Arknights", rect: new WindowsOverlayCapabilityService.OverlayRect(10, 20, 210, 320));
        var service = new WindowsOverlayCapabilityService(api);
        var events = new List<OverlayStateChangedEvent>();
        service.OverlayStateChanged += (_, e) => events.Add(e);

        await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 0.85);
        await service.SelectTargetAsync("hwnd:20");
        await service.SetVisibleAsync(true);
        events.Clear();
        api.RemoveWindow(0x20);

        service.SyncNowForTesting();

        var targetLost = Assert.Single(events, e => string.Equals(e.Action, "target-lost", StringComparison.Ordinal));
        Assert.Equal(OverlayRuntimeMode.Preview, targetLost.Mode);
        Assert.True(targetLost.Visible);
        Assert.Equal("preview", targetLost.TargetId);
    }

    [Fact]
    public async Task NativeAttach_ShouldOwnHostToTargetAndLeaveTopmostBandLikeWpf()
    {
        var api = new FakeNativeApi();
        // The Avalonia host window is created with Topmost=true (WS_EX_TOPMOST).
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100), initialExStyle: 0x00000008);
        api.AddWindow(0x20, "Arknights", rect: new WindowsOverlayCapabilityService.OverlayRect(10, 20, 210, 320));
        var service = new WindowsOverlayCapabilityService(api);

        var bind = await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 1.0);
        var select = await service.SelectTargetAsync("hwnd:20");
        var visible = await service.SetVisibleAsync(true);
        service.Dispose();

        Assert.True(bind.Success);
        Assert.True(select.Success);
        Assert.True(visible.Success);

        // WPF parity (OverlayWindow.InitializeAndShowAsync): the host is owned by the target and
        // leaves the topmost band, so it is covered together with the target window.
        Assert.Equal((nint)0x20, api.GetOwner(0x10));
        Assert.Equal((nint)0, api.GetExStyle(0x10) & (nint)0x00000008);
        var records = api.SetWindowPosRecords.ToList();
        Assert.Contains(records, r => r.Hwnd == (nint)0x10 && r.InsertAfter == (nint)(-2));
        Assert.All(
            records.Where(r => r.Cx > 0),
            r =>
            {
                Assert.Equal(nint.Zero, r.InsertAfter);
                Assert.True((r.Flags & 0x0004) != 0, "SWP_NOZORDER must be set on position syncs.");
            });
    }

    [Fact]
    public async Task TargetLost_ShouldClearOwnershipAndRestoreFloatingTopmostPreview()
    {
        var api = new FakeNativeApi();
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100), initialExStyle: 0x00000008);
        api.AddWindow(0x20, "Arknights", rect: new WindowsOverlayCapabilityService.OverlayRect(10, 20, 210, 320));
        var service = new WindowsOverlayCapabilityService(api);

        await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 1.0);
        await service.SelectTargetAsync("hwnd:20");
        await service.SetVisibleAsync(true);
        api.RemoveWindow(0x20);

        service.SyncNowForTesting();
        service.Dispose();

        // Preview mode keeps MAAUnified's floating topmost presentation with no owner.
        Assert.Equal(nint.Zero, api.GetOwner(0x10));
        Assert.NotEqual((nint)0, api.GetExStyle(0x10) & (nint)0x00000008);
        Assert.Contains(
            api.SetWindowPosRecords.ToList(),
            r => r.Hwnd == (nint)0x10 && r.InsertAfter == (nint)(-1) && (r.Flags & 0x0002) != 0);
    }

    [Fact]
    public async Task PeriodicSync_ShouldNotForceShowHostWindowLikeWpfUpdatePosition()
    {
        var api = new FakeNativeApi();
        api.AddWindow(0x10, "Overlay Host", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 100, 100));
        api.AddWindow(0x20, "Arknights", rect: new WindowsOverlayCapabilityService.OverlayRect(10, 20, 210, 320));
        var service = new WindowsOverlayCapabilityService(api);

        await service.BindHostWindowAsync((nint)0x10, clickThrough: true, opacity: 1.0);
        await service.SelectTargetAsync("hwnd:20");
        var visible = await service.SetVisibleAsync(true);
        Assert.True(visible.Success);

        // The attach itself shows the host once (WPF InitializeAndShowAsync -> Show()).
        Assert.Contains(
            api.SetWindowPosRecords.ToList(),
            r => r.Hwnd == (nint)0x10 && r.Cx > 0 && (r.Flags & 0x0040) != 0);
        Assert.True(api.ShowWindowCalls > 0);

        api.SetWindowPosRecords.Clear();
        var showWindowCallsBeforeSync = api.ShowWindowCalls;

        // WPF parity (OverlayWindow.UpdatePosition, freeze 23ddd27): the periodic position sync uses
        // no SWP_SHOWWINDOW and no ShowWindow, so an owned overlay stays hidden while the target is
        // minimized and reappears only when the OS restores the owner.
        service.SyncNowForTesting();
        service.Dispose();

        var syncRecords = api.SetWindowPosRecords.ToList();
        Assert.Contains(syncRecords, r => r.Hwnd == (nint)0x10 && r.Cx > 0);
        Assert.All(
            syncRecords.Where(r => r.Cx > 0),
            r => Assert.True((r.Flags & 0x0040) == 0, "SWP_SHOWWINDOW must not be set on periodic syncs."));
        Assert.Equal(showWindowCallsBeforeSync, api.ShowWindowCalls);
    }

    [Fact]
    public void FindVisibleWindowsByExactTitle_ShouldMatchRawTitlesWithoutCapLikeWpf()
    {
        var api = new FakeNativeApi();
        for (var i = 0; i < 90; i++)
        {
            api.AddWindow(0x1000 + i, "明日方舟", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 10, 10));
        }

        api.AddWindow(0x2000, "明日方舟 ", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 10, 10));
        api.AddWindow(0x2001, "明日方舟", rect: new WindowsOverlayCapabilityService.OverlayRect(0, 0, 10, 10), visible: false);
        var service = new WindowsOverlayCapabilityService(api);

        var matches = service.FindVisibleWindowsByExactTitle("明日方舟");

        // WPF FindWindowsByName has no result cap and compares raw titles exactly (no trimming).
        Assert.Equal(90, matches.Count);
        Assert.DoesNotContain((nint)0x2000, matches);
        Assert.DoesNotContain((nint)0x2001, matches);
    }

    private sealed class FakeNativeApi : WindowsOverlayCapabilityService.INativeApi
    {
        private const int GwlExStyle = -20;
        private const int GwlHwndParent = -8;

        private readonly Dictionary<nint, WindowState> _windows = [];

        public List<SetWindowPosRecord> SetWindowPosRecords { get; } = [];

        public List<SetWindowLongRecord> SetWindowLongRecords { get; } = [];

        public int ShowWindowCalls { get; private set; }

        public bool FailSetWindowPos { get; set; }

        public void AddWindow(
            nint handle,
            string title,
            WindowsOverlayCapabilityService.OverlayRect rect,
            bool visible = true,
            nint initialExStyle = 0)
        {
            _windows[handle] = new WindowState
            {
                Rect = rect,
                Title = title,
                Visible = visible,
                ExStyle = initialExStyle,
            };
        }

        public void RemoveWindow(nint handle)
        {
            _windows.Remove(handle);
        }

        public nint GetExStyle(nint handle)
            => _windows[handle].ExStyle;

        public nint GetOwner(nint handle)
            => _windows[handle].Owner;

        public IEnumerable<nint> EnumerateWindows()
            => _windows.Keys;

        public bool IsWindow(nint hWnd)
            => _windows.ContainsKey(hWnd);

        public bool IsWindowVisible(nint hWnd)
            => _windows.TryGetValue(hWnd, out var window) && window.Visible;

        public int GetWindowTextLength(nint hWnd)
            => _windows.TryGetValue(hWnd, out var window) ? window.Title.Length : 0;

        public string GetWindowText(nint hWnd)
            => _windows.TryGetValue(hWnd, out var window) ? window.Title : string.Empty;

        public uint GetWindowThreadProcessId(nint hWnd)
            => 4242;

        public bool TryGetWindowRect(nint hWnd, out WindowsOverlayCapabilityService.OverlayRect rect)
        {
            if (_windows.TryGetValue(hWnd, out var window))
            {
                rect = window.Rect;
                return true;
            }

            rect = default;
            return false;
        }

        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags)
        {
            lock (SetWindowPosRecords)
            {
                SetWindowPosRecords.Add(new SetWindowPosRecord(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags));
            }

            return !FailSetWindowPos;
        }

        public bool ShowWindow(nint hWnd, int nCmdShow)
        {
            ShowWindowCalls++;
            return true;
        }

        public nint GetWindowLongPtr(nint hWnd, int nIndex)
        {
            if (!_windows.TryGetValue(hWnd, out var window))
            {
                return nint.Zero;
            }

            return nIndex switch
            {
                GwlExStyle => window.ExStyle,
                GwlHwndParent => window.Owner,
                _ => nint.Zero,
            };
        }

        public int GetLastWin32Error()
            => 0;

        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong)
        {
            SetWindowLongRecords.Add(new SetWindowLongRecord(hWnd, nIndex, dwNewLong));
            if (_windows.TryGetValue(hWnd, out var window))
            {
                if (nIndex == GwlExStyle)
                {
                    window.ExStyle = dwNewLong;
                }
                else if (nIndex == GwlHwndParent)
                {
                    window.Owner = dwNewLong;
                }
            }

            return dwNewLong;
        }

        public bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags)
            => true;

        private sealed class WindowState
        {
            public required WindowsOverlayCapabilityService.OverlayRect Rect { get; init; }

            public required string Title { get; init; }

            public bool Visible { get; init; }

            public nint ExStyle { get; set; }

            public nint Owner { get; set; }
        }

        public sealed record SetWindowPosRecord(nint Hwnd, nint InsertAfter, int X, int Y, int Cx, int Cy, uint Flags);

        public sealed record SetWindowLongRecord(nint Hwnd, int Index, nint Value);
    }
}
