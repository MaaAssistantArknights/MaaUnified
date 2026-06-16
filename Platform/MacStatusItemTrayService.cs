using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace MAAUnified.Platform;

internal sealed record MacStatusMenuItem(
    string Title,
    int Tag,
    bool IsSeparator,
    bool IsEnabled);

internal sealed class MacStatusItemHandle
{
    public nint StatusItem { get; set; }

    public nint Target { get; set; }
}

internal interface IMacStatusItemInterop : IDisposable
{
    bool IsRuntimeAvailable { get; }

    MacStatusItemHandle CreateStatusItem(
        string tooltip,
        IReadOnlyList<MacStatusMenuItem> menuItems,
        Action<int> menuAction);

    void UpdateTooltip(MacStatusItemHandle handle, string tooltip);

    void UpdateMenu(MacStatusItemHandle handle, IReadOnlyList<MacStatusMenuItem> menuItems);

    void SetVisible(MacStatusItemHandle handle, bool visible);

    void RemoveStatusItem(MacStatusItemHandle handle);
}

public sealed class MacStatusItemTrayService : ITrayService, IDisposable
{
    internal const string EnableEnvironmentVariable = "MAA_PLATFORM_ENABLE_MAC_STATUS_ITEM";

    private readonly IMacStatusItemInterop _interop;
    private readonly CommandNotificationService _notificationFallback = new();
    private readonly ITrayService _fallbackService;
    private MacStatusItemHandle? _statusItem;
    private bool _initialized;
    private bool _fallbackMode;
    private bool _visible = true;
    private string _appTitle = "MAAUnified";
    private TrayMenuText _menuText = TrayMenuText.Default;
    private TrayMenuState _menuState = new(true, true, true, true, true);

    public MacStatusItemTrayService()
        : this(new MacStatusItemNativeInterop(), CreateFallbackService())
    {
    }

    internal MacStatusItemTrayService(IMacStatusItemInterop interop)
        : this(interop, new WindowMenuTrayService())
    {
    }

