using System.Diagnostics.CodeAnalysis;
using Tmds.DBus.Protocol;

namespace MAAUnified.Platform;

public enum LinuxDesktopSessionKind
{
    Unknown = 0,
    X11 = 1,
    Wayland = 2,
}

public static class LinuxDesktopSessionDetector
{
    public static LinuxDesktopSessionKind Detect()
    {
        if (!OperatingSystem.IsLinux())
        {
            return LinuxDesktopSessionKind.Unknown;
        }

        var sessionType = (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? string.Empty).Trim();
        if (sessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxDesktopSessionKind.Wayland;
        }

        if (sessionType.Equals("x11", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxDesktopSessionKind.X11;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return LinuxDesktopSessionKind.Wayland;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return LinuxDesktopSessionKind.X11;
        }

        return LinuxDesktopSessionKind.Unknown;
    }
}

internal sealed record PortalShortcutDefinition(
    string Id,
    string Description,
    string PreferredTrigger);

internal sealed record PortalShortcutBinding(
    string Id,
    string Description,
    string TriggerDescription);

internal sealed record PortalSessionCreated(
    string RequestHandle,
    string SessionHandle);

internal sealed record PortalRequestResult(
    uint ResponseCode,
    IReadOnlyDictionary<string, VariantValue> Results);

internal sealed record PortalShortcutActivatedEvent(
    string SessionHandle,
    string ShortcutId,
    ulong Timestamp);

internal sealed record PortalShortcutsChangedEvent(
    string SessionHandle,
    IReadOnlyList<PortalShortcutBinding> Shortcuts);

internal interface ILinuxPortalGlobalShortcutsClient : IAsyncDisposable
{
    event EventHandler<PortalShortcutActivatedEvent>? Activated;

    event EventHandler<PortalShortcutsChangedEvent>? ShortcutsChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<uint> GetInterfaceVersionAsync(CancellationToken cancellationToken = default);

    Task<PortalSessionCreated> CreateSessionAsync(CancellationToken cancellationToken = default);

    Task<PortalRequestResult> BindShortcutsAsync(
        string sessionHandle,
        IReadOnlyList<PortalShortcutDefinition> shortcuts,
        string parentWindowIdentifier,
        CancellationToken cancellationToken = default);

    Task<PortalRequestResult> ListShortcutsAsync(
        string sessionHandle,
        CancellationToken cancellationToken = default);

    Task ConfigureShortcutsAsync(
        string sessionHandle,
        string parentWindowIdentifier,
        string? activationToken,
        CancellationToken cancellationToken = default);

    Task CloseSessionAsync(string sessionHandle, CancellationToken cancellationToken = default);
}

internal sealed class TmdsDbusLinuxPortalGlobalShortcutsClient : ILinuxPortalGlobalShortcutsClient
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string DesktopPath = "/org/freedesktop/portal/desktop";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string GlobalShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string SessionInterface = "org.freedesktop.portal.Session";
    private const string ResponseMember = "Response";
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private Connection? _connection;
    private IDisposable? _activatedSubscription;
    private IDisposable? _shortcutsChangedSubscription;
    private bool _signalsSubscribed;
    private bool _disposed;

    public event EventHandler<PortalShortcutActivatedEvent>? Activated;

