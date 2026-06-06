using Avalonia;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MAAUnified.Platform;

public sealed class MacOverlayCapabilityService : IOverlayCapabilityService
{
    private const int MaxTargets = 80;
    private const string PreviewTargetId = "preview";
    private readonly IMacWindowEnumerator _windowEnumerator;
    private readonly IMacOverlayHostConfigurator _hostConfigurator;
    private string _selectedTargetId = PreviewTargetId;
    private nint _selectedTarget;
    private nint _hostWindow;
    private bool _visible;

    public MacOverlayCapabilityService()
        : this(new NativeMacWindowEnumerator(), new NativeMacOverlayHostConfigurator())
    {
    }

    internal MacOverlayCapabilityService(IMacWindowEnumerator windowEnumerator)
        : this(windowEnumerator, NoOpMacOverlayHostConfigurator.Instance)
    {
    }

    internal MacOverlayCapabilityService(
        IMacWindowEnumerator windowEnumerator,
        IMacOverlayHostConfigurator hostConfigurator)
    {
        _windowEnumerator = windowEnumerator;
        _hostConfigurator = hostConfigurator;
    }

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "macOS overlay target discovery is available via CoreGraphics; native attachment falls back to preview.",
        Provider: "macos-coregraphics-overlay",
        HasFallback: true,
        FallbackMode: "preview-and-log");

    public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged;

    public static bool TryCreate([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MacOverlayCapabilityService? service)
    {
        service = null;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        service = new MacOverlayCapabilityService();
        return true;
    }

    public static bool TryGetTargetBounds(string targetId, out PixelRect bounds)
    {
        bounds = default;
        if (!OperatingSystem.IsMacOS() || !TryParseMacTargetId(targetId, out var target))
        {
            return false;
        }

        return NativeMacWindowEnumerator.TryGetTargetBounds(target, out bounds);
    }

    public Task<PlatformOperationResult> BindHostWindowAsync(
        nint hostWindowHandle,
        bool clickThrough,
        double opacity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (hostWindowHandle == nint.Zero)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "macOS overlay host window handle is invalid.",
                PlatformErrorCodes.OverlayHostNotBound,
                "overlay.bindHost"));
        }

        _hostWindow = hostWindowHandle;
        if (!_hostConfigurator.Configure(_hostWindow, clickThrough, opacity, out var configureMessage))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                configureMessage,
                PlatformErrorCodes.OverlayAttachFailed,
                "overlay.bindHost"));
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            configureMessage,
            "overlay.bindHost"));
    }

    public Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OverlayTarget> targets;
        try
        {
            targets = EnsurePreviewTarget(_windowEnumerator.EnumerateTargets(Environment.ProcessId));
        }
        catch (Exception ex)
        {
            targets = [CreatePreviewTarget()];
            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                targets,
                $"macOS overlay target query fell back to preview mode: {ex.Message}",
                "overlay.query-targets",
                PlatformErrorCodes.OverlayPreviewMode));
        }

        if (targets.Count <= 1)
        {
            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                targets,
                "No macOS overlay target is available, switched to preview mode.",
                "overlay.query-targets",
                PlatformErrorCodes.OverlayPreviewMode));
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            targets,
            $"Found {targets.Count - 1} macOS overlay target(s).",
            "overlay.query-targets"));
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

        if (string.Equals(targetId, PreviewTargetId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedTarget = nint.Zero;
            _selectedTargetId = PreviewTargetId;
            if (_visible)
            {
                EmitPreviewState(PreviewTargetId, "fallback-enter", "Overlay switched to Preview + Logs mode.");
            }

            return Task.FromResult(PlatformOperation.FallbackSuccess(
                Capability.Provider,
                "Overlay target switched to preview mode.",
                "overlay.selectTarget",
                PlatformErrorCodes.OverlayPreviewMode));
        }

        if (!TryParseMacTargetId(targetId, out var target))
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                $"Invalid macOS overlay target id: {targetId}",
                PlatformErrorCodes.OverlayTargetInvalid,
                "overlay.selectTarget"));
        }

        _selectedTarget = target;
        _selectedTargetId = targetId;
        if (_visible)
        {
            EmitPreviewState(
                _selectedTargetId,
                "target-change",
                "macOS overlay target selected; showing Preview + Logs mode.");
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"macOS overlay target selected: {targetId}",
            "overlay.selectTarget"));
    }

    public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!visible)
        {
            _visible = false;
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

        if (_hostWindow == nint.Zero)
        {
            return Task.FromResult(PlatformOperation.Failed(
                Capability.Provider,
                "macOS overlay host is unavailable; switched to Preview + Logs mode.",
                PlatformErrorCodes.OverlayHostNotBound,
                "overlay.setVisible"));
        }

        _visible = true;
        var targetId = _selectedTarget == nint.Zero ? PreviewTargetId : _selectedTargetId;
        EmitPreviewState(
            targetId,
            "fallback-enter",
            _selectedTarget == nint.Zero
                ? "Overlay switched to Preview + Logs mode because no native target is selected."
                : "macOS overlay target is selected; native attachment falls back to Preview + Logs mode.");
        return Task.FromResult(PlatformOperation.FallbackSuccess(
            Capability.Provider,
            "Overlay switched to preview mode on macOS.",
            "overlay.setVisible",
            PlatformErrorCodes.OverlayPreviewMode));
    }

    internal interface IMacWindowEnumerator
    {
        IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId);
    }

    internal interface IMacOverlayHostConfigurator
    {
        bool Configure(nint hostWindowHandle, bool clickThrough, double opacity, out string message);
    }

    private sealed class NoOpMacOverlayHostConfigurator : IMacOverlayHostConfigurator
    {
        public static readonly NoOpMacOverlayHostConfigurator Instance = new();

        private NoOpMacOverlayHostConfigurator()
        {
        }

        public bool Configure(nint hostWindowHandle, bool clickThrough, double opacity, out string message)
        {
            message = "macOS overlay host window bound.";
            return true;
        }
    }

    private sealed class NativeMacOverlayHostConfigurator : IMacOverlayHostConfigurator
    {
        public bool Configure(nint hostWindowHandle, bool clickThrough, double opacity, out string message)
        {
            try
            {
                MacAppKitInterop.SetIgnoresMouseEvents(hostWindowHandle, clickThrough);
                message = clickThrough
                    ? "macOS overlay host window bound with click-through enabled."
                    : "macOS overlay host window bound.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Failed to configure macOS overlay click-through: {ex.Message}";
                return false;
            }
        }
    }

    private static IReadOnlyList<OverlayTarget> EnsurePreviewTarget(IReadOnlyList<OverlayTarget> targets)
    {
        if (targets.Any(static target => string.Equals(target.Id, PreviewTargetId, StringComparison.OrdinalIgnoreCase)))
        {
            return targets;
        }

        var copy = new List<OverlayTarget>(targets.Count + 1) { CreatePreviewTarget() };
        copy.AddRange(targets);
        return copy;
    }

    private static OverlayTarget CreatePreviewTarget()
        => new(PreviewTargetId, "Preview + Logs", true);

    private void EmitPreviewState(string targetId, string action, string message)
    {
        EmitStateChanged(
            OverlayRuntimeMode.Preview,
            visible: true,
            targetId: targetId,
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
            // Overlay state consumers must not destabilize platform callbacks.
        }
    }

    private static bool TryParseMacTargetId(string targetId, out nint target)
    {
        target = nint.Zero;
        var payload = targetId.StartsWith("mac:", StringComparison.OrdinalIgnoreCase)
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

    private sealed class NativeMacWindowEnumerator : IMacWindowEnumerator
    {
        public static bool TryGetTargetBounds(nint target, out PixelRect bounds)
        {
            bounds = default;
            if (target == nint.Zero)
            {
                return false;
            }

            var windowInfoArray = MacCoreGraphicsInterop.CGWindowListCopyWindowInfo(
                MacCoreGraphicsInterop.CGWindowListExcludeDesktopElements,
                0);
            if (windowInfoArray == nint.Zero)
            {
                return false;
            }

            using var keys = new WindowInfoKeys();
            try
            {
                var count = MacCoreFoundationInterop.CFArrayGetCount(windowInfoArray);
                for (var i = 0L; i < count; i++)
                {
                    var windowInfo = MacCoreFoundationInterop.CFArrayGetValueAtIndex(windowInfoArray, i);
                    if (windowInfo == nint.Zero
                        || !TryReadInt64(windowInfo, keys.WindowNumber, out var windowId)
                        || windowId != (long)target
                        || !TryReadPixelRect(windowInfo, keys, out bounds))
                    {
                        continue;
                    }

                    return bounds.Width > 0 && bounds.Height > 0;
                }
            }
            finally
            {
                MacCoreFoundationInterop.CFRelease(windowInfoArray);
            }

            bounds = default;
            return false;
        }

        public IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId)
        {
            var onScreenTargets = EnumerateTargets(
                currentProcessId,
                MacCoreGraphicsInterop.CGWindowListOptionOnScreenOnly
                | MacCoreGraphicsInterop.CGWindowListExcludeDesktopElements);
            if (onScreenTargets.Count > 1)
            {
                return onScreenTargets;
            }

            var relaxedTargets = EnumerateTargets(
                currentProcessId,
                MacCoreGraphicsInterop.CGWindowListExcludeDesktopElements);
            return MergeTargets(onScreenTargets, relaxedTargets);
        }

        private static IReadOnlyList<OverlayTarget> EnumerateTargets(int currentProcessId, uint options)
        {
            var windowInfoArray = MacCoreGraphicsInterop.CGWindowListCopyWindowInfo(options, 0);
            if (windowInfoArray == nint.Zero)
            {
                return [CreatePreviewTarget()];
            }

            using var keys = new WindowInfoKeys();
            var targets = new List<OverlayTarget> { CreatePreviewTarget() };
            var seen = new HashSet<long>();
            try
            {
                var count = MacCoreFoundationInterop.CFArrayGetCount(windowInfoArray);
                for (var i = 0L; i < count && targets.Count < MaxTargets; i++)
                {
                    var windowInfo = MacCoreFoundationInterop.CFArrayGetValueAtIndex(windowInfoArray, i);
                    if (windowInfo == nint.Zero
                        || !TryReadInt64(windowInfo, keys.WindowNumber, out var windowId)
                        || windowId <= 0
                        || !seen.Add(windowId)
                        || !TryReadInt64(windowInfo, keys.OwnerPid, out var ownerPid)
                        || ownerPid == currentProcessId
                        || !TryReadInt64(windowInfo, keys.Layer, out var layer)
                        || layer != 0)
                    {
                        continue;
                    }

                    var windowTitle = TryReadString(windowInfo, keys.WindowName);
                    var ownerName = TryReadString(windowInfo, keys.OwnerName);
                    var displayTitle = string.IsNullOrWhiteSpace(windowTitle) ? ownerName : windowTitle;
                    if (string.IsNullOrWhiteSpace(displayTitle))
                    {
                        continue;
                    }

                    var processName = ResolveProcessName((int)ownerPid, ownerName);
                    var displayName = string.IsNullOrWhiteSpace(processName)
                        ? $"{displayTitle.Trim()} (pid:{ownerPid})"
                        : $"{displayTitle.Trim()} - {processName} - {ownerPid}";

                    targets.Add(new OverlayTarget(
                        $"mac:0x{windowId:X}",
                        displayName,
                        false,
                        NativeHandle: windowId,
                        ProcessId: (int)ownerPid,
                        ProcessName: processName,
                        WindowTitle: displayTitle.Trim()));
                }
            }
            finally
            {
                MacCoreFoundationInterop.CFRelease(windowInfoArray);
            }

            return targets;
        }

        private static IReadOnlyList<OverlayTarget> MergeTargets(
            IReadOnlyList<OverlayTarget> primary,
            IReadOnlyList<OverlayTarget> fallback)
        {
            if (fallback.Count <= 1)
            {
                return primary;
            }

            var merged = new List<OverlayTarget>(Math.Max(primary.Count, fallback.Count));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in primary.Concat(fallback))
            {
                if (seen.Add(target.Id))
                {
                    merged.Add(target);
                }
            }

            return merged;
        }

        private static bool TryReadInt64(nint dictionary, nint key, out long value)
        {
            value = 0;
            var cfValue = MacCoreFoundationInterop.CFDictionaryGetValue(dictionary, key);
            return cfValue != nint.Zero
                   && MacCoreFoundationInterop.CFNumberGetValue(
                       cfValue,
                       MacCoreFoundationInterop.KCFNumberSInt64Type,
                       out value);
        }

        private static string? TryReadString(nint dictionary, nint key)
        {
            var value = MacCoreFoundationInterop.CFDictionaryGetValue(dictionary, key);
            return value == nint.Zero ? null : MacCoreFoundationInterop.ReadString(value);
        }

        private static bool TryReadPixelRect(nint dictionary, WindowInfoKeys keys, out PixelRect bounds)
        {
            bounds = default;
            var value = MacCoreFoundationInterop.CFDictionaryGetValue(dictionary, keys.Bounds);
            if (value == nint.Zero
                || !TryReadDouble(value, keys.BoundsX, out var x)
                || !TryReadDouble(value, keys.BoundsY, out var y)
                || !TryReadDouble(value, keys.BoundsWidth, out var width)
                || !TryReadDouble(value, keys.BoundsHeight, out var height))
            {
                return false;
            }

            var roundedX = (int)Math.Round(x);
            var roundedY = (int)Math.Round(y);
            var roundedWidth = Math.Max(0, (int)Math.Round(width));
            var roundedHeight = Math.Max(0, (int)Math.Round(height));
            if (roundedWidth <= 0 || roundedHeight <= 0)
            {
                return false;
            }

            bounds = new PixelRect(roundedX, roundedY, roundedWidth, roundedHeight);
            return true;
        }

        private static bool TryReadDouble(nint dictionary, nint key, out double value)
        {
            value = 0d;
            var cfValue = MacCoreFoundationInterop.CFDictionaryGetValue(dictionary, key);
            return cfValue != nint.Zero
                   && MacCoreFoundationInterop.CFNumberGetValue(
                       cfValue,
                       MacCoreFoundationInterop.KCFNumberFloat64Type,
                       out value);
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

            return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
        }
    }

    private sealed class WindowInfoKeys : IDisposable
    {
        public WindowInfoKeys()
        {
            WindowNumber = MacCoreFoundationInterop.CreateString("kCGWindowNumber");
            WindowName = MacCoreFoundationInterop.CreateString("kCGWindowName");
            OwnerPid = MacCoreFoundationInterop.CreateString("kCGWindowOwnerPID");
            OwnerName = MacCoreFoundationInterop.CreateString("kCGWindowOwnerName");
            Layer = MacCoreFoundationInterop.CreateString("kCGWindowLayer");
            Bounds = MacCoreFoundationInterop.CreateString("kCGWindowBounds");
            BoundsX = MacCoreFoundationInterop.CreateString("X");
            BoundsY = MacCoreFoundationInterop.CreateString("Y");
            BoundsWidth = MacCoreFoundationInterop.CreateString("Width");
            BoundsHeight = MacCoreFoundationInterop.CreateString("Height");
        }

        public nint WindowNumber { get; }

        public nint WindowName { get; }

        public nint OwnerPid { get; }

        public nint OwnerName { get; }

        public nint Layer { get; }

        public nint Bounds { get; }

        public nint BoundsX { get; }

        public nint BoundsY { get; }

        public nint BoundsWidth { get; }

        public nint BoundsHeight { get; }

        public void Dispose()
        {
            Release(WindowNumber);
            Release(WindowName);
            Release(OwnerPid);
            Release(OwnerName);
            Release(Layer);
            Release(Bounds);
            Release(BoundsX);
            Release(BoundsY);
            Release(BoundsWidth);
            Release(BoundsHeight);
        }

        private static void Release(nint value)
        {
            if (value != nint.Zero)
            {
                MacCoreFoundationInterop.CFRelease(value);
            }
        }
    }

    private static class MacCoreGraphicsInterop
    {
        public const uint CGWindowListOptionOnScreenOnly = 1;
        public const uint CGWindowListExcludeDesktopElements = 16;
        private const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreGraphicsLibrary)]
        public static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    }

    private static class MacAppKitInterop
    {
        private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
        private static readonly nint SetIgnoresMouseEventsSelector = sel_registerName("setIgnoresMouseEvents:");

        public static void SetIgnoresMouseEvents(nint nsWindow, bool ignoresMouseEvents)
        {
            if (nsWindow == nint.Zero)
            {
                return;
            }

            objc_msgSend(nsWindow, SetIgnoresMouseEventsSelector, ignoresMouseEvents);
        }

        [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
        private static extern nint sel_registerName(string selectorName);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(
            nint receiver,
            nint selector,
            [MarshalAs(UnmanagedType.I1)] bool value);
    }

    private static class MacCoreFoundationInterop
    {
        public const int KCFNumberSInt64Type = 4;
        public const int KCFNumberFloat64Type = 6;
        private const uint KCFStringEncodingUtf8 = 0x08000100;
        private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CoreFoundationLibrary)]
        public static extern long CFArrayGetCount(nint array);

        [DllImport(CoreFoundationLibrary)]
        public static extern nint CFArrayGetValueAtIndex(nint array, long index);

        [DllImport(CoreFoundationLibrary)]
        public static extern nint CFDictionaryGetValue(nint dictionary, nint key);

        [DllImport(CoreFoundationLibrary)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CFNumberGetValue(nint number, int type, out long value);

        [DllImport(CoreFoundationLibrary, EntryPoint = "CFNumberGetValue")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CFNumberGetValue(nint number, int type, out double value);

        [DllImport(CoreFoundationLibrary)]
        public static extern long CFStringGetLength(nint theString);

        [DllImport(CoreFoundationLibrary)]
        public static extern long CFStringGetMaximumSizeForEncoding(long length, uint encoding);

        [DllImport(CoreFoundationLibrary)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CFStringGetCString(nint theString, byte[] buffer, long bufferSize, uint encoding);

        [DllImport(CoreFoundationLibrary)]
        public static extern void CFRelease(nint cf);

        [DllImport(CoreFoundationLibrary)]
        private static extern nint CFStringCreateWithCString(
            nint allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr,
            uint encoding);

        public static nint CreateString(string value)
            => CFStringCreateWithCString(nint.Zero, value, KCFStringEncodingUtf8);

        public static string? ReadString(nint cfString)
        {
            var length = CFStringGetLength(cfString);
            if (length <= 0)
            {
                return null;
            }

            var maxSize = CFStringGetMaximumSizeForEncoding(length, KCFStringEncodingUtf8) + 1;
            if (maxSize <= 1 || maxSize > int.MaxValue)
            {
                return null;
            }

            var buffer = new byte[maxSize];
            if (!CFStringGetCString(cfString, buffer, maxSize, KCFStringEncodingUtf8))
            {
                return null;
            }

            var byteCount = Array.IndexOf(buffer, (byte)0);
            if (byteCount < 0)
            {
                byteCount = buffer.Length;
            }

            return byteCount == 0 ? null : Encoding.UTF8.GetString(buffer, 0, byteCount);
        }
    }
}
