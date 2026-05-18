using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using SharpHook;
using SharpHook.Data;

namespace MAAUnified.Platform;

internal static class PlatformNativeDependencyProbe
{
    public static bool HasAssembly(string assemblyName)
    {
        try
        {
            _ = Assembly.Load(assemblyName);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class HotkeyGestureNormalizer
{
    public static bool TryNormalize(string gesture, out string normalized)
    {
        return HotkeyGestureCodec.TryNormalize(gesture, out normalized);
    }
}

internal interface IGlobalKeyboardHook : IDisposable
{
    event EventHandler<KeyboardHookEventArgs>? KeyPressed;

    Task RunAsync();

    void Stop();
}

internal sealed class SharpHookEventLoopKeyboardHook : IGlobalKeyboardHook
{
    private readonly EventLoopGlobalHook _inner = new(GlobalHookType.Keyboard);

    public event EventHandler<KeyboardHookEventArgs>? KeyPressed
    {
        add => _inner.KeyPressed += value;
        remove => _inner.KeyPressed -= value;
    }

    public Task RunAsync() => _inner.RunAsync();

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();
}

public sealed class WindowsNotifyIconTrayService : ITrayService, IDisposable
{
    private const nint DefaultAppIconId = 32512; // IDI_APPLICATION
    private readonly CommandNotificationService _notificationFallback = new();
    private readonly WindowMenuTrayService _fallbackService = new();
    private nint _iconHandle;
    private bool _ownsIconHandle;
    private bool _visible = true;
    private bool _initialized;
    private bool _fallbackMode;
    private TrayMenuState _menuState = new(true, true, true, true, true);
    private TrayMenuText _menuText = TrayMenuText.Default;
    private H.NotifyIcon.Core.TrayIcon? _trayIcon;

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "System tray integration is available via native notify icon backend.",
        Provider: "h-notifyicon",
        HasFallback: true,
        FallbackMode: "window-menu");

    public event EventHandler<TrayCommandEvent>? CommandInvoked;

    public event EventHandler<TrayMenuRequestEvent>? MenuRequested;

    public WindowsNotifyIconTrayService()
    {
        _fallbackService.CommandInvoked += (_, e) => RaiseCommand(e.Command);
    }

    public static bool TryCreate([NotNullWhen(true)] out WindowsNotifyIconTrayService? service)
    {
        service = null;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!PlatformNativeDependencyProbe.HasAssembly("H.NotifyIcon"))
        {
            return false;
        }

        service = new WindowsNotifyIconTrayService();
        return true;
    }

    public async Task<PlatformOperationResult> InitializeAsync(
        string appTitle,
        TrayMenuText? menuText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _menuText = menuText ?? TrayMenuText.Default;

        if (_fallbackMode)
        {
            return await _fallbackService.InitializeAsync(appTitle, _menuText, cancellationToken);
        }

        if (_initialized)
        {
            try
            {
                _trayIcon?.UpdateToolTip(string.IsNullOrWhiteSpace(appTitle) ? "MAAUnified" : appTitle.Trim());
                return PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    "Tray service already initialized.",
                    "tray.initialize");
            }
            catch (Exception ex)
            {
                return await SwitchToFallbackTrayAsync(appTitle, ex, cancellationToken);
            }
        }

        try
        {
            _trayIcon = new H.NotifyIcon.Core.TrayIcon("MAAUnified");
            _trayIcon.UpdateToolTip(string.IsNullOrWhiteSpace(appTitle) ? "MAAUnified" : appTitle.Trim());
            _iconHandle = LoadTrayIconHandle(out _ownsIconHandle);
            _trayIcon.UpdateIcon(_iconHandle);
            _trayIcon.UpdateVisibility(_visible ? IconVisibility.Visible : IconVisibility.Hidden);
            _trayIcon.Create();
            _trayIcon.MessageWindow.MouseEventReceived += OnTrayMouseEventReceived;
            _initialized = true;
            _fallbackMode = false;
            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                "Tray service initialized.",
                "tray.initialize");
        }
        catch (Exception ex)
        {
            return await SwitchToFallbackTrayAsync(appTitle, ex, cancellationToken);
        }
    }

    public Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return _fallbackService.ShutdownAsync(cancellationToken);
        }

        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.MessageWindow.MouseEventReceived -= OnTrayMouseEventReceived;
            }

            _trayIcon?.TryRemove();
            _trayIcon?.Dispose();
            _trayIcon = null;
            ReleaseIconHandle();
            _initialized = false;
            _fallbackMode = false;
            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                "Tray service shutdown completed.",
                "tray.shutdown"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Tray shutdown failed: {ex.Message}",
                PlatformErrorCodes.TrayInitFailed,
                "tray.shutdown"));
        }
    }

    public void Dispose()
    {
        try
        {
            _ = ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Tray cleanup is best-effort.
        }
    }

    public async Task<PlatformOperationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return await _fallbackService.ShowAsync(title, message, cancellationToken);
        }

        if (_trayIcon is not null && _initialized)
        {
            try
            {
                _trayIcon.ShowNotification(
                    title,
                    message,
                    NotificationIcon.None,
                    customIconHandle: null,
                    largeIcon: false,
                    sound: true,
                    respectQuietTime: true,
                    realtime: false,
                    timeout: TimeSpan.FromSeconds(8));

                return PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    "Tray notification dispatched.",
                    "tray.show");
            }
            catch
            {
                // fall through to notification fallback
            }
        }

        var fallback = await _notificationFallback.NotifyAsync(title, message, cancellationToken);
        if (!fallback.Success)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "Tray notification failed and fallback could not be delivered.",
                PlatformErrorCodes.TrayFallback,
                "tray.show",
                usedFallback: true);
        }

        return PlatformOperation.FallbackSuccess(
            Capability.Provider,
            "Tray notification fallback delivered via notification service.",
            "tray.show",
            PlatformErrorCodes.TrayFallback);
    }

    public Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return _fallbackService.SetMenuStateAsync(state, cancellationToken);
        }

        _menuState = state;
        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            "Tray menu state updated.",
            "tray.setMenuState"));
    }

    public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return _fallbackService.SetVisibleAsync(visible, cancellationToken);
        }

        _visible = visible;
        if (_trayIcon is null || !_initialized)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Tray service is not initialized.",
                PlatformErrorCodes.TrayNotInitialized,
                "tray.setVisible"));
        }

        try
        {
            _trayIcon.UpdateVisibility(visible ? IconVisibility.Visible : IconVisibility.Hidden);
            if (visible)
            {
                _trayIcon.Show();
            }
            else
            {
                _trayIcon.Hide();
            }

            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                $"Tray visibility set to {visible}.",
                "tray.setVisible"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Tray visibility update failed: {ex.Message}",
                PlatformErrorCodes.TrayMenuDispatchFailed,
                "tray.setVisible"));
        }
    }

    private void RaiseCommand(TrayCommandId command)
    {
        try
        {
            CommandInvoked?.Invoke(this, new TrayCommandEvent(command, Capability.Provider, DateTimeOffset.UtcNow));
        }
        catch
        {
            // Ignore user event exceptions to avoid destabilizing tray message loop.
        }
    }

    private void OnTrayMouseEventReceived(object? sender, MessageWindow.MouseEventReceivedEventArgs e)
    {
        if (!_initialized || _fallbackMode || _trayIcon is null || e.MouseEvent != MouseEvent.IconRightMouseUp)
        {
            return;
        }

        try
        {
            MenuRequested?.Invoke(
                this,
                new TrayMenuRequestEvent(
                    e.Point.X,
                    e.Point.Y,
                    Capability.Provider,
                    DateTimeOffset.UtcNow));
        }
        catch
        {
            // Ignore user event exceptions to avoid destabilizing tray message loop.
        }
    }

    private async Task<PlatformOperationResult> SwitchToFallbackTrayAsync(
        string appTitle,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            _trayIcon?.Dispose();
        }
        catch
        {
            // Best-effort cleanup before switching providers.
        }

        _trayIcon = null;
        ReleaseIconHandle();
        _initialized = false;
        _fallbackMode = true;
        var fallbackResult = await _fallbackService.InitializeAsync(appTitle, _menuText, cancellationToken);
        if (fallbackResult.Success)
        {
            return PlatformOperation.FallbackSuccess(
                Capability.Provider,
                $"Native tray initialization failed and switched to fallback menu: {exception.Message}",
                "tray.initialize",
                PlatformErrorCodes.TrayFallback);
        }

        return PlatformOperation.Failed(
            Capability.Provider,
            $"Failed to initialize tray service: {exception.Message}",
            PlatformErrorCodes.TrayInitFailed,
            "tray.initialize");
    }

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, nint[]? phiconLarge, nint[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    private static nint LoadTrayIconHandle(out bool ownsHandle)
    {
        ownsHandle = false;
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var largeIcons = new nint[1];
            try
            {
                var extracted = ExtractIconEx(processPath, 0, largeIcons, null, 1);
                if (extracted > 0 && largeIcons[0] != nint.Zero)
                {
                    ownsHandle = true;
                    return largeIcons[0];
                }
            }
            catch
            {
                // Fall back to the default application icon when icon extraction fails.
            }
        }

        return LoadIcon(nint.Zero, DefaultAppIconId);
    }

    private void ReleaseIconHandle()
    {
        if (_ownsIconHandle && _iconHandle != nint.Zero)
        {
            _ = DestroyIcon(_iconHandle);
        }

        _iconHandle = nint.Zero;
        _ownsIconHandle = false;
    }
}

public sealed class AvaloniaTrayIconTrayService : ITrayService, IDisposable
{
    // 1x1 transparent PNG used when no app icon is available.
    private const string DefaultPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7+N3EAAAAASUVORK5CYII=";
    private static readonly Uri AppIconUri = new("avares://MAAUnified/Assets/Brand/newlogo.ico");

