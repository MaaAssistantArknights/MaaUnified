using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace MAAUnified.Platform;

internal interface IMacCarbonHotkeyInterop
{
    nint GetApplicationEventTarget();

    int InstallEventHandler(
        nint target,
        MacCarbonEventHandler handler,
        CarbonEventTypeSpec[] eventTypes,
        nint userData,
        out nint handlerRef);

    int RemoveEventHandler(nint handlerRef);

    int RegisterEventHotKey(
        uint keyCode,
        uint modifiers,
        CarbonEventHotKeyId hotKeyId,
        nint target,
        out nint hotKeyRef);

    int UnregisterEventHotKey(nint hotKeyRef);

    int GetEventHotKeyId(nint eventRef, out uint hotKeyId);
}

internal delegate int MacCarbonEventHandler(nint nextHandler, nint eventRef, nint userData);

internal struct CarbonEventTypeSpec
{
    public CarbonEventTypeSpec(uint eventClass, uint eventKind)
    {
        EventClass = eventClass;
        EventKind = eventKind;
    }

    public uint EventClass;

    public uint EventKind;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CarbonEventHotKeyId
{
    public CarbonEventHotKeyId(uint signature, uint id)
    {
        Signature = signature;
        Id = id;
    }

    public uint Signature;

    public uint Id;
}

internal sealed class MacCarbonNativeHotkeyInterop : IMacCarbonHotkeyInterop
{
    private const string CarbonFramework = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint EventParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;

    public nint GetApplicationEventTarget() => NativeMethods.GetApplicationEventTarget();

    public int InstallEventHandler(
        nint target,
        MacCarbonEventHandler handler,
        CarbonEventTypeSpec[] eventTypes,
        nint userData,
        out nint handlerRef)
    {
        return NativeMethods.InstallEventHandler(
            target,
            handler,
            (uint)eventTypes.Length,
            eventTypes,
            userData,
            out handlerRef);
    }

    public int RemoveEventHandler(nint handlerRef) => NativeMethods.RemoveEventHandler(handlerRef);

    public int RegisterEventHotKey(
        uint keyCode,
        uint modifiers,
        CarbonEventHotKeyId hotKeyId,
        nint target,
        out nint hotKeyRef)
    {
        return NativeMethods.RegisterEventHotKey(keyCode, modifiers, hotKeyId, target, 0, out hotKeyRef);
    }

    public int UnregisterEventHotKey(nint hotKeyRef) => NativeMethods.UnregisterEventHotKey(hotKeyRef);

    public int GetEventHotKeyId(nint eventRef, out uint hotKeyId)
    {
        var size = (uint)Marshal.SizeOf<CarbonEventHotKeyId>();
        var status = NativeMethods.GetEventParameter(
            eventRef,
            EventParamDirectObject,
            TypeEventHotKeyId,
            nint.Zero,
            size,
            out _,
            out var value);
        hotKeyId = value.Id;
        return status;
    }

    private static class NativeMethods
    {
        [DllImport(CarbonFramework)]
        public static extern nint GetApplicationEventTarget();

        [DllImport(CarbonFramework)]
        public static extern int InstallEventHandler(
            nint target,
            MacCarbonEventHandler handler,
            uint eventTypeCount,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] CarbonEventTypeSpec[] eventTypes,
            nint userData,
            out nint handlerRef);

        [DllImport(CarbonFramework)]
        public static extern int RemoveEventHandler(nint handlerRef);

        [DllImport(CarbonFramework)]
        public static extern int RegisterEventHotKey(
            uint keyCode,
            uint modifiers,
            CarbonEventHotKeyId hotKeyId,
            nint target,
            uint options,
            out nint hotKeyRef);

        [DllImport(CarbonFramework)]
        public static extern int UnregisterEventHotKey(nint hotKeyRef);

        [DllImport(CarbonFramework)]
        public static extern int GetEventParameter(
            nint eventRef,
            uint name,
            uint desiredType,
            nint actualType,
            uint bufferSize,
            out uint actualSize,
            out CarbonEventHotKeyId data);
    }
}