    private MacStatusItemTrayService(IMacStatusItemInterop interop, ITrayService fallbackService)
    {
        _interop = interop;
        _fallbackService = fallbackService;
        _fallbackService.CommandInvoked += (_, e) => RaiseCommand(e.Command);
    }

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "macOS status item tray integration is available via AppKit NSStatusItem.",
        Provider: "macos-appkit-statusitem",
        HasFallback: true,
        FallbackMode: "avalonia-trayicon/window-menu");

    public event EventHandler<TrayCommandEvent>? CommandInvoked;

    public event EventHandler<TrayMenuRequestEvent>? MenuRequested;

    public static bool TryCreate([NotNullWhen(true)] out MacStatusItemTrayService? service)
    {
        service = null;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var interop = new MacStatusItemNativeInterop();
        if (!interop.IsRuntimeAvailable)
        {
            interop.Dispose();
            return false;
        }

        service = new MacStatusItemTrayService(interop, CreateFallbackService());
        return true;
    }

    public async Task<PlatformOperationResult> InitializeAsync(
        string appTitle,
        TrayMenuText? menuText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _appTitle = NormalizeTitle(appTitle);
        _menuText = menuText ?? TrayMenuText.Default;

        if (_fallbackMode)
        {
            return await _fallbackService.InitializeAsync(_appTitle, _menuText, cancellationToken);
        }

        try
        {
            await RunOnUiThreadAsync(
                () =>
                {
                    var items = BuildMenuItems();
                    if (_initialized && _statusItem is not null)
                    {
                        _interop.UpdateTooltip(_statusItem, _appTitle);
                        _interop.UpdateMenu(_statusItem, items);
                        _interop.SetVisible(_statusItem, _visible);
                        return;
                    }

                    _statusItem = _interop.CreateStatusItem(_appTitle, items, HandleMenuActionTag);
                    _interop.SetVisible(_statusItem, _visible);
                    _initialized = true;
                },
                cancellationToken);

            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                "macOS status item tray service initialized.",
                "tray.initialize");
        }
        catch (Exception ex)
        {
            return await SwitchToFallbackTrayAsync(_appTitle, ex, cancellationToken);
        }
    }

    public async Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return await _fallbackService.ShutdownAsync(cancellationToken);
        }

        try
        {
            await RunOnUiThreadAsync(
                () =>
                {
                    DisposeNative();
                    _initialized = false;
                    _fallbackMode = false;
                },
                cancellationToken);

            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                "macOS status item tray service shutdown completed.",
                "tray.shutdown");
        }
        catch (Exception ex)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                $"macOS status item tray shutdown failed: {ex.Message}",
                PlatformErrorCodes.TrayInitFailed,
                "tray.shutdown");
        }
    }

    public async Task<PlatformOperationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return await _fallbackService.ShowAsync(title, message, cancellationToken);
        }

        var fallback = await _notificationFallback.NotifyAsync(title, message, cancellationToken);
        if (!fallback.Success)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "macOS tray notification fallback could not be delivered.",
                PlatformErrorCodes.TrayFallback,
                "tray.show",
                usedFallback: true);
        }

        return PlatformOperation.FallbackSuccess(
            Capability.Provider,
            "macOS tray notification fallback delivered via notification service.",
            "tray.show",
            PlatformErrorCodes.TrayFallback);
    }

    public async Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return await _fallbackService.SetMenuStateAsync(state, cancellationToken);
        }

        _menuState = state;
        if (_initialized && _statusItem is not null)
        {
            try
            {
                await RunOnUiThreadAsync(
                    () => _interop.UpdateMenu(_statusItem, BuildMenuItems()),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return PlatformOperation.Failed(
                    Capability.Provider,
                    $"macOS status item menu state update failed: {ex.Message}",
                    PlatformErrorCodes.TrayMenuDispatchFailed,
                    "tray.setMenuState");
            }
        }

        return PlatformOperation.NativeSuccess(
            Capability.Provider,
            "macOS status item tray menu state updated.",
            "tray.setMenuState");
    }

    public async Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return await _fallbackService.SetVisibleAsync(visible, cancellationToken);
        }

        _visible = visible;
        if (!_initialized || _statusItem is null)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "macOS status item tray service is not initialized.",
                PlatformErrorCodes.TrayNotInitialized,
                "tray.setVisible");
        }

        try
        {
            await RunOnUiThreadAsync(
                () => _interop.SetVisible(_statusItem, visible),
                cancellationToken);

            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                $"macOS status item tray visibility set to {visible}.",
                "tray.setVisible");
        }
        catch (Exception ex)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                $"macOS status item tray visibility update failed: {ex.Message}",
                PlatformErrorCodes.TrayMenuDispatchFailed,
                "tray.setVisible");
        }
    }

    public void Dispose()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            DisposeNative();
            _interop.Dispose();
            DisposeFallback();
            return;
        }

        Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                DisposeNative();
                _interop.Dispose();
                DisposeFallback();
            },
            DispatcherPriority.Send).GetTask().GetAwaiter().GetResult();
    }

    private IReadOnlyList<MacStatusMenuItem> BuildMenuItems()
    {
        return
        [
            CreateItem(TrayCommandId.Start, _menuText.Start, _menuState.StartEnabled),
            CreateItem(TrayCommandId.Stop, _menuText.Stop, _menuState.StopEnabled),
            CreateSeparator(),
            CreateItem(TrayCommandId.ForceShow, _menuText.ForceShow, _menuState.ForceShowEnabled),
            CreateItem(TrayCommandId.HideTray, _menuText.HideTray, _menuState.HideTrayEnabled),
            CreateItem(TrayCommandId.ToggleOverlay, _menuText.ToggleOverlay, _menuState.OverlayEnabled),
            CreateItem(TrayCommandId.Restart, _menuText.Restart, true),
            CreateSeparator(),
            CreateItem(TrayCommandId.Exit, _menuText.Exit, true),
        ];
    }

    private static MacStatusMenuItem CreateItem(TrayCommandId command, string text, bool enabled)
        => new(text, (int)command, IsSeparator: false, IsEnabled: enabled);

    private static MacStatusMenuItem CreateSeparator()
        => new(string.Empty, Tag: -1, IsSeparator: true, IsEnabled: false);

    private void HandleMenuActionTag(int tag)
    {
        if (!Enum.IsDefined(typeof(TrayCommandId), tag))
        {
            return;
        }

        RaiseCommand((TrayCommandId)tag);
    }

    private void RaiseCommand(TrayCommandId command)
    {
        try
        {
            CommandInvoked?.Invoke(this, new TrayCommandEvent(command, Capability.Provider, DateTimeOffset.UtcNow));
        }
        catch
        {
            // Keep native callbacks isolated from application subscribers.
        }
    }

    private async Task<PlatformOperationResult> SwitchToFallbackTrayAsync(
        string appTitle,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunOnUiThreadAsync(DisposeNative, cancellationToken);
        }
        catch
        {
            // Best-effort cleanup before switching providers.
        }

        _initialized = false;
        _fallbackMode = true;
        var fallbackResult = await _fallbackService.InitializeAsync(appTitle, _menuText, cancellationToken);
        if (fallbackResult.Success)
        {
            return PlatformOperation.FallbackSuccess(
                Capability.Provider,
                $"macOS status item initialization failed and switched to fallback menu: {exception.Message}",
                "tray.initialize",
                PlatformErrorCodes.TrayFallback);
        }

        return PlatformOperation.Failed(
            Capability.Provider,
            $"Failed to initialize macOS status item tray service: {exception.Message}",
            PlatformErrorCodes.TrayInitFailed,
            "tray.initialize");
    }

    private void DisposeNative()
    {
        if (_statusItem is not null)
        {
            _interop.RemoveStatusItem(_statusItem);
            _statusItem = null;
        }

        _initialized = false;
    }

    private void DisposeFallback()
    {
        if (_fallbackService is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static ITrayService CreateFallbackService()
        => AvaloniaTrayIconTrayService.TryCreate(out var avaloniaTray)
            ? avaloniaTray
            : new WindowMenuTrayService();

    private static string NormalizeTitle(string appTitle)
        => string.IsNullOrWhiteSpace(appTitle) ? "MAAUnified" : appTitle.Trim();

    private static Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Send, cancellationToken).GetTask();
    }
}