    private readonly CommandNotificationService _notificationFallback = new();
    private readonly WindowMenuTrayService _fallbackService = new();
    private readonly Dictionary<TrayCommandId, NativeMenuItem> _menuItems = new();
    private bool _visible = true;
    private bool _initialized;
    private bool _fallbackMode;
    private TrayMenuState _menuState = new(true, true, true, true, true);
    private TrayMenuText _menuText = TrayMenuText.Default;
    private Avalonia.Controls.TrayIcon? _trayIcon;
    private Avalonia.Controls.TrayIcons? _trayIcons;

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "System tray integration is available via Avalonia tray icon backend.",
        Provider: "avalonia-trayicon",
        HasFallback: true,
        FallbackMode: "window-menu");

    public event EventHandler<TrayCommandEvent>? CommandInvoked;

    public event EventHandler<TrayMenuRequestEvent>? MenuRequested;

    public AvaloniaTrayIconTrayService()
    {
        _fallbackService.CommandInvoked += (_, e) => RaiseCommand(e.Command);
    }

    public static bool TryCreate([NotNullWhen(true)] out AvaloniaTrayIconTrayService? service)
    {
        service = null;
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return false;
        }

        if (!PlatformNativeDependencyProbe.HasAssembly("Avalonia.Controls"))
        {
            return false;
        }

        service = new AvaloniaTrayIconTrayService();
        return true;
    }

    public async Task<PlatformOperationResult> InitializeAsync(
        string appTitle,
        TrayMenuText? menuText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _menuText = menuText ?? TrayMenuText.Default;

        if (_fallbackMode)
        {
            return await _fallbackService.InitializeAsync(appTitle, _menuText, cancellationToken);
        }

        if (_initialized && _trayIcon is not null)
        {
            try
            {
                _trayIcon.ToolTipText = string.IsNullOrWhiteSpace(appTitle) ? "MAAUnified" : appTitle.Trim();
                RebuildMenu();
                ApplyMenuState();
                _trayIcon.IsVisible = _visible;
                return PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    "Tray service already initialized.",
                    "tray.initialize");
            }
            catch (Exception ex)
            {
                return await SwitchToFallbackTrayAsync(appTitle, ex, cancellationToken);
            }
        }

        try
        {
            if (Application.Current is null)
            {
                throw new InvalidOperationException("Avalonia application is not initialized.");
            }

            _trayIcon = new Avalonia.Controls.TrayIcon
            {
                ToolTipText = string.IsNullOrWhiteSpace(appTitle) ? "MAAUnified" : appTitle.Trim(),
                IsVisible = _visible,
                Icon = BuildDefaultIcon(),
            };
            RebuildMenu();
            _trayIcons = new Avalonia.Controls.TrayIcons
            {
                _trayIcon,
            };
            Application.Current.SetValue(Avalonia.Controls.TrayIcon.IconsProperty, _trayIcons);

            _initialized = true;
            _fallbackMode = false;
            ApplyMenuState();
            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                "Tray service initialized.",
                "tray.initialize");
        }
        catch (Exception ex)
        {
            return await SwitchToFallbackTrayAsync(appTitle, ex, cancellationToken);
        }
    }

    public Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return _fallbackService.ShutdownAsync(cancellationToken);
        }

        try
        {
            DisposeNative();
            _initialized = false;
            _fallbackMode = false;
            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                "Tray service shutdown completed.",
                "tray.shutdown"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Tray shutdown failed: {ex.Message}",
                PlatformErrorCodes.TrayInitFailed,
                "tray.shutdown"));
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
                "Tray notification fallback could not be delivered.",
                PlatformErrorCodes.TrayFallback,
                "tray.show",
                usedFallback: true);
        }

        return PlatformOperation.FallbackSuccess(
            Capability.Provider,
            "Tray notification fallback delivered via notification service.",
            "tray.show",
            PlatformErrorCodes.TrayFallback);
    }

    public Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return _fallbackService.SetMenuStateAsync(state, cancellationToken);
        }

        _menuState = state;
        if (_initialized)
        {
            try
            {
                ApplyMenuState();
            }
            catch (Exception ex)
            {
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    $"Tray menu state update failed: {ex.Message}",
                    PlatformErrorCodes.TrayMenuDispatchFailed,
                    "tray.setMenuState"));
            }
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            "Tray menu state updated.",
            "tray.setMenuState"));
    }

    public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fallbackMode)
        {
            return _fallbackService.SetVisibleAsync(visible, cancellationToken);
        }

        _visible = visible;
        if (_trayIcon is null || !_initialized)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Tray service is not initialized.",
                PlatformErrorCodes.TrayNotInitialized,
                "tray.setVisible"));
        }

        try
        {
            _trayIcon.IsVisible = visible;
            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                $"Tray visibility set to {visible}.",
                "tray.setVisible"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Tray visibility update failed: {ex.Message}",
                PlatformErrorCodes.TrayMenuDispatchFailed,
                "tray.setVisible"));
        }
    }

    public void Dispose()
    {
        DisposeNative();
    }

    private void RebuildMenu()
    {
        _menuItems.Clear();
        if (_trayIcon is null)
        {
            return;
        }

        var menu = new NativeMenu();
        menu.Items.Add(CreateMenuItem(TrayCommandId.Start, _menuText.Start));
        menu.Items.Add(CreateMenuItem(TrayCommandId.Stop, _menuText.Stop));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateMenuItem(TrayCommandId.ForceShow, _menuText.ForceShow));
        menu.Items.Add(CreateMenuItem(TrayCommandId.HideTray, _menuText.HideTray));
        menu.Items.Add(CreateMenuItem(TrayCommandId.ToggleOverlay, _menuText.ToggleOverlay));
        menu.Items.Add(CreateMenuItem(TrayCommandId.SwitchLanguage, _menuText.SwitchLanguage));
        menu.Items.Add(CreateMenuItem(TrayCommandId.Restart, _menuText.Restart));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateMenuItem(TrayCommandId.Exit, _menuText.Exit));
        _trayIcon.Menu = menu;
    }

    private NativeMenuItem CreateMenuItem(TrayCommandId command, string text)
    {
        var item = new NativeMenuItem(text);
        item.Click += (_, _) => RaiseCommand(command);
        _menuItems[command] = item;
        return item;
    }

    private void ApplyMenuState()
    {
        SetEnabled(TrayCommandId.Start, _menuState.StartEnabled);
        SetEnabled(TrayCommandId.Stop, _menuState.StopEnabled);
        SetEnabled(TrayCommandId.ToggleOverlay, _menuState.OverlayEnabled);
        SetEnabled(TrayCommandId.ForceShow, _menuState.ForceShowEnabled);
        SetEnabled(TrayCommandId.HideTray, _menuState.HideTrayEnabled);
        SetEnabled(TrayCommandId.SwitchLanguage, true);
        SetEnabled(TrayCommandId.Restart, true);
        SetEnabled(TrayCommandId.Exit, true);
    }

    private void SetEnabled(TrayCommandId command, bool enabled)
    {
        if (_menuItems.TryGetValue(command, out var item))
        {
            item.IsEnabled = enabled;
        }
    }

    private void RaiseCommand(TrayCommandId command)
    {
        try
        {
            CommandInvoked?.Invoke(this, new TrayCommandEvent(command, Capability.Provider, DateTimeOffset.UtcNow));
        }
        catch
        {
            // Ignore user event exceptions to avoid destabilizing tray message loop.
        }
    }

    private async Task<PlatformOperationResult> SwitchToFallbackTrayAsync(
        string appTitle,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            DisposeNative();
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
                $"Native tray initialization failed and switched to fallback menu: {exception.Message}",
                "tray.initialize",
                PlatformErrorCodes.TrayFallback);
        }

        return PlatformOperation.Failed(
            Capability.Provider,
            $"Failed to initialize tray service: {exception.Message}",
            PlatformErrorCodes.TrayInitFailed,
            "tray.initialize");
    }

    private static WindowIcon BuildDefaultIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(AppIconUri);
            return new WindowIcon(stream);
        }
        catch
        {
            var stream = new MemoryStream(Convert.FromBase64String(DefaultPngBase64));
            return new WindowIcon(stream);
        }
    }

    private void DisposeNative()
    {
        try
        {
            if (Application.Current is not null)
            {
                Application.Current.SetValue(Avalonia.Controls.TrayIcon.IconsProperty, null);
            }
        }
        catch
        {
            // Tray cleanup is best-effort.
        }

        try
        {
            _trayIcon?.Dispose();
        }
        catch
        {
            // Tray cleanup is best-effort.
        }
        _trayIcon = null;
        _trayIcons?.Clear();
        _trayIcons = null;
    }
}

public sealed class DesktopNotificationService : INotificationService, IDisposable
{
    private readonly CommandNotificationService _fallback = new();
    private readonly object _nativeAdapterGate = new();
    private readonly string _appName;
    private DesktopNotificationAdapter? _nativeAdapter;
    private bool _nativeAdapterInitialized;

