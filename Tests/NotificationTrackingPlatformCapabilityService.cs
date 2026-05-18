using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

internal sealed class NotificationTrackingPlatformCapabilityService : IPlatformCapabilityService
{
    private readonly IPlatformCapabilityService _inner;

    public NotificationTrackingPlatformCapabilityService(IPlatformCapabilityService inner)
    {
        _inner = inner;
    }

    public int NotificationCallCount { get; private set; }

    public string? LastTitle { get; private set; }

    public string? LastMessage { get; private set; }

    public event EventHandler<TrayCommandEvent>? TrayCommandInvoked
    {
        add => _inner.TrayCommandInvoked += value;
        remove => _inner.TrayCommandInvoked -= value;
    }

    public event EventHandler<TrayMenuRequestEvent>? TrayMenuRequested
    {
        add => _inner.TrayMenuRequested += value;
        remove => _inner.TrayMenuRequested -= value;
    }

    public event EventHandler<GlobalHotkeyTriggeredEvent>? GlobalHotkeyTriggered
    {
        add => _inner.GlobalHotkeyTriggered += value;
        remove => _inner.GlobalHotkeyTriggered -= value;
    }

    public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged
    {
        add => _inner.OverlayStateChanged += value;
        remove => _inner.OverlayStateChanged -= value;
    }

    public Task<UiOperationResult<PlatformCapabilitySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => _inner.GetSnapshotAsync(cancellationToken);

    public Task<UiOperationResult> InitializeTrayAsync(string appTitle, TrayMenuText? menuText, CancellationToken cancellationToken = default)
        => _inner.InitializeTrayAsync(appTitle, menuText, cancellationToken);

    public Task<UiOperationResult> ShutdownTrayAsync(CancellationToken cancellationToken = default)
        => _inner.ShutdownTrayAsync(cancellationToken);

    public Task<UiOperationResult> ShowTrayMessageAsync(string title, string message, CancellationToken cancellationToken = default)
        => _inner.ShowTrayMessageAsync(title, message, cancellationToken);

    public Task<UiOperationResult> SetTrayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => _inner.SetTrayVisibleAsync(visible, cancellationToken);

    public Task<UiOperationResult> SetTrayMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
        => _inner.SetTrayMenuStateAsync(state, cancellationToken);

    public Task<UiOperationResult> SendSystemNotificationAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        NotificationCallCount++;
        LastTitle = title;
        LastMessage = message;
        return _inner.SendSystemNotificationAsync(title, message, cancellationToken);
    }

    public Task<UiOperationResult> RegisterGlobalHotkeyAsync(string name, string gesture, CancellationToken cancellationToken = default)
        => _inner.RegisterGlobalHotkeyAsync(name, gesture, cancellationToken);

    public Task<UiOperationResult<IReadOnlyList<HotkeyRegistrationOutcome>>> RegisterGlobalHotkeysAsync(IReadOnlyList<HotkeyBindingRequest> requests, CancellationToken cancellationToken = default)
        => _inner.RegisterGlobalHotkeysAsync(requests, cancellationToken);

    public Task<UiOperationResult> UnregisterGlobalHotkeyAsync(string name, CancellationToken cancellationToken = default)
        => _inner.UnregisterGlobalHotkeyAsync(name, cancellationToken);

    public Task<UiOperationResult> ConfigureHotkeyHostContextAsync(HotkeyHostContext context, CancellationToken cancellationToken = default)
        => _inner.ConfigureHotkeyHostContextAsync(context, cancellationToken);

    public bool TryDispatchWindowScopedHotkey(HotkeyGesture gesture)
        => _inner.TryDispatchWindowScopedHotkey(gesture);

    public Task<UiOperationResult<bool>> GetAutostartEnabledAsync(CancellationToken cancellationToken = default)
        => _inner.GetAutostartEnabledAsync(cancellationToken);

    public Task<UiOperationResult> SetAutostartEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => _inner.SetAutostartEnabledAsync(enabled, cancellationToken);

    public Task<UiOperationResult> BindOverlayHostAsync(nint hostWindowHandle, bool clickThrough, double opacity, CancellationToken cancellationToken = default)
        => _inner.BindOverlayHostAsync(hostWindowHandle, clickThrough, opacity, cancellationToken);

    public Task<UiOperationResult<IReadOnlyList<OverlayTarget>>> QueryOverlayTargetsAsync(CancellationToken cancellationToken = default)
        => _inner.QueryOverlayTargetsAsync(cancellationToken);

    public Task<UiOperationResult> SelectOverlayTargetAsync(string targetId, CancellationToken cancellationToken = default)
        => _inner.SelectOverlayTargetAsync(targetId, cancellationToken);

    public Task<UiOperationResult> SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => _inner.SetOverlayVisibleAsync(visible, cancellationToken);
}