internal sealed class MacCarbonGlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 6;
    private const uint HotKeySignature = 0x4D414155; // MAAU
    private const uint CmdKey = 1u << 8;
    private const uint ShiftKey = 1u << 9;
    private const uint OptionKey = 1u << 11;
    private const uint ControlKey = 1u << 12;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, RegisteredMacCarbonHotkey> _registeredByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _registeredByChord = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, RegisteredMacCarbonHotkey> _registeredByHotKeyId = new();
    private readonly IMacCarbonHotkeyInterop _interop;
    private readonly MacCarbonEventHandler _eventHandler;
    private readonly CarbonEventTypeSpec[] _eventTypes = [new(EventClassKeyboard, EventHotKeyPressed)];
    private HotkeyHostContext _hostContext = new(nint.Zero, string.Empty, "unknown");
    private nint _handlerRef;
    private bool _disposed;
    private uint _nextHotKeyId = 1;

    public MacCarbonGlobalHotkeyService()
        : this(new MacCarbonNativeHotkeyInterop())
    {
    }

    internal MacCarbonGlobalHotkeyService(IMacCarbonHotkeyInterop interop)
    {
        _interop = interop;
        _eventHandler = HandleCarbonHotKeyPressed;
    }

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "Global hotkeys are available via macOS Carbon RegisterEventHotKey backend.",
        Provider: "mac-carbon",
        HasFallback: true,
        FallbackMode: "window-scoped");

    public event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

    public static bool TryCreate([NotNullWhen(true)] out MacCarbonGlobalHotkeyService? service)
    {
        service = null;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        service = new MacCarbonGlobalHotkeyService();
        return true;
    }

    public Task<PlatformOperationResult> RegisterAsync(string name, string gesture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThreadAsync(() => RegisterCore(name, gesture), cancellationToken);
    }

    public Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThreadAsync(() => UnregisterCore(name), cancellationToken);
    }

    public Task<PlatformOperationResult> ConfigureHostContextAsync(
        HotkeyHostContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            _hostContext = context;
        }

        return Task.FromResult(PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Captured macOS hotkey host context for session={context.SessionType}.",
            "hotkey.configure-host"));
    }

    public bool TryGetRegisteredHotkey(string name, out RegisteredHotkeyState state)
    {
        lock (_syncRoot)
        {
            if (_registeredByName.TryGetValue(name, out var registered))
            {
                state = new RegisteredHotkeyState(
                    registered.Name,
                    registered.NormalizedGesture,
                    registered.DisplayGesture,
                    Capability.Provider,
                    PlatformExecutionMode.Native);
                return true;
            }
        }

        state = default!;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_syncRoot)
        {
            foreach (var registered in _registeredByName.Values)
            {
                try
                {
                    _ = _interop.UnregisterEventHotKey(registered.NativeHotKeyRef);
                }
                catch
                {
                    // ignored
                }
            }

            _registeredByName.Clear();
            _registeredByChord.Clear();
            _registeredByHotKeyId.Clear();

            if (_handlerRef != nint.Zero)
            {
                try
                {
                    _ = _interop.RemoveEventHandler(_handlerRef);
                }
                catch
                {
                    // ignored
                }

                _handlerRef = nint.Zero;
            }
        }
    }

    private PlatformOperationResult RegisterCore(string name, string gesture)
    {
        if (_disposed)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "macOS Carbon hotkey service is disposed.",
                PlatformErrorCodes.HotkeyNativeRegistrationFailed,
                "hotkey.register");
        }

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
            if (_registeredByName.TryGetValue(name, out var current)
                && string.Equals(current.ChordKey, binding.ChordKey, StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperation.NativeSuccess(
                    Capability.Provider,
                    $"Global hotkey already registered: {name} => {binding.NormalizedGesture}",
                    "hotkey.register");
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

            var handlerResult = EnsureHandlerInstalledUnsafe();
            if (!handlerResult.Success)
            {
                return handlerResult;
            }

            var hotKeyId = _nextHotKeyId++;
            var status = _interop.RegisterEventHotKey(
                binding.KeyCode,
                binding.Modifiers,
                new CarbonEventHotKeyId(HotKeySignature, hotKeyId),
                _interop.GetApplicationEventTarget(),
                out var hotKeyRef);
            if (status != 0)
            {
                return CreateNativeFailure(
                    "Failed to register macOS Carbon global hotkey.",
                    status,
                    "hotkey.register");
            }

            var replacement = new RegisteredMacCarbonHotkey(
                Name: name,
                NormalizedGesture: binding.NormalizedGesture,
                DisplayGesture: binding.DisplayGesture,
                ChordKey: binding.ChordKey,
                HotKeyId: hotKeyId,
                NativeHotKeyRef: hotKeyRef);

            if (_registeredByName.TryGetValue(name, out var previous))
            {
                var unregisterStatus = _interop.UnregisterEventHotKey(previous.NativeHotKeyRef);
                if (unregisterStatus != 0)
                {
                    try
                    {
                        _ = _interop.UnregisterEventHotKey(hotKeyRef);
                    }
                    catch
                    {
                        // ignored
                    }

                    return CreateNativeFailure(
                        "Failed to replace existing macOS Carbon global hotkey.",
                        unregisterStatus,
                        "hotkey.register");
                }

                _registeredByChord.Remove(previous.ChordKey);
                _registeredByHotKeyId.Remove(previous.HotKeyId);
            }

            _registeredByName[name] = replacement;
            _registeredByChord[binding.ChordKey] = name;
            _registeredByHotKeyId[hotKeyId] = replacement;
            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                $"Global hotkey registered: {name} => {binding.NormalizedGesture}",
                "hotkey.register");
        }
    }

    private PlatformOperationResult UnregisterCore(string name)
    {
        if (_disposed)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "macOS Carbon hotkey service is disposed.",
                PlatformErrorCodes.HotkeyNativeRegistrationFailed,
                "hotkey.unregister");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "Hotkey name cannot be empty.",
                PlatformErrorCodes.HotkeyNameMissing,
                "hotkey.unregister");
        }

        lock (_syncRoot)
        {
            if (!_registeredByName.TryGetValue(name, out var registered))
            {
                return PlatformOperation.Failed(
                    Capability.Provider,
                    $"Hotkey `{name}` was not registered.",
                    PlatformErrorCodes.HotkeyNotFound,
                    "hotkey.unregister");
            }

            var status = _interop.UnregisterEventHotKey(registered.NativeHotKeyRef);
            if (status != 0)
            {
                return CreateNativeFailure(
                    $"Failed to unregister macOS Carbon global hotkey `{name}`.",
                    status,
                    "hotkey.unregister");
            }

            _registeredByName.Remove(name);
            _registeredByChord.Remove(registered.ChordKey);
            _registeredByHotKeyId.Remove(registered.HotKeyId);
            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                $"Global hotkey unregistered: {name}",
                "hotkey.unregister");
        }
    }

    private PlatformOperationResult EnsureHandlerInstalledUnsafe()
    {
        if (_handlerRef != nint.Zero)
        {
            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                "macOS Carbon hotkey handler already installed.",
                "hotkey.hook");
        }

        var target = _interop.GetApplicationEventTarget();
        if (target == nint.Zero)
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "macOS application event target is unavailable.",
                PlatformErrorCodes.HotkeyNativeRegistrationFailed,
                "hotkey.hook");
        }

        var status = _interop.InstallEventHandler(target, _eventHandler, _eventTypes, nint.Zero, out var handlerRef);
        if (status != 0 || handlerRef == nint.Zero)
        {
            return CreateNativeFailure(
                "Failed to install macOS Carbon global hotkey handler.",
                status,
                "hotkey.hook");
        }

        _handlerRef = handlerRef;
        return PlatformOperation.NativeSuccess(
            Capability.Provider,
            "macOS Carbon hotkey handler installed.",
            "hotkey.hook");
    }

    private int HandleCarbonHotKeyPressed(nint nextHandler, nint eventRef, nint userData)
    {
        RegisteredMacCarbonHotkey? matched = null;
        try
        {
            if (_interop.GetEventHotKeyId(eventRef, out var hotKeyId) != 0)
            {
                return 0;
            }

            lock (_syncRoot)
            {
                if (_registeredByHotKeyId.TryGetValue(hotKeyId, out var registered))
                {
                    matched = registered;
                }
            }

            if (matched is null)
            {
                return 0;
            }

            Triggered?.Invoke(this, new GlobalHotkeyTriggeredEvent(
                matched.Value.Name,
                matched.Value.NormalizedGesture,
                DateTimeOffset.UtcNow));
        }
        catch
        {
            // Keep the native handler alive.
        }

        return 0;
    }

    private PlatformOperationResult CreateNativeFailure(string message, int status, string operationId)
    {
        return PlatformOperation.Failed(
            Capability.Provider,
            $"{message} OSStatus={status}.",
            PlatformErrorCodes.HotkeyNativeRegistrationFailed,
            operationId);
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Send, cancellationToken).GetTask();
    }

    private static bool TryParseGesture(string gesture, out ParsedMacCarbonHotkey binding)
    {
        binding = default;
        if (!HotkeyGestureCodec.TryParse(gesture, out var parsed))
        {
            return false;
        }

        if (!TryParseKeyCode(parsed.Key, out var keyCode))
        {
            return false;
        }

        var normalized = parsed.ToStorageString();
        binding = new ParsedMacCarbonHotkey(
            normalized,
            HotkeyGestureCodec.FormatDisplay(normalized, HotkeyDisplayPlatform.MacOS),
            keyCode,
            GetModifiers(parsed),
            parsed.ToChordKey());
        return true;
    }

    private static uint GetModifiers(HotkeyGesture gesture)
    {
        var modifiers = 0u;
        if (gesture.Ctrl)
        {
            modifiers |= ControlKey;
        }

        if (gesture.Shift)
        {
            modifiers |= ShiftKey;
        }

        if (gesture.Alt)
        {
            modifiers |= OptionKey;
        }

        if (gesture.Meta)
        {
            modifiers |= CmdKey;
        }

        return modifiers;
    }

    private static bool TryParseKeyCode(string token, out uint keyCode)
    {
        return CarbonKeyCodes.TryGetValue(token, out keyCode);
    }

    private static readonly IReadOnlyDictionary<string, uint> CarbonKeyCodes =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = 0x00,
            ["S"] = 0x01,
            ["D"] = 0x02,
            ["F"] = 0x03,
            ["H"] = 0x04,
            ["G"] = 0x05,
            ["Z"] = 0x06,
            ["X"] = 0x07,
            ["C"] = 0x08,
            ["V"] = 0x09,
            ["B"] = 0x0B,
            ["Q"] = 0x0C,
            ["W"] = 0x0D,
            ["E"] = 0x0E,
            ["R"] = 0x0F,
            ["Y"] = 0x10,
            ["T"] = 0x11,
            ["1"] = 0x12,
            ["2"] = 0x13,
            ["3"] = 0x14,
            ["4"] = 0x15,
            ["6"] = 0x16,
            ["5"] = 0x17,
            ["Plus"] = 0x18,
            ["9"] = 0x19,
            ["7"] = 0x1A,
            ["Minus"] = 0x1B,
            ["8"] = 0x1C,
            ["0"] = 0x1D,
            ["O"] = 0x1F,
            ["U"] = 0x20,
            ["I"] = 0x22,
            ["P"] = 0x23,
            ["Enter"] = 0x24,
            ["L"] = 0x25,
            ["J"] = 0x26,
            ["K"] = 0x28,
            ["N"] = 0x2D,
            ["M"] = 0x2E,
            ["Tab"] = 0x30,
            ["Space"] = 0x31,
            ["Backspace"] = 0x33,
            ["Escape"] = 0x35,
            ["F17"] = 0x40,
            ["F18"] = 0x4F,
            ["F19"] = 0x50,
            ["F20"] = 0x5A,
            ["F5"] = 0x60,
            ["F6"] = 0x61,
            ["F7"] = 0x62,
            ["F3"] = 0x63,
            ["F8"] = 0x64,
            ["F9"] = 0x65,
            ["F11"] = 0x67,
            ["F13"] = 0x69,
            ["F16"] = 0x6A,
            ["F14"] = 0x6B,
            ["F10"] = 0x6D,
            ["F12"] = 0x6F,
            ["F15"] = 0x71,
            ["Insert"] = 0x72,
            ["Home"] = 0x73,
            ["PageUp"] = 0x74,
            ["Delete"] = 0x75,
            ["F4"] = 0x76,
            ["End"] = 0x77,
            ["F2"] = 0x78,
            ["PageDown"] = 0x79,
            ["F1"] = 0x7A,
            ["Left"] = 0x7B,
            ["Right"] = 0x7C,
            ["Down"] = 0x7D,
            ["Up"] = 0x7E,
        };

    private readonly record struct ParsedMacCarbonHotkey(
        string NormalizedGesture,
        string DisplayGesture,
        uint KeyCode,
        uint Modifiers,
        string ChordKey);

    private readonly record struct RegisteredMacCarbonHotkey(
        string Name,
        string NormalizedGesture,
        string DisplayGesture,
        string ChordKey,
        uint HotKeyId,
        nint NativeHotKeyRef);
}