    public DesktopNotificationService()
    {
        _appName = "MaaAssistantArknights";
    }

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "System notification uses DesktopNotifications backend when available.",
        Provider: "desktop-notifications",
        HasFallback: true,
        FallbackMode: "command-or-in-app");

    public static bool TryCreate([NotNullWhen(true)] out DesktopNotificationService? service)
    {
        service = null;
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return false;
        }

        if (!PlatformNativeDependencyProbe.HasAssembly("DesktopNotifications"))
        {
            return false;
        }

        service = new DesktopNotificationService();
        return true;
    }

    public async Task<PlatformOperationResult> NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var nativeAdapter = EnsureNativeAdapter();
            if (nativeAdapter is null)
            {
                return await NotifyFallbackAsync(title, message, "Native notification backend is unavailable.", cancellationToken);
            }

            await nativeAdapter.NotifyAsync(title, message, cancellationToken);
            return PlatformOperation.NativeSuccess(Capability.Provider, "System notification dispatched.", "notification.notify");
        }
        catch (Exception ex)
        {
            return await NotifyFallbackAsync(
                title,
                message,
                $"Native notification dispatch failed: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task<PlatformOperationResult> NotifyFallbackAsync(
        string title,
        string message,
        string reason,
        CancellationToken cancellationToken)
    {
        var fallbackResult = await _fallback.NotifyAsync(title, message, cancellationToken);
        if (!fallbackResult.Success)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                $"{reason} Fallback notification also failed.",
                PlatformErrorCodes.NotificationSendFailed,
                "notification.notify",
                usedFallback: true);
        }

        return PlatformOperation.FallbackSuccess(
            Capability.Provider,
            $"{reason} Switched to fallback notification provider.",
            "notification.notify",
            PlatformErrorCodes.NotificationFallback);
    }

    public void Dispose()
    {
        DesktopNotificationAdapter? nativeAdapter;
        lock (_nativeAdapterGate)
        {
            nativeAdapter = _nativeAdapter;
            _nativeAdapter = null;
            _nativeAdapterInitialized = true;
        }

        nativeAdapter?.Dispose();
    }

    private DesktopNotificationAdapter? EnsureNativeAdapter()
    {
        if (_nativeAdapterInitialized)
        {
            return _nativeAdapter;
        }

        lock (_nativeAdapterGate)
        {
            if (!_nativeAdapterInitialized)
            {
                _nativeAdapter = DesktopNotificationAdapter.TryCreate(_appName);
                _nativeAdapterInitialized = true;
            }

            return _nativeAdapter;
        }
    }

    private sealed class DesktopNotificationAdapter : IDisposable
    {
        private readonly object _manager;
        private readonly MethodInfo _showMethod;
        private readonly Type _notificationType;
        private readonly ConstructorInfo? _notificationCtor;

        private DesktopNotificationAdapter(
            object manager,
            MethodInfo showMethod,
            Type notificationType,
            ConstructorInfo? notificationCtor)
        {
            _manager = manager;
            _showMethod = showMethod;
            _notificationType = notificationType;
            _notificationCtor = notificationCtor;
        }

        public static DesktopNotificationAdapter? TryCreate(string appName)
        {
            try
            {
                var assembly = Assembly.Load("DesktopNotifications");
                var managerType = assembly.GetType("DesktopNotifications.NotificationManager");
                var notificationType = assembly.GetType("DesktopNotifications.Notification");
                if (managerType is null || notificationType is null)
                {
                    return null;
                }

                var manager = CreateManagerInstance(managerType, assembly, appName);
                if (manager is null)
                {
                    return null;
                }

                var showMethod = managerType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m =>
                    {
                        if (!string.Equals(m.Name, "ShowNotification", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var parameters = m.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(notificationType);
                    });

                if (showMethod is null)
                {
                    return null;
                }

                var ctor = notificationType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                    .OrderBy(c => c.GetParameters().Length)
                    .FirstOrDefault();

                return new DesktopNotificationAdapter(manager, showMethod, notificationType, ctor);
            }
            catch
            {
                return null;
            }
        }

        public async Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
        {
            var notification = CreateNotification(title, message);
            var result = _showMethod.Invoke(_manager, new[] { notification });
            if (result is Task task)
            {
                await task.WaitAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            if (_manager is IDisposable disposable)
            {
                disposable.Dispose();
                return;
            }

            var disposeMethod = _manager.GetType().GetMethod(
                "Dispose",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            disposeMethod?.Invoke(_manager, null);
        }

        private object CreateNotification(string title, string message)
        {
            object notification;
            if (_notificationCtor is not null)
            {
                var args = _notificationCtor
                    .GetParameters()
                    .Select(p => BuildConstructorArgument(p, title, message))
                    .ToArray();
                notification = _notificationCtor.Invoke(args);
            }
            else
            {
                notification = Activator.CreateInstance(_notificationType)
                    ?? throw new InvalidOperationException("Cannot create notification object.");
            }

            SetProperty(notification, "Title", title);
            SetProperty(notification, "Body", message);
            return notification;
        }

        private static object? CreateManagerInstance(Type managerType, Assembly assembly, string appName)
        {
            foreach (var ctor in managerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                         .OrderBy(c => c.GetParameters().Length))
            {
                try
                {
                    var args = ctor.GetParameters()
                        .Select(p => BuildManagerArgument(assembly, p, appName))
                        .ToArray();
                    return ctor.Invoke(args);
                }
                catch
                {
                    // try next constructor
                }
            }

            return null;
        }

        private static object? BuildManagerArgument(Assembly assembly, ParameterInfo parameter, string appName)
        {
            if (parameter.ParameterType == typeof(string))
            {
                return appName;
            }

            if (parameter.ParameterType == typeof(bool))
            {
                return false;
            }

            if (parameter.ParameterType == typeof(int))
            {
                return 0;
            }

            if (parameter.ParameterType == typeof(DateTimeOffset))
            {
                return DateTimeOffset.UtcNow;
            }

            var fullName = parameter.ParameterType.FullName ?? string.Empty;
            if (string.Equals(fullName, "DesktopNotifications.Application", StringComparison.Ordinal))
            {
                return CreateByName(assembly, fullName, appName);
            }

            if (string.Equals(fullName, "DesktopNotifications.ApplicationContext", StringComparison.Ordinal))
            {
                var app = CreateByName(assembly, "DesktopNotifications.Application", appName);
                return CreateByName(assembly, fullName, app);
            }

            return parameter.HasDefaultValue
                ? parameter.DefaultValue
                : parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
        }

        private static object? BuildConstructorArgument(ParameterInfo parameter, string title, string message)
        {
            if (parameter.ParameterType == typeof(string))
            {
                if (parameter.Name?.Contains("title", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return title;
                }

                return message;
            }

            if (parameter.ParameterType == typeof(DateTimeOffset))
            {
                return DateTimeOffset.UtcNow;
            }

            if (parameter.ParameterType == typeof(bool))
            {
                return false;
            }

            if (parameter.ParameterType == typeof(int))
            {
                return 0;
            }

            if (parameter.ParameterType == typeof(byte))
            {
                return (byte)0;
            }

            return parameter.HasDefaultValue
                ? parameter.DefaultValue
                : parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
        }

        private static object? CreateByName(Assembly assembly, string typeName, params object?[] args)
        {
            var type = assembly.GetType(typeName);
            if (type is null)
            {
                return null;
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                         .OrderBy(c => c.GetParameters().Length))
            {
                try
                {
                    var ctorParams = ctor.GetParameters();
                    if (ctorParams.Length == 0)
                    {
                        return ctor.Invoke(null);
                    }

                    var values = new object?[ctorParams.Length];
                    for (var i = 0; i < ctorParams.Length; i++)
                    {
                        values[i] = i < args.Length
                            ? args[i]
                            : ctorParams[i].HasDefaultValue
                                ? ctorParams[i].DefaultValue
                                : ctorParams[i].ParameterType.IsValueType
                                    ? Activator.CreateInstance(ctorParams[i].ParameterType)
                                    : null;
                    }

                    return ctor.Invoke(values);
                }
                catch
                {
                    // try next constructor
                }
            }

            return null;
        }

        private static void SetProperty(object target, string propertyName, string value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.CanWrite == true && property.PropertyType == typeof(string))
            {
                property.SetValue(target, value);
            }
        }
    }
}

public sealed class SharpHookGlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private static readonly TimeSpan DefaultStartupProbeWindow = TimeSpan.FromMilliseconds(250);
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, RegisteredHotkey> _registeredByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _registeredByChord = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IGlobalKeyboardHook> _hookFactory;
    private readonly TimeSpan _startupProbeWindow;
    private IGlobalKeyboardHook? _hook;
    private Task? _hookTask;

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "Global hotkeys are available via SharpHook backend.",
        Provider: "sharp-hook",
        HasFallback: true,
        FallbackMode: "window-scoped");

    public event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

    internal SharpHookGlobalHotkeyService(
        Func<IGlobalKeyboardHook>? hookFactory = null,
        TimeSpan? startupProbeWindow = null)
    {
        _hookFactory = hookFactory ?? (() => new SharpHookEventLoopKeyboardHook());
        _startupProbeWindow = startupProbeWindow ?? DefaultStartupProbeWindow;
    }

    public static bool TryCreate([NotNullWhen(true)] out SharpHookGlobalHotkeyService? service)
    {
        service = null;
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return false;
        }

        if (!PlatformNativeDependencyProbe.HasAssembly("SharpHook"))
        {
            return false;
        }

        service = new SharpHookGlobalHotkeyService();
        return true;
    }

    public async Task<PlatformOperationResult> RegisterAsync(string name, string gesture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "Hotkey name cannot be empty.",
                PlatformErrorCodes.HotkeyNameMissing,
                "hotkey.register");
        }

        if (!TryParseGesture(gesture, out var binding))
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "Invalid hotkey gesture format.",
                PlatformErrorCodes.HotkeyInvalidGesture,
                "hotkey.register");
        }

        lock (_syncRoot)
        {
            if (_registeredByName.TryGetValue(name, out var previous))
            {
                _registeredByChord.Remove(previous.ChordKey);
            }

            if (_registeredByChord.TryGetValue(binding.ChordKey, out var existingName)
                && !string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperation.Failed(
                    Capability.Provider,
                    "Hotkey gesture already in use.",
                    PlatformErrorCodes.HotkeyConflict,
                    "hotkey.register");
            }

            _registeredByName[name] = binding with { Name = name };
            _registeredByChord[binding.ChordKey] = name;
        }

        var hookResult = await EnsureHookStartedAsync(cancellationToken);
        if (!hookResult.Success)
        {
            lock (_syncRoot)
            {
                if (_registeredByName.TryGetValue(name, out var rollback) && rollback.ChordKey == binding.ChordKey)
                {
                    _registeredByName.Remove(name);
                    _registeredByChord.Remove(binding.ChordKey);
                }
            }

            return hookResult;
        }

        return PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Global hotkey registered: {name} => {binding.NormalizedGesture}",
            "hotkey.register");
    }

    public Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Hotkey name cannot be empty.",
                PlatformErrorCodes.HotkeyNameMissing,
                "hotkey.unregister"));
        }

        bool removed;
        lock (_syncRoot)
        {
            removed = _registeredByName.Remove(name, out var existing);
            if (removed)
            {
                _registeredByChord.Remove(existing.ChordKey);
            }
        }

        if (!removed)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Hotkey `{name}` was not registered.",
                PlatformErrorCodes.HotkeyNotFound,
                "hotkey.unregister"));
        }

        StopHookIfIdle();
        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Global hotkey unregistered: {name}",
            "hotkey.unregister"));
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            StopHookUnsafe();
            _registeredByName.Clear();
            _registeredByChord.Clear();
        }
    }

    private async Task<PlatformOperationResult> EnsureHookStartedAsync(CancellationToken cancellationToken)
    {
        IGlobalKeyboardHook? createdHook = null;
        Task? createdHookTask = null;

        lock (_syncRoot)
        {
            if (_hook is not null)
            {
                return PlatformOperation.NativeSuccess(Capability.Provider, "Global hook already running.", "hotkey.hook");
            }

            try
            {
                createdHook = _hookFactory();
                createdHook.KeyPressed += OnHookKeyPressed;
                createdHookTask = createdHook.RunAsync();
                _hook = createdHook;
                _hookTask = createdHookTask;
                _hookTask.ContinueWith(
                    ObserveHookCompletion,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                StopHookUnsafe();
                return CreateHookFailureResult(ex);
            }
        }

        var startupFailure = await WaitForImmediateHookFailureAsync(createdHookTask!, cancellationToken);
        if (startupFailure is null)
        {
            return PlatformOperation.NativeSuccess(Capability.Provider, "Global hook started.", "hotkey.hook");
        }

        lock (_syncRoot)
        {
            if (ReferenceEquals(_hook, createdHook) || ReferenceEquals(_hookTask, createdHookTask))
            {
                StopHookUnsafe();
            }
        }

        return CreateHookFailureResult(startupFailure);
    }

    private void StopHookIfIdle()
    {
        lock (_syncRoot)
        {
            if (_registeredByName.Count == 0)
            {
                StopHookUnsafe();
            }
        }
    }

    private void StopHookUnsafe()
    {
        try
        {
            if (_hook is not null)
            {
                _hook.KeyPressed -= OnHookKeyPressed;
                _hook.Stop();
                _hook.Dispose();
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _hook = null;
            _hookTask = null;
        }
    }

    private async Task<Exception?> WaitForImmediateHookFailureAsync(Task hookTask, CancellationToken cancellationToken)
    {
        if (_startupProbeWindow <= TimeSpan.Zero)
        {
            return ExtractHookFailure(hookTask);
        }

        if (!hookTask.IsCompleted)
        {
            var delayTask = Task.Delay(_startupProbeWindow, cancellationToken);
            var completedTask = await Task.WhenAny(hookTask, delayTask);
            if (ReferenceEquals(completedTask, delayTask))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }

        return ExtractHookFailure(hookTask);
    }

    private void ObserveHookCompletion(Task hookTask)
    {
        var failure = ExtractHookFailure(hookTask);
        if (failure is null)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (ReferenceEquals(_hookTask, hookTask))
            {
                StopHookUnsafe();
            }
        }
    }

    private static Exception? ExtractHookFailure(Task hookTask)
    {
        if (hookTask.IsFaulted)
        {
            var aggregate = hookTask.Exception;
            return aggregate?.InnerExceptions.Count == 1
                ? aggregate.InnerExceptions[0]
                : aggregate;
        }

        return hookTask.IsCanceled
            ? new OperationCanceledException("Global hotkey hook task was canceled.")
            : null;
    }

    private PlatformOperationResult CreateHookFailureResult(Exception ex)
    {
        return PlatformOperation.Failed(
            Capability.Provider,
            $"Failed to start global hotkey hook: {DescribeHookFailure(ex)}",
            GetHookFailureErrorCode(ex),
            "hotkey.hook");
    }

    private static string DescribeHookFailure(Exception ex)
    {
        return IsAccessibilityApiDisabled(ex)
            ? "macOS Accessibility API is disabled for global keyboard hooks."
            : ex.Message;
    }

    private static string GetHookFailureErrorCode(Exception ex)
    {
        return IsPermissionDenied(ex)
            ? PlatformErrorCodes.HotkeyPermissionDenied
            : PlatformErrorCodes.HotkeyHookStartFailed;
    }

    private static bool IsPermissionDenied(Exception ex)
    {
        return IsAccessibilityApiDisabled(ex)
               || ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccessibilityApiDisabled(Exception ex)
    {
        return ex.Message.Contains("ErrorAxApiDisabled", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("ax api", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("accessibility", StringComparison.OrdinalIgnoreCase);
    }

    private void OnHookKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        RegisteredHotkey? matched = null;
        lock (_syncRoot)
        {
            foreach (var item in _registeredByName.Values)
            {
                if (IsMatch(item, e))
                {
                    matched = item;
                    break;
                }
            }
        }

        if (!matched.HasValue)
        {
            return;
        }

        var hotkey = matched.Value;
        try
        {
            Triggered?.Invoke(this, new GlobalHotkeyTriggeredEvent(
                hotkey.Name,
                hotkey.NormalizedGesture,
                DateTimeOffset.UtcNow));
        }
        catch
        {
            // Ignore callback errors to keep hook thread alive.
        }
    }

    private static bool IsMatch(RegisteredHotkey hotkey, KeyboardHookEventArgs args)
    {
        if (args.Data.KeyCode != hotkey.KeyCode)
        {
            return false;
        }

        var mask = args.RawEvent.Mask;
        var ctrl = mask.HasFlag(EventMask.Ctrl);
        var shift = mask.HasFlag(EventMask.Shift);
        var alt = mask.HasFlag(EventMask.Alt);
        var meta = mask.HasFlag(EventMask.Meta);
        return ctrl == hotkey.Ctrl
               && shift == hotkey.Shift
               && alt == hotkey.Alt
               && meta == hotkey.Meta;
    }

    private static bool TryParseGesture(string gesture, out RegisteredHotkey hotkey)
    {
        hotkey = default;
        if (!HotkeyGestureCodec.TryParse(gesture, out var parsed))
        {
            return false;
        }

        var normalized = parsed.ToStorageString();
        if (!TryParseKeyCode(parsed.Key, out var keyCode))
        {
            return false;
        }

        var chord = $"{(int)keyCode}:{parsed.Ctrl}:{parsed.Shift}:{parsed.Alt}:{parsed.Meta}";
        hotkey = new RegisteredHotkey(
            string.Empty,
            normalized,
            keyCode,
            parsed.Ctrl,
            parsed.Shift,
            parsed.Alt,
            parsed.Meta,
            chord);
        return true;
    }

    private static bool TryParseKeyCode(string token, out KeyCode keyCode)
    {
        keyCode = KeyCode.VcUndefined;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var upper = token.Trim().ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z')
        {
            return Enum.TryParse($"Vc{upper}", out keyCode);
        }

        if (upper.Length == 1 && upper[0] is >= '0' and <= '9')
        {
            return Enum.TryParse($"Vc{upper}", out keyCode);
        }

        if (upper.StartsWith("F", StringComparison.Ordinal)
            && int.TryParse(upper[1..], out var fn)
            && fn is >= 1 and <= 24)
        {
            return Enum.TryParse($"VcF{fn}", out keyCode);
        }

        return upper switch
        {
            "ENTER" => Enum.TryParse("VcEnter", out keyCode),
            "TAB" => Enum.TryParse("VcTab", out keyCode),
            "SPACE" => Enum.TryParse("VcSpace", out keyCode),
            "ESC" or "ESCAPE" => Enum.TryParse("VcEscape", out keyCode),
            "BACKSPACE" => Enum.TryParse("VcBackspace", out keyCode),
            "DELETE" => Enum.TryParse("VcDelete", out keyCode),
            "INSERT" => Enum.TryParse("VcInsert", out keyCode),
            "HOME" => Enum.TryParse("VcHome", out keyCode),
            "END" => Enum.TryParse("VcEnd", out keyCode),
            "PAGEUP" => Enum.TryParse("VcPageUp", out keyCode),
            "PAGEDOWN" => Enum.TryParse("VcPageDown", out keyCode),
            "LEFT" => Enum.TryParse("VcLeft", out keyCode),
            "UP" => Enum.TryParse("VcUp", out keyCode),
            "RIGHT" => Enum.TryParse("VcRight", out keyCode),
            "DOWN" => Enum.TryParse("VcDown", out keyCode),
            "PLUS" => Enum.TryParse("VcEquals", out keyCode),
            "MINUS" => Enum.TryParse("VcMinus", out keyCode),
            _ => false,
        };
    }

    public bool TryGetRegisteredHotkey(string name, out RegisteredHotkeyState state)
    {
        lock (_syncRoot)
        {
            if (!_registeredByName.TryGetValue(name, out var registered))
            {
                state = default!;
                return false;
            }

            state = new RegisteredHotkeyState(
                registered.Name,
                registered.NormalizedGesture,
                HotkeyGestureCodec.FormatDisplay(registered.NormalizedGesture),
                Capability.Provider,
                PlatformExecutionMode.Native);
            return true;
        }
    }

    private readonly record struct RegisteredHotkey(
        string Name,
        string NormalizedGesture,
        KeyCode KeyCode,
        bool Ctrl,
        bool Shift,
        bool Alt,
        bool Meta,
        string ChordKey);
}

public sealed class WindowMenuTrayService : ITrayService
{
    private bool _visible = true;
    private TrayMenuState _menuState = new(true, true, true, true, true);

    public event EventHandler<TrayCommandEvent>? CommandInvoked;

    public event EventHandler<TrayMenuRequestEvent>? MenuRequested;

    public PlatformCapabilityStatus Capability => new(
        Supported: false,
        Message: "System tray native integration is unavailable, fallback to window menu.",
        Provider: "window-menu",
        HasFallback: true,
        FallbackMode: "window-menu");

    public Task<PlatformOperationResult> InitializeAsync(string appTitle, TrayMenuText? menuText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PlatformOperation.FallbackSuccess(
            Capability.Provider,
            "Window-menu tray fallback initialized.",
            operationId: "tray.initialize",
            errorCode: PlatformErrorCodes.TrayFallback));
    }

    public Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PlatformOperation.FallbackSuccess(
            Capability.Provider,
            "Window-menu tray fallback shutdown completed.",
            operationId: "tray.shutdown",
            errorCode: PlatformErrorCodes.TrayFallback));
    }

    public Task<PlatformOperationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PlatformOperation.FallbackSuccess(
            Capability.Provider,
            $"Window menu fallback message: {title} - {message}",
            operationId: "tray.show",
            errorCode: PlatformErrorCodes.TrayFallback));
    }

    public Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _menuState = state;
        return Task.FromResult(PlatformOperation.FallbackSuccess(
            Capability.Provider,
            "Window menu tray state updated.",
            operationId: "tray.setMenuState",
            errorCode: PlatformErrorCodes.TrayFallback));
    }

    public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _visible = visible;
        return Task.FromResult(PlatformOperation.FallbackSuccess(
            Capability.Provider,
            $"Window menu tray visibility set to {visible}.",
            operationId: "tray.setVisible",
            errorCode: PlatformErrorCodes.TrayFallback));
    }
}