    public event EventHandler<PortalShortcutsChangedEvent>? ShortcutsChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken);
        if (_signalsSubscribed)
        {
            return;
        }

        var activatedRule = new MatchRule
        {
            Type = MessageType.Signal,
            Path = DesktopPath,
            Interface = GlobalShortcutsInterface,
            Member = "Activated",
        };
        _activatedSubscription = await connection.AddMatchAsync(
            activatedRule,
            ReadActivatedSignal,
            OnActivatedSignal,
            ObserverFlags.EmitOnConnectionDispose,
            null,
            this,
            false);

        var changedRule = new MatchRule
        {
            Type = MessageType.Signal,
            Path = DesktopPath,
            Interface = GlobalShortcutsInterface,
            Member = "ShortcutsChanged",
        };
        _shortcutsChangedSubscription = await connection.AddMatchAsync(
            changedRule,
            ReadShortcutsChangedSignal,
            OnShortcutsChangedSignal,
            ObserverFlags.EmitOnConnectionDispose,
            null,
            this,
            false);

        _signalsSubscribed = true;
    }

    public async Task<uint> GetInterfaceVersionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken);
        var value = await connection.CallMethodAsync(
            CreateGetInterfaceVersionMessage(connection),
            ReadPropertyVariant,
            readerState: null);
        return value.GetUInt32();
    }

    public async Task<PortalSessionCreated> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken);
        var handleToken = CreateToken("maa_request");
        var sessionHandleToken = CreateToken("maa_session");
        var expectedRequestPath = BuildExpectedRequestPath(GetRequiredUniqueName(connection), handleToken);
        await using var requestAwaiter = await WatchRequestResponseAsync(expectedRequestPath, cancellationToken);

        var requestHandle = await connection.CallMethodAsync(
            CreateCreateSessionMessage(connection, handleToken, sessionHandleToken),
            ReadObjectPathResponse,
            readerState: null);
        var response = await requestAwaiter.WaitAsync(cancellationToken);
        if (!TryGetStringLike(response.Results, "session_handle", out var sessionHandle))
        {
            throw new ProtocolException("GlobalShortcuts.CreateSession completed without session_handle.");
        }

        return new PortalSessionCreated(requestHandle, sessionHandle);
    }

    public async Task<PortalRequestResult> BindShortcutsAsync(
        string sessionHandle,
        IReadOnlyList<PortalShortcutDefinition> shortcuts,
        string parentWindowIdentifier,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken);
        var handleToken = CreateToken("maa_bind");
        var expectedRequestPath = BuildExpectedRequestPath(GetRequiredUniqueName(connection), handleToken);
        await using var requestAwaiter = await WatchRequestResponseAsync(expectedRequestPath, cancellationToken);

        _ = await connection.CallMethodAsync(
            CreateBindShortcutsMessage(connection, sessionHandle, shortcuts, parentWindowIdentifier, handleToken),
            ReadObjectPathResponse,
            readerState: null);
        return await requestAwaiter.WaitAsync(cancellationToken);
    }

    public async Task<PortalRequestResult> ListShortcutsAsync(
        string sessionHandle,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken);
        var handleToken = CreateToken("maa_list");
        var expectedRequestPath = BuildExpectedRequestPath(GetRequiredUniqueName(connection), handleToken);
        await using var requestAwaiter = await WatchRequestResponseAsync(expectedRequestPath, cancellationToken);

        _ = await connection.CallMethodAsync(
            CreateListShortcutsMessage(connection, sessionHandle, handleToken),
            ReadObjectPathResponse,
            readerState: null);
        return await requestAwaiter.WaitAsync(cancellationToken);
    }

    public async Task ConfigureShortcutsAsync(
        string sessionHandle,
        string parentWindowIdentifier,
        string? activationToken,
        CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken);
        await connection.CallMethodAsync(
            CreateConfigureShortcutsMessage(connection, sessionHandle, parentWindowIdentifier, activationToken));
    }

    public async Task CloseSessionAsync(string sessionHandle, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionHandle))
        {
            return;
        }

        try
        {
            var connection = await EnsureConnectedAsync(cancellationToken);
            await connection.CallMethodAsync(CreateCloseSessionMessage(connection, sessionHandle));
        }
        catch
        {
            // Session cleanup is best-effort only.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activatedSubscription?.Dispose();
        _shortcutsChangedSubscription?.Dispose();
        _connection?.Dispose();
        _connectionSemaphore.Dispose();
        await Task.CompletedTask;
    }

    private async Task<Connection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _connectionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var sessionAddress = Address.Session;
            if (string.IsNullOrWhiteSpace(sessionAddress))
            {
                throw new ConnectException("D-Bus session address is unavailable.");
            }

            var connection = new Connection(sessionAddress);
            await connection.ConnectAsync();
            _connection = connection;
            return connection;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private async Task<RequestResponseAwaiter> WatchRequestResponseAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var connection = await EnsureConnectedAsync(cancellationToken);
        var awaiter = new RequestResponseAwaiter();
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Path = requestPath,
            Interface = RequestInterface,
            Member = ResponseMember,
        };

        var subscription = await connection.AddMatchAsync(
            rule,
            ReadRequestResponseSignal,
            OnRequestResponseSignal,
            ObserverFlags.EmitOnConnectionDispose,
            null,
            awaiter,
            false);
        awaiter.Attach(subscription);
        return awaiter;
    }

    private static PortalRequestResult ReadRequestResponseSignal(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        var responseCode = reader.ReadUInt32();
        var results = reader.ReadDictionaryOfStringToVariantValue();
        return new PortalRequestResult(responseCode, results);
    }

    private static PortalShortcutActivatedEvent ReadActivatedSignal(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        var sessionHandle = reader.ReadObjectPath().ToString();
        var shortcutId = reader.ReadString();
        var timestamp = reader.ReadUInt64();
        _ = reader.ReadDictionaryOfStringToVariantValue();
        return new PortalShortcutActivatedEvent(sessionHandle, shortcutId, timestamp);
    }

    private static PortalShortcutsChangedEvent ReadShortcutsChangedSignal(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        var sessionHandle = reader.ReadObjectPath().ToString();
        var shortcuts = ReadShortcutBindings(reader);
        return new PortalShortcutsChangedEvent(sessionHandle, shortcuts);
    }

    private static VariantValue ReadPropertyVariant(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        return reader.ReadVariantValue();
    }

    private static string ReadObjectPathResponse(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        return reader.ReadObjectPath().ToString();
    }

    private static IReadOnlyList<PortalShortcutBinding> ReadShortcutBindings(Reader reader)
    {
        var shortcuts = new List<PortalShortcutBinding>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            var id = reader.ReadString();
            var properties = reader.ReadDictionaryOfStringToVariantValue();
            var description = TryGetStringLike(properties, "description", out var descriptionValue)
                ? descriptionValue
                : id;
            var triggerDescription = TryGetStringLike(properties, "trigger_description", out var triggerValue)
                ? triggerValue
                : string.Empty;
            shortcuts.Add(new PortalShortcutBinding(id, description, triggerDescription));
        }

        return shortcuts;
    }

    private static MessageBuffer CreateGetInterfaceVersionMessage(Connection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            Destination,
            DesktopPath,
            PropertiesInterface,
            "Get",
            "ss",
            MessageFlags.None);
        writer.WriteString(GlobalShortcutsInterface);
        writer.WriteString("version");
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateCreateSessionMessage(
        Connection connection,
        string handleToken,
        string sessionHandleToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            Destination,
            DesktopPath,
            GlobalShortcutsInterface,
            "CreateSession",
            "a{sv}",
            MessageFlags.None);
        writer.WriteDictionary(new Dictionary<string, Variant>(StringComparer.Ordinal)
        {
            ["handle_token"] = handleToken,
            ["session_handle_token"] = sessionHandleToken,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateBindShortcutsMessage(
        Connection connection,
        string sessionHandle,
        IReadOnlyList<PortalShortcutDefinition> shortcuts,
        string parentWindowIdentifier,
        string handleToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            Destination,
            DesktopPath,
            GlobalShortcutsInterface,
            "BindShortcuts",
            "oa(sa{sv})sa{sv}",
            MessageFlags.None);
        writer.WriteObjectPath(sessionHandle);
        WriteShortcutArray(writer, shortcuts);
        writer.WriteString(parentWindowIdentifier ?? string.Empty);
        writer.WriteDictionary(new Dictionary<string, Variant>(StringComparer.Ordinal)
        {
            ["handle_token"] = handleToken,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateListShortcutsMessage(
        Connection connection,
        string sessionHandle,
        string handleToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            Destination,
            DesktopPath,
            GlobalShortcutsInterface,
            "ListShortcuts",
            "oa{sv}",
            MessageFlags.None);
        writer.WriteObjectPath(sessionHandle);
        writer.WriteDictionary(new Dictionary<string, Variant>(StringComparer.Ordinal)
        {
            ["handle_token"] = handleToken,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateConfigureShortcutsMessage(
        Connection connection,
        string sessionHandle,
        string parentWindowIdentifier,
        string? activationToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            Destination,
            DesktopPath,
            GlobalShortcutsInterface,
            "ConfigureShortcuts",
            "osa{sv}",
            MessageFlags.None);
        writer.WriteObjectPath(sessionHandle);
        writer.WriteString(parentWindowIdentifier ?? string.Empty);
        var options = new Dictionary<string, Variant>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(activationToken))
        {
            options["activation_token"] = activationToken!;
        }

        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateCloseSessionMessage(Connection connection, string sessionHandle)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            Destination,
            sessionHandle,
            SessionInterface,
            "Close",
            string.Empty,
            MessageFlags.None);
        return writer.CreateMessage();
    }

    private static string GetRequiredUniqueName(Connection connection)
    {
        return string.IsNullOrWhiteSpace(connection.UniqueName)
            ? throw new ConnectException("D-Bus unique name is unavailable.")
            : connection.UniqueName;
    }

    private static void WriteShortcutArray(
        MessageWriter writer,
        IReadOnlyList<PortalShortcutDefinition> shortcuts)
    {
        var arrayStart = writer.WriteArrayStart(DBusType.Struct);
        foreach (var shortcut in shortcuts)
        {
            writer.WriteStructureStart();
            writer.WriteString(shortcut.Id);

            var propertiesStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("description");
            writer.WriteVariantString(shortcut.Description);
            if (!string.IsNullOrWhiteSpace(shortcut.PreferredTrigger))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("preferred_trigger");
                writer.WriteVariantString(shortcut.PreferredTrigger);
            }

            writer.WriteDictionaryEnd(propertiesStart);
        }

        writer.WriteArrayEnd(arrayStart);
    }

    private static string BuildExpectedRequestPath(string uniqueName, string handleToken)
    {
        var sender = uniqueName.TrimStart(':').Replace(".", "_", StringComparison.Ordinal);
        return $"/org/freedesktop/portal/desktop/request/{sender}/{handleToken}";
    }

    private static string CreateToken(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }

    private static bool TryGetStringLike(
        IReadOnlyDictionary<string, VariantValue> values,
        string key,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!values.TryGetValue(key, out var variant))
        {
            return false;
        }

        return TryGetStringLike(variant, out value);
    }

    private static bool TryGetStringLike(VariantValue variant, [NotNullWhen(true)] out string? value)
    {
        value = null;
        try
        {
            value = variant.Type switch
            {
                VariantValueType.String => variant.GetString(),
                VariantValueType.ObjectPath => variant.GetObjectPath(),
                _ => null,
            };
        }
        catch
        {
            value = null;
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static void OnRequestResponseSignal(
        Exception? exception,
        PortalRequestResult result,
        object? _,
        object? handlerState)
    {
        ((RequestResponseAwaiter)handlerState!).SetResult(exception, result);
    }

    private static void OnActivatedSignal(
        Exception? exception,
        PortalShortcutActivatedEvent signal,
        object? _,
        object? handlerState)
    {
        if (exception is not null)
        {
            return;
        }

        var client = (TmdsDbusLinuxPortalGlobalShortcutsClient)handlerState!;
        try
        {
            client.Activated?.Invoke(client, signal);
        }
        catch
        {
            // Keep the DBus signal handler alive even if a subscriber fails.
        }
    }

    private static void OnShortcutsChangedSignal(
        Exception? exception,
        PortalShortcutsChangedEvent signal,
        object? _,
        object? handlerState)
    {
        if (exception is not null)
        {
            return;
        }

        var client = (TmdsDbusLinuxPortalGlobalShortcutsClient)handlerState!;
        try
        {
            client.ShortcutsChanged?.Invoke(client, signal);
        }
        catch
        {
            // Keep the DBus signal handler alive even if a subscriber fails.
        }
    }

    private sealed class RequestResponseAwaiter : IAsyncDisposable
    {
        private readonly TaskCompletionSource<PortalRequestResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDisposable? _subscription;

        public void Attach(IDisposable subscription)
        {
            _subscription = subscription;
        }

        public Task<PortalRequestResult> WaitAsync(CancellationToken cancellationToken)
        {
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void SetResult(Exception? exception, PortalRequestResult result)
        {
            if (exception is not null)
            {
                _completion.TrySetException(exception);
                return;
            }

            _completion.TrySetResult(result);
        }

        public ValueTask DisposeAsync()
        {
            _subscription?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class LinuxPortalGlobalHotkeyService : IGlobalHotkeyService
{
    private static readonly string[] DefaultShortcutOrder =
    [
        "ShowGui",
        "LinkStart",
    ];

    private readonly object _syncRoot = new();
    private readonly ILinuxPortalGlobalShortcutsClient _client;
    private readonly Dictionary<string, string> _registeredGestures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PortalShortcutBinding> _boundShortcuts = new(StringComparer.OrdinalIgnoreCase);
    private HotkeyHostContext _hostContext = new(nint.Zero, string.Empty, "unknown");
    private string? _sessionHandle;
    private bool _supportChecked;
    private bool _portalSupported;
    private string _portalSupportMessage = "Wayland global hotkeys are available via xdg-desktop-portal.";

    public LinuxPortalGlobalHotkeyService()
        : this(new TmdsDbusLinuxPortalGlobalShortcutsClient())
    {
    }

    internal LinuxPortalGlobalHotkeyService(ILinuxPortalGlobalShortcutsClient client)
    {
        _client = client;
        _client.Activated += OnPortalActivated;
        _client.ShortcutsChanged += OnPortalShortcutsChanged;
    }

    public event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: _portalSupportMessage,
        Provider: "xdg-desktop-portal",
        HasFallback: true,
        FallbackMode: "window-scoped");

    public static bool TryCreate([NotNullWhen(true)] out LinuxPortalGlobalHotkeyService? service)
    {
        service = null;
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (!PlatformNativeDependencyProbe.HasAssembly("Tmds.DBus.Protocol"))
        {
            return false;
        }

        service = new LinuxPortalGlobalHotkeyService();
        return true;
    }

    public async Task<PlatformOperationResult> RegisterAsync(string name, string gesture, CancellationToken cancellationToken = default)
    {
        var results = await RegisterBatchAsync(
            [new HotkeyBindingRequest(name, gesture)],
            cancellationToken);
        return results.FirstOrDefault()?.Result
            ?? PlatformOperation.Failed(
                Capability.Provider,
                "Hotkey registration batch did not produce a result.",
                PlatformErrorCodes.HotkeyPortalUnavailable,
                "hotkey.register");
    }

    public async Task<IReadOnlyList<HotkeyRegistrationOutcome>> RegisterBatchAsync(
        IReadOnlyList<HotkeyBindingRequest> requests,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count == 0)
        {
            return [];
        }

        var normalizedRequests = new List<HotkeyBindingRequest>(requests.Count);
        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return
                [
                    new HotkeyRegistrationOutcome(
                        request.Name,
                        request.Gesture,
                        PlatformOperation.Failed(
                            Capability.Provider,
                            "Hotkey name cannot be empty.",
                            PlatformErrorCodes.HotkeyNameMissing,
                            "hotkey.register")),
                ];
            }

            if (!HotkeyGestureCodec.TryNormalize(request.Gesture, out var normalizedGesture))
            {
                return
                [
                    new HotkeyRegistrationOutcome(
                        request.Name,
                        request.Gesture,
                        PlatformOperation.Failed(
                            Capability.Provider,
                            "Invalid hotkey gesture format.",
                            PlatformErrorCodes.HotkeyInvalidGesture,
                            "hotkey.register")),
                ];
            }

            normalizedRequests.Add(request with { Gesture = normalizedGesture });
        }

        var supportFailure = await EnsurePortalSupportAsync(cancellationToken);
        if (supportFailure is not null)
        {
            return normalizedRequests
                .Select(request => new HotkeyRegistrationOutcome(request.Name, request.Gesture, supportFailure))
                .ToArray();
        }

        Dictionary<string, string> mergedGestures;
        lock (_syncRoot)
        {
            mergedGestures = new Dictionary<string, string>(_registeredGestures, StringComparer.OrdinalIgnoreCase);
            foreach (var request in normalizedRequests)
            {
                mergedGestures[request.Name] = request.Gesture;
            }
        }

        if (TryFindConflict(mergedGestures, out var conflictMessage))
        {
            var failure = PlatformOperation.Failed(
                Capability.Provider,
                conflictMessage,
                PlatformErrorCodes.HotkeyConflict,
                "hotkey.register");
            return normalizedRequests
                .Select(request => new HotkeyRegistrationOutcome(request.Name, request.Gesture, failure))
                .ToArray();
        }

        await CloseCurrentSessionAsync(cancellationToken);

        try
        {
            var session = await _client.CreateSessionAsync(cancellationToken);
            var sessionHandle = session.SessionHandle;
            var requestResult = await _client.BindShortcutsAsync(
                sessionHandle,
                BuildShortcutDefinitions(mergedGestures),
                ResolveParentWindowIdentifier(),
                cancellationToken);

            if (requestResult.ResponseCode != 0)
            {
                await _client.CloseSessionAsync(sessionHandle, cancellationToken);
                var failure = BuildPortalResponseFailure(requestResult.ResponseCode);
                lock (_syncRoot)
                {
                    _sessionHandle = null;
                    _boundShortcuts.Clear();
                    _registeredGestures.Clear();
                }

                return normalizedRequests
                    .Select(request => new HotkeyRegistrationOutcome(request.Name, request.Gesture, failure))
                    .ToArray();
            }

            var boundShortcuts = ExtractBoundShortcuts(requestResult.Results);
            if (boundShortcuts.Count == 0)
            {
                try
                {
                    var listed = await _client.ListShortcutsAsync(sessionHandle, cancellationToken);
                    boundShortcuts = ExtractBoundShortcuts(listed.Results);
                }
                catch
                {
                    // Keep the bind response as the source of truth if listing fails.
                }
            }

            lock (_syncRoot)
            {
                _sessionHandle = sessionHandle;
                _boundShortcuts.Clear();
                _registeredGestures.Clear();
                foreach (var shortcut in boundShortcuts)
                {
                    _boundShortcuts[shortcut.Id] = shortcut;
                    if (mergedGestures.TryGetValue(shortcut.Id, out var gesture))
                    {
                        _registeredGestures[shortcut.Id] = gesture;
                    }
                }
            }

            return BuildRegistrationOutcomes(normalizedRequests, mergedGestures, boundShortcuts);
        }
        catch (Exception ex)
        {
            var failure = BuildPortalExceptionFailure(ex);
            lock (_syncRoot)
            {
                _sessionHandle = null;
                _boundShortcuts.Clear();
                _registeredGestures.Clear();
            }

            return normalizedRequests
                .Select(request => new HotkeyRegistrationOutcome(request.Name, request.Gesture, failure))
                .ToArray();
        }
    }

    public async Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
        {
            return PlatformOperation.Failed(
                Capability.Provider,
                "Hotkey name cannot be empty.",
                PlatformErrorCodes.HotkeyNameMissing,
                "hotkey.unregister");
        }

        Dictionary<string, string> remainingGestures;
        lock (_syncRoot)
        {
            if (!_registeredGestures.ContainsKey(name))
            {
                return PlatformOperation.Failed(
                    Capability.Provider,
                    $"Hotkey `{name}` was not registered.",
                    PlatformErrorCodes.HotkeyNotFound,
                    "hotkey.unregister");
            }

            remainingGestures = new Dictionary<string, string>(_registeredGestures, StringComparer.OrdinalIgnoreCase);
            remainingGestures.Remove(name);
        }

        await CloseCurrentSessionAsync(cancellationToken);
        if (remainingGestures.Count == 0)
        {
            lock (_syncRoot)
            {
                _registeredGestures.Clear();
                _boundShortcuts.Clear();
                _sessionHandle = null;
            }

            return PlatformOperation.NativeSuccess(
                Capability.Provider,
                $"Portal hotkey unregistered: {name}",
                "hotkey.unregister");
        }

        var rebindResults = await RegisterBatchAsync(
            remainingGestures.Select(pair => new HotkeyBindingRequest(pair.Key, pair.Value)).ToArray(),
            cancellationToken);
        var failed = rebindResults.FirstOrDefault(result => !result.Result.Success);
        if (failed is not null)
        {
            return failed.Result;
        }

        return PlatformOperation.NativeSuccess(
            Capability.Provider,
            $"Portal hotkey unregistered: {name}",
            "hotkey.unregister");
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
            $"Portal hotkey host context updated. session={context.SessionType} parent_window={(string.IsNullOrWhiteSpace(context.ParentWindowIdentifier) ? "<empty>" : context.ParentWindowIdentifier)}",
            "hotkey.configure-host"));
    }

    public bool TryGetRegisteredHotkey(string name, out RegisteredHotkeyState state)
    {
        lock (_syncRoot)
        {
            if (!_registeredGestures.TryGetValue(name, out var gesture))
            {
                state = default!;
                return false;
            }

            var displayGesture = _boundShortcuts.TryGetValue(name, out var binding)
                && !string.IsNullOrWhiteSpace(binding.TriggerDescription)
                ? binding.TriggerDescription
                : HotkeyGestureCodec.FormatDisplay(gesture);
            state = new RegisteredHotkeyState(
                name,
                gesture,
                displayGesture,
                Capability.Provider,
                PlatformExecutionMode.Native);
            return true;
        }
    }

    private async Task<PlatformOperationResult?> EnsurePortalSupportAsync(CancellationToken cancellationToken)
    {
        if (_supportChecked)
        {
            return _portalSupported
                ? null
                : PlatformOperation.Failed(
                    Capability.Provider,
                    _portalSupportMessage,
                    PlatformErrorCodes.HotkeyPortalUnsupported,
                    "hotkey.portal.support");
        }

        try
        {
            await _client.InitializeAsync(cancellationToken);
            var version = await _client.GetInterfaceVersionAsync(cancellationToken);
            _portalSupported = version >= 2;
            _supportChecked = true;
            _portalSupportMessage = _portalSupported
                ? $"Wayland global hotkeys are available via xdg-desktop-portal GlobalShortcuts (version={version})."
                : $"xdg-desktop-portal GlobalShortcuts is too old (version={version}); version 2 or newer is required.";

            return _portalSupported
                ? null
                : PlatformOperation.Failed(
                    Capability.Provider,
                    _portalSupportMessage,
                    PlatformErrorCodes.HotkeyPortalUnsupported,
                    "hotkey.portal.support");
        }
        catch (Exception ex)
        {
            _portalSupported = false;
            _supportChecked = true;
            _portalSupportMessage = $"xdg-desktop-portal GlobalShortcuts is unavailable: {ex.Message}";
            return PlatformOperation.Failed(
                Capability.Provider,
                _portalSupportMessage,
                PlatformErrorCodes.HotkeyPortalUnavailable,
                "hotkey.portal.support");
        }
    }

    private async Task CloseCurrentSessionAsync(CancellationToken cancellationToken)
    {
        string? sessionHandle;
        lock (_syncRoot)
        {
            sessionHandle = _sessionHandle;
            _sessionHandle = null;
        }

        if (!string.IsNullOrWhiteSpace(sessionHandle))
        {
            try
            {
                await _client.CloseSessionAsync(sessionHandle, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Closing stale portal sessions is best-effort; rebind/unregister should still return a platform result.
            }
        }
    }

    private static IReadOnlyList<PortalShortcutDefinition> BuildShortcutDefinitions(
        IReadOnlyDictionary<string, string> gestures)
    {
        var orderedIds = DefaultShortcutOrder
            .Concat(gestures.Keys.Where(key => !DefaultShortcutOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return orderedIds
            .Where(gestures.ContainsKey)
            .Select(id => new PortalShortcutDefinition(
                id,
                BuildShortcutDescription(id),
                gestures[id]))
            .ToArray();
    }

    private static IReadOnlyList<HotkeyRegistrationOutcome> BuildRegistrationOutcomes(
        IReadOnlyList<HotkeyBindingRequest> requests,
        IReadOnlyDictionary<string, string> mergedGestures,
        IReadOnlyList<PortalShortcutBinding> boundShortcuts)
    {
        var boundById = boundShortcuts.ToDictionary(binding => binding.Id, StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<HotkeyRegistrationOutcome>(requests.Count);
        foreach (var request in requests)
        {
            if (!boundById.TryGetValue(request.Name, out var binding))
            {
                outcomes.Add(new HotkeyRegistrationOutcome(
                    request.Name,
                    request.Gesture,
                    PlatformOperation.Failed(
                        "xdg-desktop-portal",
                        $"Portal did not bind shortcut `{request.Name}`.",
                        PlatformErrorCodes.HotkeyPortalCancelled,
                        "hotkey.register")));
                continue;
            }

            var displayGesture = string.IsNullOrWhiteSpace(binding.TriggerDescription)
                ? HotkeyGestureCodec.FormatDisplay(request.Gesture)
                : binding.TriggerDescription;
            outcomes.Add(new HotkeyRegistrationOutcome(
                request.Name,
                mergedGestures[request.Name],
                PlatformOperation.NativeSuccess(
                    "xdg-desktop-portal",
                    $"Portal hotkey bound: {request.Name} => {displayGesture}",
                    "hotkey.register"),
                displayGesture));
        }

        return outcomes;
    }

    private static IReadOnlyList<PortalShortcutBinding> ExtractBoundShortcuts(
        IReadOnlyDictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("shortcuts", out var shortcutsValue)
            || shortcutsValue.Type != VariantValueType.Array)
        {
            return [];
        }

        var array = shortcutsValue.GetArray<VariantValue>();
        var shortcuts = new List<PortalShortcutBinding>(array.Length);
        foreach (var item in array)
        {
            if (item.Type != VariantValueType.Struct || item.Count < 2)
            {
                continue;
            }

            var id = item.GetItem(0).GetString();
            var properties = item.GetItem(1).GetDictionary<string, VariantValue>();
            var description = properties.TryGetValue("description", out var descriptionValue)
                && TryGetStringLike(descriptionValue, out var descriptionText)
                    ? descriptionText
                    : id;
            var triggerDescription = properties.TryGetValue("trigger_description", out var triggerValue)
                && TryGetStringLike(triggerValue, out var triggerText)
                    ? triggerText
                    : string.Empty;
            shortcuts.Add(new PortalShortcutBinding(id, description, triggerDescription));
        }

        return shortcuts;
    }

    private static PlatformOperationResult BuildPortalResponseFailure(uint responseCode)
    {
        return responseCode switch
        {
            1 => PlatformOperation.Failed(
                "xdg-desktop-portal",
                "Portal shortcut binding was cancelled by the user.",
                PlatformErrorCodes.HotkeyPortalCancelled,
                "hotkey.register"),
            _ => PlatformOperation.Failed(
                "xdg-desktop-portal",
                $"Portal shortcut binding did not complete successfully (response={responseCode}).",
                PlatformErrorCodes.HotkeyPortalUnavailable,
                "hotkey.register"),
        };
    }

    private static PlatformOperationResult BuildPortalExceptionFailure(Exception ex)
    {
        var errorCode = ex switch
        {
            DBusException dbus when dbus.ErrorName.Contains("UnknownMethod", StringComparison.OrdinalIgnoreCase)
                || dbus.ErrorName.Contains("ServiceUnknown", StringComparison.OrdinalIgnoreCase)
                => PlatformErrorCodes.HotkeyPortalUnavailable,
            ConnectException => PlatformErrorCodes.HotkeyPortalUnavailable,
            _ => PlatformErrorCodes.HotkeyPortalUnavailable,
        };

        return PlatformOperation.Failed(
            "xdg-desktop-portal",
            $"Portal hotkey binding failed: {ex.Message}",
            errorCode,
            "hotkey.register");
    }

    private static bool TryFindConflict(
        IReadOnlyDictionary<string, string> gestures,
        [NotNullWhen(true)] out string? message)
    {
        message = null;
        var usedGestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, gesture) in gestures)
        {
            if (usedGestures.TryGetValue(gesture, out var existingName)
                && !string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
            {
                message = $"Hotkey gesture already in use: {existingName} and {name} both use {gesture}.";
                return true;
            }

            usedGestures[gesture] = name;
        }

        return false;
    }

    private static string BuildShortcutDescription(string shortcutId)
    {
        return shortcutId switch
        {
            "ShowGui" => "Show or restore the MAA window.",
            "LinkStart" => "Start the MAA task queue.",
            _ => shortcutId,
        };
    }

    private static bool TryGetStringLike(VariantValue variant, [NotNullWhen(true)] out string? value)
    {
        value = null;
        try
        {
            value = variant.Type switch
            {
                VariantValueType.String => variant.GetString(),
                VariantValueType.ObjectPath => variant.GetObjectPath(),
                _ => null,
            };
        }
        catch
        {
            value = null;
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private string ResolveParentWindowIdentifier()
    {
        lock (_syncRoot)
        {
            return _hostContext.ParentWindowIdentifier ?? string.Empty;
        }
    }

    private void OnPortalActivated(object? sender, PortalShortcutActivatedEvent e)
    {
        string? gesture = null;
        lock (_syncRoot)
        {
            if (!string.Equals(_sessionHandle, e.SessionHandle, StringComparison.Ordinal))
            {
                return;
            }

            if (_registeredGestures.TryGetValue(e.ShortcutId, out var registeredGesture))
            {
                gesture = registeredGesture;
            }
        }

        if (gesture is null)
        {
            return;
        }

        try
        {
            Triggered?.Invoke(this, new GlobalHotkeyTriggeredEvent(
                e.ShortcutId,
                gesture,
                DateTimeOffset.UtcNow));
        }
        catch
        {
            // Keep signal handling resilient.
        }
    }

    private void OnPortalShortcutsChanged(object? sender, PortalShortcutsChangedEvent e)
    {
        lock (_syncRoot)
        {
            if (!string.Equals(_sessionHandle, e.SessionHandle, StringComparison.Ordinal))
            {
                return;
            }

            _boundShortcuts.Clear();
            foreach (var shortcut in e.Shortcuts)
            {
                _boundShortcuts[shortcut.Id] = shortcut;
            }
        }
    }
}

public sealed class CompositeGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly object _syncRoot = new();
    private readonly IGlobalHotkeyService _primary;
    private readonly IGlobalHotkeyService _fallback;
    private readonly Dictionary<string, string> _desiredGestures = new(StringComparer.OrdinalIgnoreCase);
    private bool _fallbackActive;
    private string _fallbackMessage;
    private HotkeyHostContext _hostContext = new(nint.Zero, string.Empty, "unknown");

    public CompositeGlobalHotkeyService(IGlobalHotkeyService primary, IGlobalHotkeyService fallback)
    {
        _primary = primary;
        _fallback = fallback;
        _fallbackMessage = _fallback.Capability.Message;
        _primary.Triggered += (_, e) => RaiseTriggered(e);
        _fallback.Triggered += (_, e) => RaiseTriggered(e);
    }

    public event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

    private void RaiseTriggered(GlobalHotkeyTriggeredEvent e)
    {
        try
        {
            Triggered?.Invoke(this, e);
        }
        catch
        {
            // Keep the underlying hotkey provider callback alive.
        }
    }

    public PlatformCapabilityStatus Capability
    {
        get
        {
            lock (_syncRoot)
            {
                if (!_fallbackActive)
                {
                    return _primary.Capability;
                }

                return new PlatformCapabilityStatus(
                    Supported: false,
                    Message: _fallbackMessage,
                    Provider: _fallback.Capability.Provider,
                    HasFallback: true,
                    FallbackMode: _fallback.Capability.FallbackMode ?? "window-scoped");
            }
        }
    }

    public async Task<PlatformOperationResult> RegisterAsync(string name, string gesture, CancellationToken cancellationToken = default)
    {
        var results = await RegisterBatchAsync(
            [new HotkeyBindingRequest(name, gesture)],
            cancellationToken);
        return results.FirstOrDefault()?.Result
            ?? PlatformOperation.Failed(
                Capability.Provider,
                "Composite hotkey registration did not produce a result.",
                PlatformErrorCodes.HotkeyUnsupported,
                "hotkey.register");
    }

    public async Task<IReadOnlyList<HotkeyRegistrationOutcome>> RegisterBatchAsync(
        IReadOnlyList<HotkeyBindingRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        Dictionary<string, string> desiredAfterApply;
        lock (_syncRoot)
        {
            desiredAfterApply = new Dictionary<string, string>(_desiredGestures, StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                desiredAfterApply[request.Name] = request.Gesture;
            }
        }

        if (IsFallbackActive())
        {
            var fallbackResults = await _fallback.RegisterBatchAsync(requests, cancellationToken);
            UpdateDesiredGestures(requests, fallbackResults);
            return fallbackResults;
        }

        var primaryResults = await _primary.RegisterBatchAsync(requests, cancellationToken);
        if (ShouldFallback(primaryResults))
        {
            await _fallback.ConfigureHostContextAsync(_hostContext, cancellationToken);
            ActivateFallback(primaryResults);
            var fallbackRequests = desiredAfterApply
                .Select(pair => new HotkeyBindingRequest(pair.Key, pair.Value))
                .ToArray();
            var fallbackResults = await _fallback.RegisterBatchAsync(fallbackRequests, cancellationToken);
            UpdateDesiredGestures(fallbackRequests, fallbackResults);
            var projected = ProjectRequestedResults(requests, fallbackResults);
            return projected;
        }

        UpdateDesiredGestures(requests, primaryResults);
        return primaryResults;
    }

    public async Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await CurrentService().UnregisterAsync(name, cancellationToken);
        if (result.Success)
        {
            lock (_syncRoot)
            {
                _desiredGestures.Remove(name);
            }
        }

        return result;
    }

    public async Task<PlatformOperationResult> ConfigureHostContextAsync(
        HotkeyHostContext context,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            _hostContext = context;
        }

        var primaryResult = await _primary.ConfigureHostContextAsync(context, cancellationToken);
        var fallbackResult = await _fallback.ConfigureHostContextAsync(context, cancellationToken);
        if (!primaryResult.Success)
        {
            return primaryResult;
        }

        return fallbackResult.Success ? primaryResult : fallbackResult;
    }

    public bool TryDispatchWindowScopedHotkey(HotkeyGesture gesture)
    {
        return CurrentService().TryDispatchWindowScopedHotkey(gesture);
    }

    public bool TryGetRegisteredHotkey(string name, out RegisteredHotkeyState state)
    {
        return CurrentService().TryGetRegisteredHotkey(name, out state);
    }

    private static IReadOnlyList<HotkeyRegistrationOutcome> ProjectRequestedResults(
        IReadOnlyList<HotkeyBindingRequest> requests,
        IReadOnlyList<HotkeyRegistrationOutcome> results)
    {
        var byName = results.ToDictionary(result => result.Name, StringComparer.OrdinalIgnoreCase);
        var projected = new List<HotkeyRegistrationOutcome>(requests.Count);
        foreach (var request in requests)
        {
            if (byName.TryGetValue(request.Name, out var result))
            {
                projected.Add(result);
            }
        }

        return projected;
    }

    private void UpdateDesiredGestures(
        IReadOnlyList<HotkeyBindingRequest> requests,
        IReadOnlyList<HotkeyRegistrationOutcome> results)
    {
        var requestsByName = requests.ToDictionary(request => request.Name, StringComparer.OrdinalIgnoreCase);
        lock (_syncRoot)
        {
            foreach (var result in results)
            {
                if (!result.Result.Success)
                {
                    continue;
                }

                _desiredGestures[result.Name] = result.Gesture;
            }

            foreach (var request in requestsByName.Values)
            {
                if (results.Any(result => string.Equals(result.Name, request.Name, StringComparison.OrdinalIgnoreCase)
                                          && result.Result.Success))
                {
                    continue;
                }

                _desiredGestures.Remove(request.Name);
            }
        }
    }

    private void ActivateFallback(IReadOnlyList<HotkeyRegistrationOutcome> results)
    {
        var firstFailure = results.FirstOrDefault(result => !result.Result.Success);
        lock (_syncRoot)
        {
            _fallbackActive = true;
            _fallbackMessage = firstFailure is null
                ? _fallback.Capability.Message
                : $"Global hotkeys switched to window-scoped fallback after {_primary.Capability.Provider} failed: {firstFailure.Result.Message}";
        }
    }

    private bool IsFallbackActive()
    {
        lock (_syncRoot)
        {
            return _fallbackActive;
        }
    }

    private IGlobalHotkeyService CurrentService()
    {
        lock (_syncRoot)
        {
            return _fallbackActive ? _fallback : _primary;
        }
    }

    private static bool ShouldFallback(IReadOnlyList<HotkeyRegistrationOutcome> results)
    {
        return results.Any(result =>
            !result.Result.Success
            && result.Result.ErrorCode is PlatformErrorCodes.HotkeyUnsupported
                or PlatformErrorCodes.HotkeyPermissionDenied
                or PlatformErrorCodes.HotkeyNativeRegistrationFailed
                or PlatformErrorCodes.HotkeyHookStartFailed
                or PlatformErrorCodes.HotkeyTriggerDispatchFailed
                or PlatformErrorCodes.HotkeyPortalUnavailable
                or PlatformErrorCodes.HotkeyPortalUnsupported
                or PlatformErrorCodes.HotkeyPortalCancelled);
    }
}