internal sealed partial class MacStatusItemNativeInterop : IMacStatusItemInterop
{
    private const string AppKitFramework = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const double NSVariableStatusItemLength = -1.0;
    private const double HiddenStatusItemLength = 0.0;
    private const string TargetClassName = "MAAUnifiedStatusItemTarget";
    private const string MenuActionSelectorName = "maaStatusItemMenuAction:";
    private static readonly object TargetGate = new();
    private static readonly Dictionary<nint, Action<int>> TargetActions = new();
    private static readonly MenuActionCallback MenuAction = HandleMenuAction;
    private static nint _targetClass;
    private bool _disposed;

    public bool IsRuntimeAvailable
    {
        get
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            nint objcHandle = nint.Zero;
            nint appKitHandle = nint.Zero;
            try
            {
                return NativeLibrary.TryLoad(ObjCLibrary, out objcHandle)
                    && NativeLibrary.TryLoad(AppKitFramework, out appKitHandle)
                    && NativeMethods.objc_getClass("NSStatusBar") != nint.Zero
                    && NativeMethods.objc_getClass("NSMenu") != nint.Zero
                    && NativeMethods.objc_getClass("NSMenuItem") != nint.Zero;
            }
            finally
            {
                ReleaseProbeHandle(objcHandle);
                ReleaseProbeHandle(appKitHandle);
            }
        }
    }

    public MacStatusItemHandle CreateStatusItem(
        string tooltip,
        IReadOnlyList<MacStatusMenuItem> menuItems,
        Action<int> menuAction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var statusBar = NativeMethods.IntPtr_objc_msgSend(
            NativeMethods.objc_getClass("NSStatusBar"),
            Selectors.SystemStatusBar);
        if (statusBar == nint.Zero)
        {
            throw new InvalidOperationException("NSStatusBar.systemStatusBar returned null.");
        }

        var statusItem = NativeMethods.IntPtr_objc_msgSend_double(
            statusBar,
            Selectors.StatusItemWithLength,
            NSVariableStatusItemLength);
        if (statusItem == nint.Zero)
        {
            throw new InvalidOperationException("NSStatusBar.statusItemWithLength returned null.");
        }

        var target = CreateTarget(menuAction);
        var handle = new MacStatusItemHandle
        {
            StatusItem = statusItem,
            Target = target,
        };

        UpdateTooltip(handle, tooltip);
        SetButtonTitle(handle, "MAA");
        UpdateMenu(handle, menuItems);
        return handle;
    }

    public void UpdateTooltip(MacStatusItemHandle handle, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (handle.StatusItem == nint.Zero)
        {
            return;
        }

        var button = NativeMethods.IntPtr_objc_msgSend(handle.StatusItem, Selectors.Button);
        if (button == nint.Zero)
        {
            return;
        }

        using var nativeTooltip = new NativeNSString(tooltip);
        NativeMethods.void_objc_msgSend_nint(button, Selectors.SetToolTip, nativeTooltip.Handle);
    }

    public void UpdateMenu(MacStatusItemHandle handle, IReadOnlyList<MacStatusMenuItem> menuItems)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (handle.StatusItem == nint.Zero || handle.Target == nint.Zero)
        {
            return;
        }

        var menu = NativeMethods.IntPtr_objc_msgSend(NativeMethods.objc_getClass("NSMenu"), Selectors.New);
        if (menu == nint.Zero)
        {
            throw new InvalidOperationException("NSMenu allocation failed.");
        }

        try
        {
            NativeMethods.void_objc_msgSend_bool(menu, Selectors.SetAutoenablesItems, false);
            foreach (var item in menuItems)
            {
                var nativeItem = item.IsSeparator
                    ? NativeMethods.IntPtr_objc_msgSend(NativeMethods.objc_getClass("NSMenuItem"), Selectors.SeparatorItem)
                    : CreateNativeMenuItem(handle.Target, item);
                NativeMethods.void_objc_msgSend_nint(menu, Selectors.AddItem, nativeItem);
                if (!item.IsSeparator)
                {
                    NativeMethods.void_objc_msgSend(nativeItem, Selectors.Release);
                }
            }

            NativeMethods.void_objc_msgSend_nint(handle.StatusItem, Selectors.SetMenu, menu);
        }
        finally
        {
            NativeMethods.void_objc_msgSend(menu, Selectors.Release);
        }
    }

    public void SetVisible(MacStatusItemHandle handle, bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (handle.StatusItem == nint.Zero)
        {
            return;
        }

        if (NativeMethods.bool_objc_msgSend_nint(handle.StatusItem, Selectors.RespondsToSelector, Selectors.SetVisible))
        {
            NativeMethods.void_objc_msgSend_bool(handle.StatusItem, Selectors.SetVisible, visible);
            return;
        }

        NativeMethods.void_objc_msgSend_double(
            handle.StatusItem,
            Selectors.SetLength,
            visible ? NSVariableStatusItemLength : HiddenStatusItemLength);
    }

    public void RemoveStatusItem(MacStatusItemHandle handle)
    {
        if (handle.StatusItem != nint.Zero)
        {
            var statusBar = NativeMethods.IntPtr_objc_msgSend(
                NativeMethods.objc_getClass("NSStatusBar"),
                Selectors.SystemStatusBar);
            if (statusBar != nint.Zero)
            {
                NativeMethods.void_objc_msgSend_nint(statusBar, Selectors.RemoveStatusItem, handle.StatusItem);
            }

            handle.StatusItem = nint.Zero;
        }

        ReleaseTarget(handle);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static void ReleaseProbeHandle(nint handle)
    {
        if (handle != nint.Zero)
        {
            NativeLibrary.Free(handle);
        }
    }

    private static nint CreateNativeMenuItem(nint target, MacStatusMenuItem item)
    {
        using var title = new NativeNSString(item.Title);
        using var keyEquivalent = new NativeNSString(string.Empty);
        var menuItem = NativeMethods.IntPtr_objc_msgSend(NativeMethods.objc_getClass("NSMenuItem"), Selectors.Alloc);
        menuItem = NativeMethods.IntPtr_objc_msgSend_nint_nint_nint(
            menuItem,
            Selectors.InitWithTitleActionKeyEquivalent,
            title.Handle,
            Selectors.MenuAction,
            keyEquivalent.Handle);

        NativeMethods.void_objc_msgSend_nint(menuItem, Selectors.SetTarget, target);
        NativeMethods.void_objc_msgSend_long(menuItem, Selectors.SetTag, item.Tag);
        NativeMethods.void_objc_msgSend_bool(menuItem, Selectors.SetEnabled, item.IsEnabled);
        return menuItem;
    }

    private static void SetButtonTitle(MacStatusItemHandle handle, string title)
    {
        var button = NativeMethods.IntPtr_objc_msgSend(handle.StatusItem, Selectors.Button);
        if (button == nint.Zero)
        {
            return;
        }

        using var nativeTitle = new NativeNSString(title);
        NativeMethods.void_objc_msgSend_nint(button, Selectors.SetTitle, nativeTitle.Handle);
    }

    private static nint CreateTarget(Action<int> menuAction)
    {
        EnsureTargetClass();
        var target = NativeMethods.IntPtr_objc_msgSend(_targetClass, Selectors.Alloc);
        target = NativeMethods.IntPtr_objc_msgSend(target, Selectors.Init);
        if (target == nint.Zero)
        {
            throw new InvalidOperationException("Status item target allocation failed.");
        }

        lock (TargetGate)
        {
            TargetActions[target] = menuAction;
        }

        return target;
    }

    private static void ReleaseTarget(MacStatusItemHandle handle)
    {
        if (handle.Target == nint.Zero)
        {
            return;
        }

        lock (TargetGate)
        {
            TargetActions.Remove(handle.Target);
        }

        NativeMethods.void_objc_msgSend(handle.Target, Selectors.Release);
        handle.Target = nint.Zero;
    }

    private static void EnsureTargetClass()
    {
        if (_targetClass != nint.Zero)
        {
            return;
        }

        lock (TargetGate)
        {
            if (_targetClass != nint.Zero)
            {
                return;
            }

            var existing = NativeMethods.objc_getClass(TargetClassName);
            if (existing != nint.Zero)
            {
                _targetClass = existing;
                return;
            }

            var nsObject = NativeMethods.objc_getClass("NSObject");
            var targetClass = NativeMethods.objc_allocateClassPair(nsObject, TargetClassName, nint.Zero);
            if (targetClass == nint.Zero)
            {
                throw new InvalidOperationException("Unable to allocate Objective-C status item target class.");
            }

            var methodImp = Marshal.GetFunctionPointerForDelegate(MenuAction);
            if (!NativeMethods.class_addMethod(
                    targetClass,
                    Selectors.MenuAction,
                    methodImp,
                    "v@:@"))
            {
                throw new InvalidOperationException("Unable to add Objective-C status item action method.");
            }

            NativeMethods.objc_registerClassPair(targetClass);
            _targetClass = targetClass;
        }
    }

    private static void HandleMenuAction(nint self, nint selector, nint sender)
    {
        try
        {
            Action<int>? action;
            lock (TargetGate)
            {
                TargetActions.TryGetValue(self, out action);
            }

            if (action is null)
            {
                return;
            }

            var tag = (int)NativeMethods.long_objc_msgSend(sender, Selectors.Tag);
            action(tag);
        }
        catch
        {
            // Keep AppKit action dispatch alive.
        }
    }

    private sealed class NativeNSString : IDisposable
    {
        public NativeNSString(string value)
        {
            var nsString = NativeMethods.IntPtr_objc_msgSend(NativeMethods.objc_getClass("NSString"), Selectors.Alloc);
            Handle = NativeMethods.IntPtr_objc_msgSend_string(
                nsString,
                Selectors.InitWithUtf8String,
                value);
        }

        public nint Handle { get; }

        public void Dispose()
        {
            if (Handle != nint.Zero)
            {
                NativeMethods.void_objc_msgSend(Handle, Selectors.Release);
            }
        }
    }

    private delegate void MenuActionCallback(nint self, nint selector, nint sender);

    private static class Selectors
    {
        public static readonly nint AddItem = NativeMethods.sel_registerName("addItem:");
        public static readonly nint Alloc = NativeMethods.sel_registerName("alloc");
        public static readonly nint Button = NativeMethods.sel_registerName("button");
        public static readonly nint Init = NativeMethods.sel_registerName("init");
        public static readonly nint InitWithTitleActionKeyEquivalent = NativeMethods.sel_registerName("initWithTitle:action:keyEquivalent:");
        public static readonly nint InitWithUtf8String = NativeMethods.sel_registerName("initWithUTF8String:");
        public static readonly nint MenuAction = NativeMethods.sel_registerName(MenuActionSelectorName);
        public static readonly nint New = NativeMethods.sel_registerName("new");
        public static readonly nint Release = NativeMethods.sel_registerName("release");
        public static readonly nint RemoveStatusItem = NativeMethods.sel_registerName("removeStatusItem:");
        public static readonly nint RespondsToSelector = NativeMethods.sel_registerName("respondsToSelector:");
        public static readonly nint SeparatorItem = NativeMethods.sel_registerName("separatorItem");
        public static readonly nint SetAutoenablesItems = NativeMethods.sel_registerName("setAutoenablesItems:");
        public static readonly nint SetEnabled = NativeMethods.sel_registerName("setEnabled:");
        public static readonly nint SetLength = NativeMethods.sel_registerName("setLength:");
        public static readonly nint SetMenu = NativeMethods.sel_registerName("setMenu:");
        public static readonly nint SetTarget = NativeMethods.sel_registerName("setTarget:");
        public static readonly nint SetTag = NativeMethods.sel_registerName("setTag:");
        public static readonly nint SetTitle = NativeMethods.sel_registerName("setTitle:");
        public static readonly nint SetToolTip = NativeMethods.sel_registerName("setToolTip:");
        public static readonly nint SetVisible = NativeMethods.sel_registerName("setVisible:");
        public static readonly nint StatusItemWithLength = NativeMethods.sel_registerName("statusItemWithLength:");
        public static readonly nint SystemStatusBar = NativeMethods.sel_registerName("systemStatusBar");
        public static readonly nint Tag = NativeMethods.sel_registerName("tag");
    }

    private static partial class NativeMethods
    {
        [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint objc_getClass(string name);

        [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint sel_registerName(string name);

        [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool class_addMethod(nint cls, nint name, nint imp, string types);

        [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

        [LibraryImport(ObjCLibrary)]
        public static partial void objc_registerClassPair(nint cls);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint IntPtr_objc_msgSend(nint receiver, nint selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint IntPtr_objc_msgSend_double(nint receiver, nint selector, double value);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint IntPtr_objc_msgSend_nint_nint_nint(
            nint receiver,
            nint selector,
            nint first,
            nint second,
            nint third);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint IntPtr_objc_msgSend_string(
            nint receiver,
            nint selector,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void void_objc_msgSend(nint receiver, nint selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void void_objc_msgSend_nint(nint receiver, nint selector, nint value);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void void_objc_msgSend_bool(
            nint receiver,
            nint selector,
            [MarshalAs(UnmanagedType.I1)] bool value);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void void_objc_msgSend_double(nint receiver, nint selector, double value);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void void_objc_msgSend_long(nint receiver, nint selector, long value);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern long long_objc_msgSend(nint receiver, nint selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool bool_objc_msgSend_nint(nint receiver, nint selector, nint value);
    }
}