public sealed class CommandNotificationService : INotificationService
{
    private readonly PlatformCapabilityStatus _capability;

    public CommandNotificationService()
    {
        _capability = BuildCapability();
    }

    public PlatformCapabilityStatus Capability => _capability;

    public async Task<PlatformOperationResult> NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_capability.Supported)
        {
            return PlatformOperation.FallbackSuccess(
                _capability.Provider,
                _capability.Message,
                operationId: "notification.notify",
                errorCode: PlatformErrorCodes.NotificationFallback);
        }

        var command = BuildCommand(title, message);
        if (command is null)
        {
            return PlatformOperation.FallbackSuccess(
                _capability.Provider,
                "Notification command is not available, switched to in-app fallback.",
                operationId: "notification.notify",
                errorCode: PlatformErrorCodes.NotificationFallback);
        }

        var result = await ExecuteCommandAsync(command.Value.fileName, command.Value.arguments, cancellationToken);
        if (result.exitCode == 0)
        {
            return PlatformOperation.FallbackSuccess(
                _capability.Provider,
                "System notification dispatched by fallback command provider.",
                operationId: "notification.notify",
                errorCode: PlatformErrorCodes.NotificationFallback);
        }

        return PlatformOperation.FallbackSuccess(
            _capability.Provider,
            $"System notification failed (exit={result.exitCode}), switched to in-app fallback.",
            operationId: "notification.notify",
            errorCode: PlatformErrorCodes.NotificationFallback);
    }

    private static PlatformCapabilityStatus BuildCapability()
    {
        if (OperatingSystem.IsLinux())
        {
            return new PlatformCapabilityStatus(
                Supported: IsCommandAvailable("which", "notify-send"),
                Message: "Linux notifications use notify-send.",
                Provider: "notify-send",
                HasFallback: true,
                FallbackMode: "in-app");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PlatformCapabilityStatus(
                Supported: IsCommandAvailable("which", "osascript"),
                Message: "macOS notifications use osascript.",
                Provider: "osascript",
                HasFallback: true,
                FallbackMode: "in-app");
        }

        if (OperatingSystem.IsWindows())
        {
            return new PlatformCapabilityStatus(
                Supported: IsCommandAvailable("where", "powershell"),
                Message: "Windows notifications use PowerShell toast command.",
                Provider: "powershell-toast",
                HasFallback: true,
                FallbackMode: "in-app");
        }

        return new PlatformCapabilityStatus(
            Supported: false,
            Message: "No supported system notification backend for current platform.",
            Provider: "unknown",
            HasFallback: true,
            FallbackMode: "in-app");
    }

    private static (string fileName, string arguments)? BuildCommand(string title, string message)
    {
        var safeTitle = EscapeSingleLine(title);
        var safeMessage = EscapeSingleLine(message);

        if (OperatingSystem.IsLinux())
        {
            return ("notify-send", $"--app-name MAAUnified \"{safeTitle}\" \"{safeMessage}\"");
        }

        if (OperatingSystem.IsMacOS())
        {
            var script = $"display notification \"{EscapeAppleScript(safeMessage)}\" with title \"{EscapeAppleScript(safeTitle)}\"";
            return ("osascript", $"-e \"{script}\"");
        }

        if (OperatingSystem.IsWindows())
        {
            var escapedTitle = EscapePowerShellLiteral(safeTitle);
            var escapedMessage = EscapePowerShellLiteral(safeMessage);
            var script =
                "$ErrorActionPreference='Stop';" +
                "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime] > $null;" +
                "$template=[Windows.UI.Notifications.ToastTemplateType]::ToastText02;" +
                "$xml=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent($template);" +
                "$texts=$xml.GetElementsByTagName('text');" +
                $"$texts.Item(0).AppendChild($xml.CreateTextNode('{escapedTitle}')) > $null;" +
                $"$texts.Item(1).AppendChild($xml.CreateTextNode('{escapedMessage}')) > $null;" +
                "$toast=[Windows.UI.Notifications.ToastNotification]::new($xml);" +
                "$notifier=[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('MAAUnified');" +
                "$notifier.Show($toast);";
            return ("powershell", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"");
        }

        return null;
    }

    private static bool IsCommandAvailable(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(1500);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int exitCode, string output, string error)> ExecuteCommandAsync(
        string fileName,
        string args,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "Process start failed.");
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }
            });

            await process.WaitForExitAsync(cancellationToken);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            return (process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string EscapeSingleLine(string text)
    {
        return (text ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string EscapeAppleScript(string text)
    {
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string EscapePowerShellLiteral(string text)
    {
        return text.Replace("'", "''", StringComparison.Ordinal);
    }
}

public sealed class WindowScopedHotkeyService : IGlobalHotkeyService
{
    private readonly Dictionary<string, HotkeyGesture> _registeredByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registeredGestures = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

    public PlatformCapabilityStatus Capability => new(
        Supported: false,
        Message: "Global hotkeys are unavailable, fallback to window-scoped hotkeys.",
        Provider: "window-scoped",
        HasFallback: true,
        FallbackMode: "window-scoped");

    public Task<PlatformOperationResult> RegisterAsync(string name, string gesture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(PlatformOperation.Failed(Capability.Provider, "Hotkey name cannot be empty.", PlatformErrorCodes.HotkeyNameMissing, "hotkey.register"));
        }

        if (!HotkeyGestureCodec.TryParse(gesture, out var parsed))
        {
            return Task.FromResult(PlatformOperation.Failed(Capability.Provider, "Invalid hotkey gesture format.", PlatformErrorCodes.HotkeyInvalidGesture, "hotkey.register"));
        }

        var normalizedGesture = parsed.ToStorageString();
        if (_registeredByName.TryGetValue(name, out var existingGesture))
        {
            _registeredGestures.Remove(existingGesture.ToChordKey());
        }

        var chordKey = parsed.ToChordKey();
        if (_registeredGestures.Contains(chordKey))
        {
            return Task.FromResult(PlatformOperation.Failed(Capability.Provider, "Hotkey gesture already in use.", PlatformErrorCodes.HotkeyConflict, "hotkey.register", usedFallback: true));
        }

        _registeredByName[name] = parsed;
        _registeredGestures.Add(chordKey);

        return Task.FromResult(PlatformOperation.FallbackSuccess(
            Capability.Provider,
            $"Window-scoped hotkey registered: {name} => {normalizedGesture}",
            operationId: "hotkey.register",
            errorCode: PlatformErrorCodes.HotkeyFallback));
    }

    public Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(PlatformOperation.Failed(Capability.Provider, "Hotkey name cannot be empty.", PlatformErrorCodes.HotkeyNameMissing, "hotkey.unregister"));
        }

        if (_registeredByName.TryGetValue(name, out var existingGesture))
        {
            _registeredByName.Remove(name);
            _registeredGestures.Remove(existingGesture.ToChordKey());
            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                $"Window-scoped hotkey unregistered: {name}",
                operationId: "hotkey.unregister",
                errorCode: PlatformErrorCodes.HotkeyFallback));
        }

        return Task.FromResult(PlatformOperation.Failed(
            Capability.Provider,
            $"Hotkey `{name}` was not registered.",
            PlatformErrorCodes.HotkeyNotFound,
            operationId: "hotkey.unregister",
            usedFallback: true));
    }

    public bool TryDispatchWindowScopedHotkey(HotkeyGesture gesture)
    {
        KeyValuePair<string, HotkeyGesture>? matched = null;
        foreach (var item in _registeredByName)
        {
            if (item.Value.ToChordKey() == gesture.ToChordKey())
            {
                matched = item;
                break;
            }
        }

        if (matched is null)
        {
            return false;
        }

        try
        {
            Triggered?.Invoke(this, new GlobalHotkeyTriggeredEvent(
                matched.Value.Key,
                matched.Value.Value.ToStorageString(),
                DateTimeOffset.UtcNow));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetRegisteredHotkey(string name, out RegisteredHotkeyState state)
    {
        if (_registeredByName.TryGetValue(name, out var gesture))
        {
            state = new RegisteredHotkeyState(
                name,
                gesture.ToStorageString(),
                HotkeyGestureCodec.FormatDisplay(gesture.ToStorageString()),
                Capability.Provider,
                PlatformExecutionMode.Fallback);
            return true;
        }

        state = default!;
        return false;
    }
}

public sealed class CrossPlatformAutostartService : IAutostartService
{
    private const string WindowsRunKey = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    private readonly PlatformCapabilityStatus _capability;
    private readonly string _processPath;
    private readonly string _windowsValueName;
    private readonly string _linuxAutostartFile;
    private readonly string _macLaunchAgentFile;

    public CrossPlatformAutostartService()
    {
        _processPath = Environment.ProcessPath ?? string.Empty;

        _windowsValueName = string.IsNullOrWhiteSpace(_processPath)
            ? "MAA_UNIFIED"
            : "MAA_" + ComputeStableHash(_processPath);

        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _linuxAutostartFile = Path.Combine(configDir, "autostart", "maa-unified.desktop");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        _macLaunchAgentFile = Path.Combine(home, "Library", "LaunchAgents", "io.maa.unified.autostart.plist");

        _capability = BuildCapability();
    }

    public PlatformCapabilityStatus Capability => _capability;

    public Task<PlatformOperationResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_capability.Supported)
        {
            return Task.FromResult(new PlatformOperationResult<bool>(
                false,
                false,
                _capability.Message,
                PlatformErrorCodes.AutostartUnsupported,
                false,
                _capability.Provider,
                "autostart.query",
                PlatformExecutionMode.Failed));
        }

        try
        {
            var enabled = QueryAutostartEnabled();
            return Task.FromResult(new PlatformOperationResult<bool>(
                true,
                enabled,
                enabled ? "Autostart is enabled." : "Autostart is disabled.",
                null,
                false,
                _capability.Provider,
                "autostart.query",
                PlatformExecutionMode.Native));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PlatformOperationResult<bool>(
                false,
                false,
                $"Failed to query autostart: {ex.Message}",
                PlatformErrorCodes.AutostartQueryFailed,
                false,
                _capability.Provider,
                "autostart.query",
                PlatformExecutionMode.Failed));
        }
    }

    public async Task<PlatformOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_capability.Supported)
        {
            return PlatformOperation.Failed(_capability.Provider, _capability.Message, PlatformErrorCodes.AutostartUnsupported, "autostart.set");
        }

        if (string.IsNullOrWhiteSpace(_processPath) || !File.Exists(_processPath))
        {
            return PlatformOperation.Failed(_capability.Provider, "Executable path is unavailable for autostart.", PlatformErrorCodes.AutostartExecutableMissing, "autostart.set");
        }

        try
        {
            ApplyAutostart(enabled);
            var current = await IsEnabledAsync(cancellationToken);
            if (!current.Success || current.Value != enabled)
            {
                return PlatformOperation.Failed(_capability.Provider, "Autostart verification failed after update.", PlatformErrorCodes.AutostartVerificationFailed, "autostart.set");
            }

            return PlatformOperation.NativeSuccess(_capability.Provider, $"Autostart set to {enabled}.", "autostart.set");
        }
        catch (Exception ex)
        {
            return PlatformOperation.Failed(_capability.Provider, $"Failed to set autostart: {ex.Message}", PlatformErrorCodes.AutostartSetFailed, "autostart.set");
        }
    }

    private PlatformCapabilityStatus BuildCapability()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformCapabilityStatus(true, "Autostart uses HKCU Run registry entry.", "registry-run", false, null);
        }

        if (OperatingSystem.IsLinux())
        {
            return new PlatformCapabilityStatus(true, "Autostart uses XDG desktop autostart file.", "xdg-autostart", false, null);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PlatformCapabilityStatus(true, "Autostart uses LaunchAgents plist.", "launch-agent", false, null);
        }

        return new PlatformCapabilityStatus(false, "Autostart is not supported on current platform.", "unsupported", false, null);
    }

    private bool QueryAutostartEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, false);
            var value = key?.GetValue(_windowsValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains(_processPath, StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists(_linuxAutostartFile);
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(_macLaunchAgentFile);
        }

        return false;
    }

    private void ApplyAutostart(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true)
                ?? throw new InvalidOperationException("Cannot open HKCU Run registry key.");

            if (enabled)
            {
                key.SetValue(_windowsValueName, $"\"{_processPath}\"");
            }
            else
            {
                key.DeleteValue(_windowsValueName, throwOnMissingValue: false);
            }

            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_linuxAutostartFile) ?? string.Empty);
            if (enabled)
            {
                var content = BuildLinuxDesktopEntry(_processPath);
                File.WriteAllText(_linuxAutostartFile, content, Encoding.UTF8);
            }
            else
            {
                File.Delete(_linuxAutostartFile);
            }

            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_macLaunchAgentFile) ?? string.Empty);
            if (enabled)
            {
                var plist = BuildMacLaunchAgentPlist(_processPath);
                File.WriteAllText(_macLaunchAgentFile, plist, Encoding.UTF8);
            }
            else
            {
                File.Delete(_macLaunchAgentFile);
            }
        }
    }

    private static string ComputeStableHash(string input)
    {
        var hash1 = (5381 << 16) + 5381;
        var hash2 = hash1;

        for (var i = 0; i < input.Length; i += 2)
        {
            hash1 = ((hash1 << 5) + hash1) ^ input[i];
            if (i == input.Length - 1)
            {
                break;
            }

            hash2 = ((hash2 << 5) + hash2) ^ input[i + 1];
        }

        return (hash1 + (hash2 * 1566083941)).ToString("X");
    }

    private static string BuildLinuxDesktopEntry(string processPath)
    {
        return new StringBuilder()
            .AppendLine("[Desktop Entry]")
            .AppendLine("Type=Application")
            .AppendLine("Version=1.0")
            .AppendLine("Name=MAAUnified")
            .AppendLine("Comment=Start MAAUnified at login")
            .AppendLine($"Exec=\"{processPath}\"")
            .AppendLine("Terminal=false")
            .AppendLine("X-GNOME-Autostart-enabled=true")
            .ToString();
    }

    private static string BuildMacLaunchAgentPlist(string processPath)
    {
        var escapedPath = processPath
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

        return new StringBuilder()
            .AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">")
            .AppendLine("<plist version=\"1.0\">")
            .AppendLine("<dict>")
            .AppendLine("  <key>Label</key>")
            .AppendLine("  <string>io.maa.unified.autostart</string>")
            .AppendLine("  <key>ProgramArguments</key>")
            .AppendLine("  <array>")
            .AppendLine($"    <string>{escapedPath}</string>")
            .AppendLine("  </array>")
            .AppendLine("  <key>RunAtLoad</key>")
            .AppendLine("  <true/>")
            .AppendLine("</dict>")
            .AppendLine("</plist>")
            .ToString();
    }
}

public sealed class WindowsOverlayCapabilityService : IOverlayCapabilityService, IDisposable
{
    private static readonly nint HwndTopMost = new(-1);
    private const int GwlExStyle = -20;
    private const int MaxConsecutiveSyncFailures = 3;
    private const nint WsExLayered = 0x00080000;
    private const nint WsExTransparent = 0x00000020;
    private const nint WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    internal readonly record struct OverlayRect(int Left, int Top, int Right, int Bottom);

    internal interface INativeApi
    {
        IEnumerable<nint> EnumerateWindows();

        bool IsWindow(nint hWnd);

        bool IsWindowVisible(nint hWnd);

        int GetWindowTextLength(nint hWnd);

        string GetWindowText(nint hWnd);

        uint GetWindowThreadProcessId(nint hWnd);

        bool TryGetWindowRect(nint hWnd, out OverlayRect rect);

        bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        bool ShowWindow(nint hWnd, int nCmdShow);

        nint GetWindowLongPtr(nint hWnd, int nIndex);

        int GetLastWin32Error();

        nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }

    private readonly object _syncRoot = new();
    private readonly INativeApi _api;
    private nint _selectedTarget;
    private nint _hostWindow;
    private bool _clickThrough = true;
    private double _opacity = 0.85d;
    private bool _visible;
    private Timer? _attachSyncTimer;
    private OverlayRect _lastTargetRect;
    private DateTimeOffset _lastSyncAt;
    private string? _lastSyncIssue;
    private int _consecutiveSyncFailures;

    public WindowsOverlayCapabilityService()
        : this(new Win32NativeApi())
    {
    }

    internal WindowsOverlayCapabilityService(INativeApi api)
    {
        _api = api;
    }

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "Overlay attachment is available via Win32 window targets.",
        Provider: "win32-overlay",
        HasFallback: true,
        FallbackMode: "preview-and-log");

    public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged;

    public Task<PlatformOperationResult> BindHostWindowAsync(
        nint hostWindowHandle,
        bool clickThrough,
        double opacity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (hostWindowHandle == nint.Zero || !_api.IsWindow(hostWindowHandle))
            {
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    "Overlay host window handle is invalid.",
                    PlatformErrorCodes.OverlayHostNotBound,
                    "overlay.bindHost"));
            }

            _hostWindow = hostWindowHandle;
            _clickThrough = clickThrough;
            _opacity = Math.Clamp(opacity, 0.05d, 1.0d);

            if (!ConfigureHostWindow())
            {
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    "Failed to configure overlay host window styles.",
                    PlatformErrorCodes.OverlayAttachFailed,
                    "overlay.bindHost"));
            }

            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                "Overlay host window bound.",
                "overlay.bindHost"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Overlay host bind failed: {ex.Message}",
                PlatformErrorCodes.OverlayAttachFailed,
                "overlay.bindHost"));
        }
    }

    public Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var targets = new List<OverlayTarget>();
            var seen = new HashSet<nint>();

            foreach (var hWnd in _api.EnumerateWindows())
            {
                if (!_api.IsWindowVisible(hWnd))
                {
                    continue;
                }

                var len = _api.GetWindowTextLength(hWnd);
                if (len <= 0)
                {
                    continue;
                }

                var title = _api.GetWindowText(hWnd).Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var pid = _api.GetWindowThreadProcessId(hWnd);
                if (pid == (uint)Environment.ProcessId)
                {
                    continue;
                }

                if (!seen.Add(hWnd))
                {
                    continue;
                }

                string? processName = null;
                try
                {
                    processName = Process.GetProcessById((int)pid).ProcessName;
                }
                catch
                {
                    // Best-effort metadata for target restore.
                }

                var display = string.IsNullOrWhiteSpace(processName)
                    ? $"{title} (pid:{pid})"
                    : $"{title} - {processName} - {pid}";
                targets.Add(new OverlayTarget(
                    $"hwnd:{hWnd:X}",
                    display,
                    false,
                    NativeHandle: (long)hWnd,
                    ProcessId: (int)pid,
                    ProcessName: processName,
                    WindowTitle: title));
                if (targets.Count >= 80)
                {
                    break;
                }
            }

            targets.Insert(0, new OverlayTarget("preview", "Preview + Logs", true));
            IReadOnlyList<OverlayTarget> resultTargets = targets;
            if (targets.Count <= 1)
            {
                return Task.FromResult(PlatformOperation.FallbackSuccess(
                    Capability.Provider,
                    resultTargets,
                    "No native overlay target is available, switched to preview mode.",
                    "overlay.query-targets",
                    PlatformErrorCodes.OverlayPreviewMode));
            }

            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                resultTargets,
                $"Found {targets.Count - 1} native overlay target(s).",
                "overlay.query-targets"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Overlay target query failed: {ex.Message}",
                PlatformErrorCodes.OverlayQueryFailed,
                "overlay.query-targets",
                value: (IReadOnlyList<OverlayTarget>)Array.Empty<OverlayTarget>()));
        }
    }

    public Task<PlatformOperationResult> SelectTargetAsync(string targetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return SelectTargetCore(targetId);
        }
        catch (Exception ex)
        {
            EnterPreviewMode(
                "Overlay target selection failed; switched to Preview + Logs mode.",
                "fallback-enter",
                PlatformErrorCodes.OverlayAttachFailed);
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Overlay target selection failed: {ex.Message}",
                PlatformErrorCodes.OverlayAttachFailed,
                operationId: "overlay.selectTarget"));
        }
    }

    private Task<PlatformOperationResult> SelectTargetCore(string targetId)
    {
        if (string.Equals(targetId, "preview", StringComparison.OrdinalIgnoreCase))
        {
            _selectedTarget = nint.Zero;
            _consecutiveSyncFailures = 0;
            StopAttachSync();
            if (_visible)
            {
                ShowPreviewHostBestEffort();
                EmitStateChanged(
                    OverlayRuntimeMode.Preview,
                    visible: true,
                    targetId: "preview",
                    action: "fallback-enter",
                    message: "Overlay switched to Preview + Logs mode.",
                    usedFallback: true,
                    errorCode: PlatformErrorCodes.OverlayPreviewMode);
            }

            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                "Overlay target switched to preview mode.",
                operationId: "overlay.selectTarget",
                errorCode: PlatformErrorCodes.OverlayPreviewMode));
        }

        if (!TryParseHandle(targetId, out var handle))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Invalid overlay target id: {targetId}",
                PlatformErrorCodes.OverlayTargetInvalid,
                operationId: "overlay.selectTarget"));
        }

        if (!_api.IsWindow(handle))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Overlay target is unavailable: 0x{handle:X}",
                PlatformErrorCodes.OverlayTargetGone,
                operationId: "overlay.selectTarget"));
        }

        _selectedTarget = handle;
        _consecutiveSyncFailures = 0;
        if (_visible)
        {
            if (_hostWindow == nint.Zero)
            {
                EnterPreviewMode(
                    "Overlay host is unavailable; switched to Preview + Logs mode.",
                    "fallback-enter",
                    PlatformErrorCodes.OverlayHostNotBound);
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    "Overlay host is unavailable; switched to Preview + Logs mode.",
                    PlatformErrorCodes.OverlayHostNotBound,
                    operationId: "overlay.selectTarget"));
            }

            if (!ConfigureHostWindow())
            {
                EnterPreviewMode(
                    "Overlay target attach failed; switched to Preview + Logs mode.",
                    "fallback-enter",
                    PlatformErrorCodes.OverlayAttachFailed);
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    "Overlay target attach failed; switched to Preview + Logs mode.",
                    PlatformErrorCodes.OverlayAttachFailed,
                    operationId: "overlay.selectTarget"));
            }

            if (!TryAttachSelectedTarget(out var attachMessage, out var attachErrorCode))
            {
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    attachMessage,
                    attachErrorCode,
                    operationId: "overlay.selectTarget"));
            }

            EmitStateChanged(
                OverlayRuntimeMode.Native,
                visible: true,
                targetId: GetSelectedTargetId(),
                action: "target-change",
                message: $"Overlay target changed to 0x{handle:X}.",
                usedFallback: false,
                errorCode: null);
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Overlay target selected: 0x{handle:X}",
            operationId: "overlay.selectTarget"));
    }

    public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return SetVisibleCore(visible);
        }
        catch (Exception ex)
        {
            EnterPreviewMode(
                "Overlay visibility update failed; switched to Preview + Logs mode.",
                "fallback-enter",
                PlatformErrorCodes.OverlayAttachFailed);
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Overlay visibility update failed: {ex.Message}",
                PlatformErrorCodes.OverlayAttachFailed,
                operationId: "overlay.setVisible"));
        }
    }

    private Task<PlatformOperationResult> SetVisibleCore(bool visible)
    {
        _visible = visible;

        if (!visible)
        {
            _consecutiveSyncFailures = 0;
            StopAttachSync();
            HideHostWindow();
            EmitStateChanged(
                OverlayRuntimeMode.Hidden,
                visible: false,
                targetId: GetSelectedTargetId(),
                action: "hide",
                message: "Overlay hidden.",
                usedFallback: false,
                errorCode: null);
            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                "Overlay visibility set to false.",
                operationId: "overlay.setVisible"));
        }

        if (_selectedTarget == nint.Zero)
        {
            _consecutiveSyncFailures = 0;
            StopAttachSync();
            ShowPreviewHostBestEffort();
            EmitStateChanged(
                OverlayRuntimeMode.Preview,
                visible: true,
                targetId: "preview",
                action: "fallback-enter",
                message: "Overlay switched to Preview + Logs mode because no native target is selected.",
                usedFallback: true,
                errorCode: PlatformErrorCodes.OverlayPreviewMode);
            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                "Overlay switched to preview mode because no native target is selected.",
                operationId: "overlay.setVisible",
                errorCode: PlatformErrorCodes.OverlayPreviewMode));
        }

        if (_hostWindow == nint.Zero)
        {
            EnterPreviewMode(
                "Overlay host is unavailable; switched to Preview + Logs mode.",
                "fallback-enter",
                PlatformErrorCodes.OverlayHostNotBound);
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Overlay host is unavailable; switched to Preview + Logs mode.",
                PlatformErrorCodes.OverlayHostNotBound,
                operationId: "overlay.setVisible"));
        }

        if (!ConfigureHostWindow())
        {
            EnterPreviewMode(
                "Overlay host configuration failed; switched to Preview + Logs mode.",
                "fallback-enter",
                PlatformErrorCodes.OverlayAttachFailed);
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Overlay host configuration failed; switched to Preview + Logs mode.",
                PlatformErrorCodes.OverlayAttachFailed,
                operationId: "overlay.setVisible"));
        }

        if (!TryAttachSelectedTarget(out var attachMessage, out var attachErrorCode))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                attachMessage,
                attachErrorCode,
                operationId: "overlay.setVisible"));
        }

        EmitStateChanged(
            OverlayRuntimeMode.Native,
            visible: true,
            targetId: GetSelectedTargetId(),
            action: "show-native",
            message: $"Overlay attached to native target: 0x{_selectedTarget:X}.",
            usedFallback: false,
            errorCode: null);

        var extra = _lastSyncAt == default
            ? string.Empty
            : $" last-sync={_lastSyncAt:O}";
        var issue = string.IsNullOrWhiteSpace(_lastSyncIssue)
            ? string.Empty
            : $" issue={_lastSyncIssue}";
        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Overlay visibility set to {visible}.{extra}{issue}",
            operationId: "overlay.setVisible"));
    }

    public void Dispose()
    {
        StopAttachSync();
    }

    internal void SyncNowForTesting()
    {
        SyncSelectedTarget();
    }

    private void StartAttachSync()
    {
        lock (_syncRoot)
        {
            _attachSyncTimer ??= new Timer(_ => SyncSelectedTargetSafe(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(300));
        }
    }

    private void StopAttachSync()
    {
        lock (_syncRoot)
        {
            _attachSyncTimer?.Dispose();
            _attachSyncTimer = null;
        }
    }

    private void SyncSelectedTarget()
    {
        var handle = _selectedTarget;
        if (handle == nint.Zero || _hostWindow == nint.Zero || !_visible)
        {
            return;
        }

        if (SyncSelectedTargetCore(handle, out var failureMessage, out var failureCode))
        {
            return;
        }

        if (string.Equals(failureCode, PlatformErrorCodes.OverlayTargetGone, StringComparison.Ordinal))
        {
            EnterPreviewMode(
                "Overlay target was lost; switched to Preview + Logs mode.",
                "target-lost",
                PlatformErrorCodes.OverlayTargetGone);
            return;
        }

        _consecutiveSyncFailures++;
        _lastSyncIssue = failureMessage;
        if (_consecutiveSyncFailures >= MaxConsecutiveSyncFailures)
        {
            EnterPreviewMode(
                "Overlay sync failed repeatedly; switched to Preview + Logs mode.",
                "fallback-enter",
                failureCode);
        }
    }

    private void SyncSelectedTargetSafe()
    {
        try
        {
            SyncSelectedTarget();
        }
        catch
        {
            EnterPreviewMode(
                "Overlay sync failed unexpectedly; switched to Preview + Logs mode.",
                "fallback-enter",
                PlatformErrorCodes.OverlayAttachFailed);
        }
    }

    private bool TryAttachSelectedTarget(out string failureMessage, out string failureCode)
    {
        if (!SyncSelectedTargetCore(_selectedTarget, out failureMessage, out failureCode))
        {
            EnterPreviewMode(
                string.Equals(failureCode, PlatformErrorCodes.OverlayTargetGone, StringComparison.Ordinal)
                    ? "Overlay target was lost; switched to Preview + Logs mode."
                    : "Overlay target attach failed; switched to Preview + Logs mode.",
                string.Equals(failureCode, PlatformErrorCodes.OverlayTargetGone, StringComparison.Ordinal)
                    ? "target-lost"
                    : "fallback-enter",
                failureCode);
            return false;
        }

        _consecutiveSyncFailures = 0;
        StartAttachSync();
        return true;
    }

    private bool SyncSelectedTargetCore(nint handle, out string failureMessage, out string failureCode)
    {
        if (handle == nint.Zero || !_api.IsWindow(handle))
        {
            failureMessage = $"Overlay target is unavailable: 0x{handle:X}";
            failureCode = PlatformErrorCodes.OverlayTargetGone;
            return false;
        }

        if (!_api.TryGetWindowRect(handle, out var rect))
        {
            failureMessage = "GetWindowRect failed.";
            failureCode = PlatformErrorCodes.OverlayAttachFailed;
            return false;
        }

        if (!SetHostBounds(rect))
        {
            failureMessage = "SetWindowPos failed.";
            failureCode = PlatformErrorCodes.OverlayAttachFailed;
            return false;
        }

        _lastTargetRect = rect;
        _lastSyncAt = DateTimeOffset.UtcNow;
        _lastSyncIssue = null;
        _consecutiveSyncFailures = 0;
        failureMessage = string.Empty;
        failureCode = string.Empty;
        return true;
    }

    private void EnterPreviewMode(string message, string action, string? errorCode)
    {
        // Avoid duplicate Preview notifications when concurrent sync callbacks
        // observe the same loss/fallback after the first state transition.
        if (_selectedTarget == nint.Zero
            && string.Equals(_lastSyncIssue, message, StringComparison.Ordinal)
            && (_attachSyncTimer is null || string.Equals(action, "target-lost", StringComparison.Ordinal)))
        {
            return;
        }

        _selectedTarget = nint.Zero;
        _consecutiveSyncFailures = 0;
        _lastSyncIssue = message;
        StopAttachSync();
        if (_visible)
        {
            ShowPreviewHostBestEffort();
        }
        else
        {
            HideHostWindow();
        }
        EmitStateChanged(
            OverlayRuntimeMode.Preview,
            visible: _visible,
            targetId: "preview",
            action: action,
            message: message,
            usedFallback: true,
            errorCode: errorCode);
    }

    private void EmitStateChanged(
        OverlayRuntimeMode mode,
        bool visible,
        string targetId,
        string action,
        string message,
        bool usedFallback,
        string? errorCode)
    {
        try
        {
            OverlayStateChanged?.Invoke(
                this,
                new OverlayStateChangedEvent(
                    mode,
                    visible,
                    targetId,
                    action,
                    message,
                    DateTimeOffset.UtcNow,
                    Capability.Provider,
                    usedFallback,
                    errorCode));
        }
        catch
        {
            // Overlay state consumers must not destabilize native callbacks.
        }
    }

    private string GetSelectedTargetId()
    {
        return _selectedTarget == nint.Zero
            ? "preview"
            : $"hwnd:{_selectedTarget:X}";
    }

    private bool ConfigureHostWindow()
    {
        if (_hostWindow == nint.Zero || !_api.IsWindow(_hostWindow))
        {
            return false;
        }

        var style = _api.GetWindowLongPtr(_hostWindow, GwlExStyle);
        if (style == nint.Zero && _api.GetLastWin32Error() != 0)
        {
            return false;
        }

        style |= WsExLayered;
        style |= WsExNoActivate;
        if (_clickThrough)
        {
            style |= WsExTransparent;
        }
        else
        {
            style &= ~WsExTransparent;
        }

        _ = _api.SetWindowLongPtr(_hostWindow, GwlExStyle, style);
        var alpha = (byte)Math.Round(_opacity * 255d, MidpointRounding.AwayFromZero);
        return _api.SetLayeredWindowAttributes(_hostWindow, 0, alpha, LwaAlpha);
    }

    private bool SetHostBounds(OverlayRect targetRect)
    {
        if (_hostWindow == nint.Zero)
        {
            return false;
        }

        var width = Math.Max(1, targetRect.Right - targetRect.Left);
        var height = Math.Max(1, targetRect.Bottom - targetRect.Top);
        var ok = _api.SetWindowPos(
            _hostWindow,
            HwndTopMost,
            targetRect.Left,
            targetRect.Top,
            width,
            height,
            SwpNoActivate | SwpShowWindow);
        if (ok)
        {
            _ = _api.ShowWindow(_hostWindow, SwShowNoActivate);
        }

        return ok;
    }

    private void ShowPreviewHostBestEffort()
    {
        try
        {
            if (_hostWindow == nint.Zero || !_api.IsWindow(_hostWindow))
            {
                return;
            }

            if (!ConfigureHostWindow())
            {
                return;
            }

            _ = _api.ShowWindow(_hostWindow, SwShowNoActivate);
        }
        catch
        {
            // Preview fallback is best-effort when native handles are unhealthy.
        }
    }

    private void HideHostWindow()
    {
        try
        {
            if (_hostWindow != nint.Zero && _api.IsWindow(_hostWindow))
            {
                _ = _api.ShowWindow(_hostWindow, SwHide);
            }
        }
        catch
        {
            // Hide is best-effort during fallback/shutdown.
        }
    }

    private static bool TryParseHandle(string targetId, out nint handle)
    {
        handle = nint.Zero;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        var payload = targetId.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase)
            ? targetId[5..]
            : targetId;

        if (!long.TryParse(payload, System.Globalization.NumberStyles.HexNumber, null, out var value)
            && !long.TryParse(payload, out value))
        {
            return false;
        }

        handle = (nint)value;
        return handle != nint.Zero;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    private sealed class Win32NativeApi : INativeApi
    {
        public IEnumerable<nint> EnumerateWindows()
        {
            var windows = new List<nint>();
            EnumWindows((hWnd, lParam) =>
            {
                _ = lParam;
                windows.Add(hWnd);
                return true;
            }, nint.Zero);
            return windows;
        }

        public bool IsWindow(nint hWnd) => WindowsOverlayCapabilityService.IsWindow(hWnd);

        public bool IsWindowVisible(nint hWnd) => WindowsOverlayCapabilityService.IsWindowVisible(hWnd);

        public int GetWindowTextLength(nint hWnd) => WindowsOverlayCapabilityService.GetWindowTextLength(hWnd);

        public string GetWindowText(nint hWnd)
        {
            var len = GetWindowTextLength(hWnd);
            if (len <= 0)
            {
                return string.Empty;
            }

            var titleBuilder = new StringBuilder(len + 1);
            _ = WindowsOverlayCapabilityService.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
            return titleBuilder.ToString();
        }

        public uint GetWindowThreadProcessId(nint hWnd)
        {
            WindowsOverlayCapabilityService.GetWindowThreadProcessId(hWnd, out var pid);
            return pid;
        }

        public bool TryGetWindowRect(nint hWnd, out OverlayRect rect)
        {
            if (WindowsOverlayCapabilityService.GetWindowRect(hWnd, out var winRect))
            {
                rect = new OverlayRect(winRect.Left, winRect.Top, winRect.Right, winRect.Bottom);
                return true;
            }

            rect = default;
            return false;
        }

        public bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags)
            => WindowsOverlayCapabilityService.SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);

        public bool ShowWindow(nint hWnd, int nCmdShow)
            => WindowsOverlayCapabilityService.ShowWindow(hWnd, nCmdShow);

        public nint GetWindowLongPtr(nint hWnd, int nIndex)
            => GetWindowLongPtrCompat(hWnd, nIndex);

        public int GetLastWin32Error()
            => Marshal.GetLastWin32Error();

        public nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong)
            => SetWindowLongPtrCompat(hWnd, nIndex, dwNewLong);

        public bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags)
            => WindowsOverlayCapabilityService.SetLayeredWindowAttributes(hwnd, crKey, bAlpha, dwFlags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out WinRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private static nint GetWindowLongPtrCompat(nint hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new nint(GetWindowLong32(hWnd, nIndex));
    }

    private static nint SetWindowLongPtrCompat(nint hWnd, int nIndex, nint dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new nint(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }
}

public sealed class LinuxOverlayCapabilityService : IOverlayCapabilityService
{
    private const int MaxTargets = 80;
    private const int MaxConsecutiveSyncFailures = 3;
    private const int NativeAttachInset = 8;
    private readonly IX11WindowEnumerator _x11WindowEnumerator;
    private readonly ICommandRunner _commandRunner;
    private readonly object _syncRoot = new();
    private nint _hostWindow;
    private nint _selectedTarget;
    private string _selectedTargetId = "preview";
    private bool _clickThrough = true;
    private bool _visible;
    private Timer? _attachSyncTimer;
    private int _consecutiveSyncFailures;
    private DateTimeOffset _lastSyncAt;
    private string? _lastSyncIssue;

    internal interface IX11WindowEnumerator
    {
        IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId);
    }

    internal interface ICommandRunner
    {
        Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
    }

    internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    public LinuxOverlayCapabilityService()
        : this(new NativeX11WindowEnumerator(), new ProcessCommandRunner())
    {
    }

    internal LinuxOverlayCapabilityService(ICommandRunner commandRunner)
        : this(new UnsupportedX11WindowEnumerator(), commandRunner)
    {
    }

    internal LinuxOverlayCapabilityService(IX11WindowEnumerator x11WindowEnumerator, ICommandRunner commandRunner)
    {
        _x11WindowEnumerator = x11WindowEnumerator;
        _commandRunner = commandRunner;
    }

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "Linux overlay target discovery is available via X11; native attachment falls back to preview.",
        Provider: "linux-x11-overlay",
        HasFallback: true,
        FallbackMode: "preview-and-log");

    public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged;

    public static bool TryCreate([NotNullWhen(true)] out LinuxOverlayCapabilityService? service)
    {
        service = null;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return false;
        }

        service = new LinuxOverlayCapabilityService();
        return true;
    }

    public Task<PlatformOperationResult> BindHostWindowAsync(
        nint hostWindowHandle,
        bool clickThrough,
        double opacity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (hostWindowHandle == nint.Zero || !X11WindowInterop.IsWindow(hostWindowHandle))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Linux overlay host window handle is invalid.",
                PlatformErrorCodes.OverlayHostNotBound,
                "overlay.bindHost"));
        }

        _hostWindow = hostWindowHandle;
        _clickThrough = clickThrough;
        if (_clickThrough)
        {
            X11WindowInterop.TrySetInputPassthrough(_hostWindow);
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            "Linux overlay host window bound.",
            "overlay.bindHost"));
    }

    public async Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? x11FailureMessage = null;
        IReadOnlyList<OverlayTarget> targets;
        try
        {
            targets = _x11WindowEnumerator.EnumerateTargets(Environment.ProcessId);
            if (targets.Count > 1)
            {
                return PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    targets,
                    $"Found {targets.Count - 1} Linux overlay target(s).",
                    "overlay.query-targets");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            x11FailureMessage = ex.Message;
        }

        try
        {
            targets = await QueryTargetsWithWmctrlAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            targets = [CreatePreviewTarget()];
            return PlatformOperation.FallbackSuccess(
                Capability.Provider,
                targets,
                BuildUnavailableMessage(ex.Message, x11FailureMessage),
                "overlay.query-targets",
                PlatformErrorCodes.OverlayPreviewMode);
        }

        if (targets.Count <= 1)
        {
            return PlatformOperation.FallbackSuccess(
                Capability.Provider,
                targets,
                string.IsNullOrWhiteSpace(x11FailureMessage)
                    ? "No Linux overlay target is available, switched to preview mode."
                    : $"No Linux overlay target is available, switched to preview mode. X11 query: {x11FailureMessage}",
                "overlay.query-targets",
                PlatformErrorCodes.OverlayPreviewMode);
        }

        return PlatformOperation.NativeSuccess(
            Capability.Provider,
            targets,
            $"Found {targets.Count - 1} Linux overlay target(s).",
            "overlay.query-targets");
    }

    private async Task<IReadOnlyList<OverlayTarget>> QueryTargetsWithWmctrlAsync(CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync("wmctrl", "-lpx", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError);
        }

        return ParseWmctrlTargets(result.StandardOutput, Environment.ProcessId);
    }

    public Task<PlatformOperationResult> SelectTargetAsync(string targetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Overlay target id is empty.",
                PlatformErrorCodes.OverlayTargetInvalid,
                "overlay.selectTarget"));
        }

        if (string.Equals(targetId, "preview", StringComparison.OrdinalIgnoreCase))
        {
            _selectedTarget = nint.Zero;
            _selectedTargetId = "preview";
            _consecutiveSyncFailures = 0;
            StopAttachSync();
            if (_visible)
            {
                EmitPreviewState("fallback-enter", "Overlay switched to Preview + Logs mode.");
            }

            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                "Overlay target switched to preview mode.",
                "overlay.selectTarget",
                PlatformErrorCodes.OverlayPreviewMode));
        }

        if (!TryParseX11TargetId(targetId, out var targetWindow))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Invalid Linux overlay target id: {targetId}",
                PlatformErrorCodes.OverlayTargetInvalid,
                "overlay.selectTarget"));
        }

        if (!X11WindowInterop.IsWindow(targetWindow))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Linux overlay target is unavailable: 0x{targetWindow:X}",
                PlatformErrorCodes.OverlayTargetGone,
                "overlay.selectTarget"));
        }

        _selectedTarget = targetWindow;
        _selectedTargetId = targetId;
        if (_visible)
        {
            if (_hostWindow == nint.Zero)
            {
                EnterPreviewMode(
                    "Overlay host is unavailable; switched to Preview + Logs mode.",
                    "fallback-enter",
                    PlatformErrorCodes.OverlayHostNotBound);
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    "Overlay host is unavailable; switched to Preview + Logs mode.",
                    PlatformErrorCodes.OverlayHostNotBound,
                    "overlay.selectTarget"));
            }

            if (!TryAttachSelectedTarget(out var attachMessage, out var attachErrorCode))
            {
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    attachMessage,
                    attachErrorCode,
                    "overlay.selectTarget"));
            }

            EmitStateChanged(
                OverlayRuntimeMode.Native,
                visible: true,
                targetId: _selectedTargetId,
                action: "target-change",
                message: $"Overlay target changed to 0x{targetWindow:X}.",
                usedFallback: false,
                errorCode: null);
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Overlay target selected: {targetId}",
            "overlay.selectTarget"));
    }

    public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _visible = visible;
        if (!visible)
        {
            _consecutiveSyncFailures = 0;
            StopAttachSync();
            HideHostWindow();
            EmitStateChanged(
                OverlayRuntimeMode.Hidden,
                visible: false,
                targetId: _selectedTargetId,
                action: "hide",
                message: "Overlay hidden.",
                usedFallback: false,
                errorCode: null);
            return Task.FromResult(PlatformOperation.NativeSuccess(
                Capability.Provider,
                "Overlay visibility set to false.",
                "overlay.setVisible"));
        }

        if (_selectedTarget == nint.Zero)
        {
            _consecutiveSyncFailures = 0;
            StopAttachSync();
            ShowHostWindowBestEffort();
            EmitPreviewState("fallback-enter", "Overlay switched to Preview + Logs mode because no native target is selected.");
            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                "Overlay switched to preview mode because no native target is selected.",
                "overlay.setVisible",
                PlatformErrorCodes.OverlayPreviewMode));
        }

        if (_hostWindow == nint.Zero)
        {
            EnterPreviewMode(
                "Overlay host is unavailable; switched to Preview + Logs mode.",
                "fallback-enter",
                PlatformErrorCodes.OverlayHostNotBound);
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "Overlay host is unavailable; switched to Preview + Logs mode.",
                PlatformErrorCodes.OverlayHostNotBound,
                "overlay.setVisible"));
        }

        if (!TryAttachSelectedTarget(out var attachMessage, out var attachErrorCode))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                attachMessage,
                attachErrorCode,
                "overlay.setVisible"));
        }

        EmitStateChanged(
            OverlayRuntimeMode.Native,
            visible: true,
            targetId: _selectedTargetId,
            action: "show-native",
            message: $"Overlay attached to Linux target: 0x{_selectedTarget:X}.",
            usedFallback: false,
            errorCode: null);

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Overlay visibility set to true. last-sync={_lastSyncAt:O}",
            "overlay.setVisible"));
    }

    internal void SyncNowForTesting()
    {
        SyncSelectedTarget();
    }

    internal static IReadOnlyList<OverlayTarget> ParseWmctrlTargets(string output, int currentProcessId)
    {
        var targets = new List<OverlayTarget> { CreatePreviewTarget() };
        var seen = new HashSet<long>();

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6
                || !TryParseWindowId(parts[0], out var windowId)
                || !int.TryParse(parts[2], out var pid)
                || pid == currentProcessId
                || !seen.Add(windowId))
            {
                continue;
            }

            var windowClass = NormalizeWmClass(parts[3]);
            var title = parts[5].Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var processName = ResolveProcessName(pid, windowClass);
            var display = string.IsNullOrWhiteSpace(processName)
                ? $"{title} (pid:{pid})"
                : $"{title} - {processName} - {pid}";

            targets.Add(new OverlayTarget(
                $"x11:0x{windowId:X}",
                display,
                false,
                NativeHandle: windowId,
                ProcessId: pid,
                ProcessName: processName,
                WindowTitle: title));

            if (targets.Count > MaxTargets)
            {
                break;
            }
        }

        return targets;
    }

    private static OverlayTarget CreatePreviewTarget()
        => new("preview", "Preview + Logs", true);

    private static string BuildUnavailableMessage(string details, string? x11FailureMessage)
    {
        var commandDetails = details.Trim();
        if (!string.IsNullOrWhiteSpace(commandDetails) && !string.IsNullOrWhiteSpace(x11FailureMessage))
        {
            return $"Linux overlay target query fell back to preview mode. X11 query: {x11FailureMessage}; wmctrl: {commandDetails}";
        }

        if (!string.IsNullOrWhiteSpace(commandDetails))
        {
            return $"Linux overlay target query fell back to preview mode: {commandDetails}";
        }

        if (!string.IsNullOrWhiteSpace(x11FailureMessage))
        {
            return $"Linux overlay target query fell back to preview mode. X11 query: {x11FailureMessage}";
        }

        return "Linux overlay target query fell back to preview mode; no window data was returned.";
    }

    private static bool TryParseWindowId(string text, out long windowId)
    {
        windowId = 0;
        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return long.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out windowId);
    }

    private static string? NormalizeWmClass(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "N/A")
        {
            return null;
        }

        var text = value.Trim();
        var dot = text.LastIndexOf('.');
        return dot >= 0 && dot < text.Length - 1
            ? text[(dot + 1)..]
            : text;
    }

    private static string? ResolveProcessName(int pid, string? fallback)
    {
        if (pid > 0)
        {
            try
            {
                return Process.GetProcessById(pid).ProcessName;
            }
            catch
            {
                // Best-effort metadata for target restore.
            }
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private void StartAttachSync()
    {
        lock (_syncRoot)
        {
            _attachSyncTimer ??= new Timer(_ => SyncSelectedTargetSafe(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(300));
        }
    }

    private void StopAttachSync()
    {
        lock (_syncRoot)
        {
            _attachSyncTimer?.Dispose();
            _attachSyncTimer = null;
        }
    }

    private void SyncSelectedTarget()
    {
        var target = _selectedTarget;
        if (!_visible || target == nint.Zero || _hostWindow == nint.Zero)
        {
            return;
        }

        if (SyncSelectedTargetCore(target, out var failureMessage, out var failureCode))
        {
            return;
        }

        if (string.Equals(failureCode, PlatformErrorCodes.OverlayTargetGone, StringComparison.Ordinal))
        {
            EnterPreviewMode(
                "Overlay target was lost; switched to Preview + Logs mode.",
                "target-lost",
                PlatformErrorCodes.OverlayTargetGone);
            return;
        }

        _consecutiveSyncFailures++;
        _lastSyncIssue = failureMessage;
        if (_consecutiveSyncFailures >= MaxConsecutiveSyncFailures)
        {
            EnterPreviewMode(
                "Overlay sync failed repeatedly; switched to Preview + Logs mode.",
                "fallback-enter",
                failureCode);
        }
    }

    private void SyncSelectedTargetSafe()
    {
        try
        {
            SyncSelectedTarget();
        }
        catch
        {
            EnterPreviewMode(
                "Overlay sync failed unexpectedly; switched to Preview + Logs mode.",
                "fallback-enter",
                PlatformErrorCodes.OverlayAttachFailed);
        }
    }

    private bool TryAttachSelectedTarget(out string failureMessage, out string failureCode)
    {
        if (!SyncSelectedTargetCore(_selectedTarget, out failureMessage, out failureCode))
        {
            EnterPreviewMode(
                string.Equals(failureCode, PlatformErrorCodes.OverlayTargetGone, StringComparison.Ordinal)
                    ? "Overlay target was lost; switched to Preview + Logs mode."
                    : "Overlay target attach failed; switched to Preview + Logs mode.",
                string.Equals(failureCode, PlatformErrorCodes.OverlayTargetGone, StringComparison.Ordinal)
                    ? "target-lost"
                    : "fallback-enter",
                failureCode);
            return false;
        }

        _consecutiveSyncFailures = 0;
        StartAttachSync();
        return true;
    }

    private bool SyncSelectedTargetCore(nint target, out string failureMessage, out string failureCode)
    {
        if (target == nint.Zero || !X11WindowInterop.IsWindow(target))
        {
            failureMessage = $"Linux overlay target is unavailable: 0x{target:X}";
            failureCode = PlatformErrorCodes.OverlayTargetGone;
            return false;
        }

        if (_hostWindow == nint.Zero || !X11WindowInterop.IsWindow(_hostWindow))
        {
            failureMessage = "Linux overlay host window is unavailable.";
            failureCode = PlatformErrorCodes.OverlayHostNotBound;
            return false;
        }

        if (!X11WindowInterop.TryGetAbsoluteBounds(target, out var bounds))
        {
            failureMessage = $"Unable to read Linux overlay target bounds: 0x{target:X}";
            failureCode = PlatformErrorCodes.OverlayAttachFailed;
            return false;
        }

        bounds = X11WindowInterop.Inset(bounds, NativeAttachInset, NativeAttachInset);
        if (!X11WindowInterop.TryMoveResizeAndRaise(_hostWindow, bounds))
        {
            failureMessage = $"Unable to move Linux overlay host to target bounds: 0x{target:X}";
            failureCode = PlatformErrorCodes.OverlayAttachFailed;
            return false;
        }

        _lastSyncAt = DateTimeOffset.UtcNow;
        _lastSyncIssue = null;
        _consecutiveSyncFailures = 0;
        failureMessage = string.Empty;
        failureCode = string.Empty;
        return true;
    }

    private void EnterPreviewMode(string message, string action, string? errorCode)
    {
        _selectedTarget = nint.Zero;
        _selectedTargetId = "preview";
        _consecutiveSyncFailures = 0;
        _lastSyncIssue = message;
        StopAttachSync();
        if (!_visible)
        {
            HideHostWindow();
        }
        else
        {
            ShowHostWindowBestEffort();
        }

        EmitStateChanged(
            OverlayRuntimeMode.Preview,
            visible: _visible,
            targetId: "preview",
            action: action,
            message: message,
            usedFallback: true,
            errorCode: errorCode);
    }

    private void HideHostWindow()
    {
        if (_hostWindow != nint.Zero)
        {
            X11WindowInterop.HideWindow(_hostWindow);
        }
    }

    private void ShowHostWindowBestEffort()
    {
        if (_hostWindow != nint.Zero)
        {
            X11WindowInterop.ShowWindow(_hostWindow);
        }
    }

    private void EmitPreviewState(string action, string message)
    {
        EmitStateChanged(
            OverlayRuntimeMode.Preview,
            visible: true,
            targetId: "preview",
            action: action,
            message: message,
            usedFallback: true,
            errorCode: PlatformErrorCodes.OverlayPreviewMode);
    }

    private void EmitStateChanged(
        OverlayRuntimeMode mode,
        bool visible,
        string targetId,
        string action,
        string message,
        bool usedFallback,
        string? errorCode)
    {
        try
        {
            OverlayStateChanged?.Invoke(
                this,
                new OverlayStateChangedEvent(
                    mode,
                    visible,
                    targetId,
                    action,
                    message,
                    DateTimeOffset.UtcNow,
                    Capability.Provider,
                    usedFallback,
                    errorCode));
        }
        catch
        {
            // Overlay state consumers must not destabilize native callbacks.
        }
    }

    private static bool TryParseX11TargetId(string targetId, out nint target)
    {
        target = nint.Zero;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        var payload = targetId.StartsWith("x11:", StringComparison.OrdinalIgnoreCase)
            ? targetId[4..]
            : targetId;
        if (payload.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            payload = payload[2..];
        }

        if (!long.TryParse(payload, System.Globalization.NumberStyles.HexNumber, null, out var value)
            && !long.TryParse(payload, out value))
        {
            return false;
        }

        target = (nint)value;
        return target != nint.Zero;
    }

    private sealed class UnsupportedX11WindowEnumerator : IX11WindowEnumerator
    {
        public IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId)
            => throw new InvalidOperationException("X11 native overlay target enumeration is not configured.");
    }

    private sealed class NativeX11WindowEnumerator : IX11WindowEnumerator
    {
        public IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId)
        {
            using var session = X11WindowInterop.OpenSession();
            var root = X11WindowInterop.XDefaultRootWindow(session.Display);
            var windows = ReadWindowList(session.Display, root);
            var targets = new List<OverlayTarget> { CreatePreviewTarget() };
            var seen = new HashSet<long>();

            foreach (var window in windows)
            {
                var windowId = window.ToInt64();
                if (windowId == 0 || !seen.Add(windowId))
                {
                    continue;
                }

                var pid = ReadCardinalProperty(session.Display, window, "_NET_WM_PID");
                if (pid == currentProcessId)
                {
                    continue;
                }

                var title = ReadTextProperty(session.Display, window, "_NET_WM_NAME", preferUtf8: true)
                            ?? ReadTextProperty(session.Display, window, "WM_NAME", preferUtf8: false);
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var windowClass = ReadWindowClass(session.Display, window);
                var processName = ResolveProcessName(pid ?? 0, windowClass);
                var displayName = string.IsNullOrWhiteSpace(processName)
                    ? pid is > 0 ? $"{title} (pid:{pid})" : title.Trim()
                    : pid is > 0 ? $"{title} - {processName} - {pid}" : $"{title} - {processName}";

                targets.Add(new OverlayTarget(
                    $"x11:0x{windowId:X}",
                    displayName,
                    false,
                    NativeHandle: windowId,
                    ProcessId: pid,
                    ProcessName: processName,
                    WindowTitle: title.Trim()));

                if (targets.Count > MaxTargets)
                {
                    break;
                }
            }

            return targets;
        }

        private static IReadOnlyList<nint> ReadWindowList(nint display, nint root)
        {
            var targets = ReadWindowListProperty(display, root, "_NET_CLIENT_LIST_STACKING");
            return targets.Count > 0
                ? targets
                : ReadWindowListProperty(display, root, "_NET_CLIENT_LIST");
        }

        private static IReadOnlyList<nint> ReadWindowListProperty(nint display, nint root, string atomName)
        {
            var property = X11WindowInterop.XInternAtom(display, atomName, X11WindowInterop.False);
            if (property == nint.Zero)
            {
                return [];
            }

            var windowAtom = X11WindowInterop.XInternAtom(display, "WINDOW", X11WindowInterop.False);
            var result = X11WindowInterop.XGetWindowProperty(
                display,
                root,
                property,
                0,
                4096,
                X11WindowInterop.False,
                windowAtom == nint.Zero ? X11WindowInterop.AnyPropertyType : windowAtom,
                out _,
                out var actualFormat,
                out var itemCount,
                out _,
                out var propertyData);
            if (result != 0 || propertyData == nint.Zero)
            {
                return [];
            }

            try
            {
                if (actualFormat != 32 || itemCount.ToInt64() <= 0)
                {
                    return [];
                }

                var windows = new List<nint>();
                var count = itemCount.ToInt64();
                for (var i = 0L; i < count; i++)
                {
                    windows.Add(Marshal.ReadIntPtr(propertyData, checked((int)(i * IntPtr.Size))));
                }

                return windows;
            }
            finally
            {
                X11WindowInterop.XFree(propertyData);
            }
        }

        private static string? ReadTextProperty(nint display, nint window, string atomName, bool preferUtf8)
        {
            var property = X11WindowInterop.XInternAtom(display, atomName, X11WindowInterop.False);
            if (property == nint.Zero)
            {
                return null;
            }

            var utf8Atom = preferUtf8 ? X11WindowInterop.XInternAtom(display, "UTF8_STRING", X11WindowInterop.False) : nint.Zero;
            var requestedType = utf8Atom == nint.Zero ? X11WindowInterop.AnyPropertyType : utf8Atom;
            var value = ReadRawStringProperty(display, window, property, requestedType);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? ReadWindowClass(nint display, nint window)
        {
            var property = X11WindowInterop.XInternAtom(display, "WM_CLASS", X11WindowInterop.False);
            if (property == nint.Zero)
            {
                return null;
            }

            var raw = ReadRawStringProperty(display, window, property, X11WindowInterop.XaString);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? null : parts[^1];
        }

        private static string? ReadRawStringProperty(nint display, nint window, nint property, nint requestedType)
        {
            var result = X11WindowInterop.XGetWindowProperty(
                display,
                window,
                property,
                0,
                4096,
                X11WindowInterop.False,
                requestedType,
                out _,
                out var actualFormat,
                out var itemCount,
                out _,
                out var propertyData);
            if (result != 0 || propertyData == nint.Zero)
            {
                return null;
            }

            try
            {
                if (actualFormat != 8 || itemCount.ToInt64() <= 0)
                {
                    return null;
                }

                var length = checked((int)itemCount.ToInt64());
                var bytes = new byte[length];
                Marshal.Copy(propertyData, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            }
            finally
            {
                X11WindowInterop.XFree(propertyData);
            }
        }

        private static int? ReadCardinalProperty(nint display, nint window, string atomName)
        {
            var property = X11WindowInterop.XInternAtom(display, atomName, X11WindowInterop.False);
            if (property == nint.Zero)
            {
                return null;
            }

            var cardinalAtom = X11WindowInterop.XInternAtom(display, "CARDINAL", X11WindowInterop.False);
            var result = X11WindowInterop.XGetWindowProperty(
                display,
                window,
                property,
                0,
                1,
                X11WindowInterop.False,
                cardinalAtom == nint.Zero ? X11WindowInterop.AnyPropertyType : cardinalAtom,
                out _,
                out var actualFormat,
                out var itemCount,
                out _,
                out var propertyData);
            if (result != 0 || propertyData == nint.Zero)
            {
                return null;
            }

            try
            {
                if (actualFormat != 32 || itemCount.ToInt64() <= 0)
                {
                    return null;
                }

                var raw = Marshal.ReadIntPtr(propertyData).ToInt64();
                return raw > 0 && raw <= int.MaxValue ? (int)raw : null;
            }
            finally
            {
                X11WindowInterop.XFree(propertyData);
            }
        }
    }

    private sealed class X11WindowInterop
    {
        internal const int False = 0;
        internal const int AnyPropertyType = 0;
        internal const int XaString = 31;
        private const int IsViewable = 2;
        private const int ShapeSet = 0;
        private const int ShapeInput = 2;

        internal readonly record struct X11Bounds(int X, int Y, int Width, int Height);

        internal static X11Bounds Inset(X11Bounds bounds, int dx, int dy)
        {
            return new X11Bounds(
                bounds.X + dx,
                bounds.Y + dy,
                Math.Max(1, bounds.Width - dx),
                Math.Max(1, bounds.Height - dy));
        }

        internal static X11Session OpenSession()
        {
            var display = XOpenDisplay(null);
            if (display == nint.Zero)
            {
                throw new InvalidOperationException("XOpenDisplay returned null.");
            }

            return new X11Session(display);
        }

        internal static bool IsWindow(nint window)
        {
            if (window == nint.Zero)
            {
                return false;
            }

            try
            {
                using var session = OpenSession();
                return XGetWindowAttributes(session.Display, window, out _) != 0;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryGetAbsoluteBounds(nint window, out X11Bounds bounds)
        {
            bounds = default;
            try
            {
                using var session = OpenSession();
                if (XGetWindowAttributes(session.Display, window, out var attributes) == 0)
                {
                    return false;
                }

                if (attributes.MapState != IsViewable)
                {
                    return false;
                }

                var root = XDefaultRootWindow(session.Display);
                if (XTranslateCoordinates(
                        session.Display,
                        window,
                        root,
                        0,
                        0,
                        out var rootX,
                        out var rootY,
                        out _) == 0)
                {
                    return false;
                }

                bounds = new X11Bounds(
                    rootX,
                    rootY,
                    Math.Max(1, attributes.Width),
                    Math.Max(1, attributes.Height));
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryMoveResizeAndRaise(nint window, X11Bounds bounds)
        {
            try
            {
                using var session = OpenSession();
                if (XMoveResizeWindow(session.Display, window, bounds.X, bounds.Y, (uint)bounds.Width, (uint)bounds.Height) == 0)
                {
                    return false;
                }

                _ = XMapRaised(session.Display, window);
                _ = XRaiseWindow(session.Display, window);
                _ = XFlush(session.Display);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TrySetInputPassthrough(nint window)
        {
            try
            {
                using var session = OpenSession();
                XShapeCombineRectangles(
                    session.Display,
                    window,
                    ShapeInput,
                    0,
                    0,
                    nint.Zero,
                    0,
                    ShapeSet,
                    0);
                _ = XFlush(session.Display);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void HideWindow(nint window)
        {
            try
            {
                using var session = OpenSession();
                _ = XUnmapWindow(session.Display, window);
                _ = XFlush(session.Display);
            }
            catch
            {
                // Best effort during shutdown/fallback transitions.
            }
        }

        internal static void ShowWindow(nint window)
        {
            try
            {
                using var session = OpenSession();
                _ = XMapRaised(session.Display, window);
                _ = XFlush(session.Display);
            }
            catch
            {
                // Best effort during preview/fallback transitions.
            }
        }

        [DllImport("libX11.so.6")]
        internal static extern nint XDefaultRootWindow(nint display);

        [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
        internal static extern nint XInternAtom(nint display, string atomName, int onlyIfExists);

        [DllImport("libX11.so.6")]
        internal static extern int XGetWindowProperty(
            nint display,
            nint window,
            nint property,
            nint longOffset,
            nint longLength,
            int delete,
            nint requestedType,
            out nint actualType,
            out int actualFormat,
            out nint itemCount,
            out nint bytesAfter,
            out nint propertyData);

        [DllImport("libX11.so.6")]
        internal static extern int XFree(nint data);

        [DllImport("libX11.so.6")]
        private static extern nint XOpenDisplay(string? displayName);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(nint display);

        [DllImport("libX11.so.6")]
        private static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);

        [DllImport("libX11.so.6")]
        private static extern int XTranslateCoordinates(
            nint display,
            nint sourceWindow,
            nint destinationWindow,
            int sourceX,
            int sourceY,
            out int destinationX,
            out int destinationY,
            out nint child);

        [DllImport("libX11.so.6")]
        private static extern int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);

        [DllImport("libX11.so.6")]
        private static extern int XMapRaised(nint display, nint window);

        [DllImport("libX11.so.6")]
        private static extern int XRaiseWindow(nint display, nint window);

        [DllImport("libX11.so.6")]
        private static extern int XUnmapWindow(nint display, nint window);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(nint display);

        [DllImport("libXext.so.6")]
        private static extern void XShapeCombineRectangles(
            nint display,
            nint destinationWindow,
            int destinationKind,
            int xOffset,
            int yOffset,
            nint rectangles,
            int rectangleCount,
            int operation,
            int ordering);

        [StructLayout(LayoutKind.Sequential)]
        private struct XWindowAttributes
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public int BorderWidth;
            public int Depth;
            public nint Visual;
            public nint Root;
            public int Class;
            public int BitGravity;
            public int WinGravity;
            public int BackingStore;
            public nint BackingPlanes;
            public nint BackingPixel;
            public int SaveUnder;
            public nint Colormap;
            public int MapInstalled;
            public int MapState;
            public nint AllEventMasks;
            public nint YourEventMask;
            public nint DoNotPropagateMask;
            public int OverrideRedirect;
            public nint Screen;
        }

        internal sealed class X11Session : IDisposable
        {
            public X11Session(nint display)
            {
                Display = display;
            }

            public nint Display { get; }

            public void Dispose()
            {
                _ = XCloseDisplay(Display);
            }
        }
    }

    private sealed class ProcessCommandRunner : ICommandRunner
    {
        public async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new CommandResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
    }
}

public sealed class CommandPostActionExecutorService : IPostActionExecutorService
{
    public CommandPostActionExecutorService()
    {
        CapabilityMatrix = GetCapabilityMatrix();
    }

    public PostActionCapabilityMatrix CapabilityMatrix { get; }

    public PostActionCapabilityMatrix GetCapabilityMatrix(PostActionExecutorRequest? request = null)
        => PostActionExecutorSupport.BuildCapabilityMatrix(request);

    public async Task<PlatformOperationResult> ExecuteAsync(
        PostActionType action,
        PostActionExecutorRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capability = GetCapabilityMatrix(request).Get(action);
        if (!capability.Supported)
        {
            return PlatformOperation.Failed(
                capability.Provider,
                capability.Message,
                PlatformErrorCodes.PostActionUnsupported,
                operationId: $"post-action.{action}",
                usedFallback: capability.HasFallback);
        }

        return action switch
        {
            PostActionType.ExitArknights => PlatformOperation.Failed(
                "maa-core",
                "Exit Arknights requires MaaCore native provider.",
                PlatformErrorCodes.PostActionUnsupported,
                $"post-action.{action}"),
            PostActionType.BackToAndroidHome => PlatformOperation.Failed(
                "maa-core",
                "Back to Android home requires MaaCore native provider.",
                PlatformErrorCodes.PostActionUnsupported,
                $"post-action.{action}"),
            PostActionType.ExitEmulator => await PostActionExecutorSupport.ExecuteExitEmulatorAsync(request, cancellationToken),
            PostActionType.ExitSelf => PlatformOperation.Failed(
                "app-lifecycle",
                "Exit MAA requires app lifecycle native provider.",
                PlatformErrorCodes.PostActionUnsupported,
                $"post-action.{action}"),
            PostActionType.Hibernate => await PostActionExecutorSupport.ExecutePowerActionAsync(action, cancellationToken),
            PostActionType.Shutdown => await PostActionExecutorSupport.ExecutePowerActionAsync(action, cancellationToken),
            PostActionType.Sleep => await PostActionExecutorSupport.ExecutePowerActionAsync(action, cancellationToken),
            _ => PlatformOperation.Failed("post-action", "Unknown post action.", PlatformErrorCodes.PostActionExecutionFailed, $"post-action.{action}"),
        };
    }
}
