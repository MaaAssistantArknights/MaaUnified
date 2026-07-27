using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MAAUnified.Platform;

/// <summary>
/// Native notification backend selected by <see cref="DesktopNotificationService"/>.
/// </summary>
internal interface INotificationPoster : IDisposable
{
    event EventHandler<NotificationActionActivatedEventArgs>? ActionActivated;

    PlatformCapabilityStatus Capability { get; }

    NotificationAvailabilityStatus GetAvailability();

    Task ShowAsync(SystemNotificationRequest notification, CancellationToken cancellationToken);
}

internal sealed class BufferedNotificationActivationSource(int capacity) : IDisposable
{
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly Queue<(object Sender, NotificationActionActivatedEventArgs EventArgs)> _pendingActivations = new();
    private readonly Queue<ActivationDispatch> _dispatchQueue = new();
    private EventHandler<NotificationActionActivatedEventArgs>? _actionActivated;
    private bool _isDraining;
    private bool _disposed;

    public event EventHandler<NotificationActionActivatedEventArgs>? ActionActivated
    {
        add
        {
            if (value is null)
            {
                return;
            }

            bool shouldDrain;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _actionActivated += value;
                foreach (var activation in _pendingActivations)
                {
                    _dispatchQueue.Enqueue(new ActivationDispatch(
                        activation.Sender,
                        activation.EventArgs,
                        [value]));
                }

                _pendingActivations.Clear();
                shouldDrain = StartDrainingIfNeeded();
            }

            if (shouldDrain)
            {
                DrainDispatchQueue();
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _actionActivated -= value;
            }
        }
    }

    public void Publish(object sender, NotificationActionActivatedEventArgs eventArgs)
    {
        bool shouldDrain;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var handlers = _actionActivated;
            if (handlers is null)
            {
                if (_pendingActivations.Count == _capacity)
                {
                    _pendingActivations.Dequeue();
                }

                _pendingActivations.Enqueue((sender, eventArgs));
                return;
            }

            // Snapshot subscribers at enqueue time so later subscriptions cannot observe earlier activations.
            _dispatchQueue.Enqueue(new ActivationDispatch(
                sender,
                eventArgs,
                handlers.GetInvocationList()
                    .Cast<EventHandler<NotificationActionActivatedEventArgs>>()
                    .ToArray()));
            shouldDrain = StartDrainingIfNeeded();
        }

        if (shouldDrain)
        {
            DrainDispatchQueue();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _actionActivated = null;
            _pendingActivations.Clear();
            _dispatchQueue.Clear();
        }
    }

    private bool StartDrainingIfNeeded()
    {
        if (_isDraining || _dispatchQueue.Count == 0)
        {
            return false;
        }

        _isDraining = true;
        return true;
    }

    private void DrainDispatchQueue()
    {
        while (true)
        {
            ActivationDispatch dispatch;
            lock (_gate)
            {
                if (_disposed || _dispatchQueue.Count == 0)
                {
                    _isDraining = false;
                    return;
                }

                dispatch = _dispatchQueue.Dequeue();
            }

            foreach (var handler in dispatch.Handlers)
            {
                InvokeSafely(handler, dispatch.Sender, dispatch.EventArgs);
            }
        }
    }

    private static void InvokeSafely(
        EventHandler<NotificationActionActivatedEventArgs> handler,
        object sender,
        NotificationActionActivatedEventArgs eventArgs)
    {
        try
        {
            handler(sender, eventArgs);
        }
        catch
        {
            // A consumer must not unwind into the native activation callback.
        }
    }

    private sealed record ActivationDispatch(
        object Sender,
        NotificationActionActivatedEventArgs EventArgs,
        EventHandler<NotificationActionActivatedEventArgs>[] Handlers);
}

/// <summary>
/// Windows native poster that follows the legacy WPF WinRT notification flow.
/// </summary>
internal sealed class WindowsToastNotificationPoster : INotificationPoster
{
    private const int PendingActivationLimit = 8;
    private const string ToolkitAssemblyFileName = "MAAUnified.WindowsToast.Toolkit.dll";
    private const string WindowsSdkAssemblyFileName = "Microsoft.Windows.SDK.NET.dll";
    private const string WinRtRuntimeAssemblyFileName = "WinRT.Runtime.dll";
    private static readonly Lazy<(AssemblyLoadContext Context, Assembly Assembly)> Toolkit = new(LoadToolkit);
    private readonly BufferedNotificationActivationSource _activationSource = new(PendingActivationLimit);
    private readonly EventInfo _activatedEvent;
    private readonly Delegate _activatedHandler;
    private int _disposed;

    private WindowsToastNotificationPoster()
    {
        var managerType = GetManagerType(Toolkit.Value.Assembly);
        _activatedEvent = managerType.GetEvent("OnActivated", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMemberException(managerType.FullName, "OnActivated");
        _activatedHandler = CreateActivationHandler(
            _activatedEvent.EventHandlerType
                ?? throw new InvalidOperationException("Windows Toast activation event has no handler type."));
        _activatedEvent.AddEventHandler(null, _activatedHandler);
    }

    public event EventHandler<NotificationActionActivatedEventArgs>? ActionActivated
    {
        add => _activationSource.ActionActivated += value;
        remove => _activationSource.ActionActivated -= value;
    }

    public PlatformCapabilityStatus Capability => new(
        Supported: true,
        Message: "System notification uses the Windows Toast backend.",
        Provider: "windows-toast",
        HasFallback: true,
        FallbackMode: "in-app");

    public static bool TryCreate([NotNullWhen(true)] out WindowsToastNotificationPoster? poster)
    {
        poster = null;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            return false;
        }

        WindowsToastNotificationPoster? candidate = null;
        try
        {
            candidate = new WindowsToastNotificationPoster();
            _ = CreateNotifier(Toolkit.Value.Assembly);
            poster = candidate;
            return true;
        }
        catch
        {
            try
            {
                candidate?.Dispose();
            }
            catch
            {
                // Initialization failure remains non-fatal even if toolkit cleanup also fails.
            }

            return false;
        }
    }

    public NotificationAvailabilityStatus GetAvailability()
    {
        try
        {
            var notifier = CreateNotifier(Toolkit.Value.Assembly);
            var setting = notifier.GetType().GetProperty("Setting")?.GetValue(notifier)?.ToString();
            return setting switch
            {
                "Enabled" => new NotificationAvailabilityStatus(true, string.Empty),
                "DisabledForApplication" => new NotificationAvailabilityStatus(
                    false,
                    "Notifications are disabled for this application.",
                    NotificationAvailabilityReason.DisabledForApplication),
                "DisabledForUser" => new NotificationAvailabilityStatus(
                    false,
                    "Notifications are disabled for this user.",
                    NotificationAvailabilityReason.DisabledForUser),
                "DisabledByGroupPolicy" => new NotificationAvailabilityStatus(
                    false,
                    "Notifications are disabled by group policy.",
                    NotificationAvailabilityReason.DisabledByGroupPolicy),
                "DisabledByManifest" => new NotificationAvailabilityStatus(
                    false,
                    "Notifications are disabled by the application manifest.",
                    NotificationAvailabilityReason.DisabledByManifest),
                _ => new NotificationAvailabilityStatus(
                    false,
                    $"Unknown notification setting: {setting ?? "unavailable"}",
                    NotificationAvailabilityReason.Unknown),
            };
        }
        catch (Exception ex)
        {
            return new NotificationAvailabilityStatus(
                false,
                ex.Message,
                NotificationAvailabilityReason.BackendUnavailable);
        }
    }

    public Task ShowAsync(SystemNotificationRequest notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();

        var toolkitAssembly = Toolkit.Value.Assembly;
        var builderType = toolkitAssembly.GetType(
            "Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder",
            throwOnError: true)!;
        var builder = Activator.CreateInstance(builderType)
            ?? throw new InvalidOperationException("Cannot create Windows Toast content builder.");

        // This matches WPF's NotificationImplWinRT: body first, then summary.
        Invoke(builder, "AddText", notification.Message);
        Invoke(builder, "AddText", notification.Title);

        var buttonType = toolkitAssembly.GetType(
            "Microsoft.Toolkit.Uwp.Notifications.ToastButton",
            throwOnError: true)!;
        foreach (var action in notification.Actions)
        {
            var button = Activator.CreateInstance(buttonType)
                ?? throw new InvalidOperationException("Cannot create Windows Toast action button.");
            Invoke(button, "SetContent", action.Label);
            Invoke(button, "AddArgument", action.Tag);
            Invoke(builder, "AddButton", button);
        }

        var toastContent = Invoke(builder, "GetToastContent")
            ?? throw new InvalidOperationException("Windows Toast content builder returned no content.");
        var activationType = toastContent.GetType().GetProperty("ActivationType")
            ?? throw new MissingMemberException(toastContent.GetType().FullName, "ActivationType");
        var activationTypeEnum = Nullable.GetUnderlyingType(activationType.PropertyType) ?? activationType.PropertyType;
        activationType.SetValue(toastContent, Enum.Parse(activationTypeEnum, "Protocol"));

        Invoke(builder, "Show");
        WindowsTaskbarAttention.TryFlashMainWindow();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _activatedEvent.RemoveEventHandler(null, _activatedHandler);
        }
        finally
        {
            _activationSource.Dispose();
        }
    }

    private static object CreateNotifier(Assembly toolkitAssembly)
    {
        var managerType = GetManagerType(toolkitAssembly);
        return Invoke(managerType, "CreateToastNotifier")
            ?? throw new InvalidOperationException("Cannot create Windows Toast notifier.");
    }

    private static Type GetManagerType(Assembly toolkitAssembly)
    {
        return toolkitAssembly.GetType(
            "Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat",
            throwOnError: true)!;
    }

    private Delegate CreateActivationHandler(Type handlerType)
    {
        var invokeMethod = handlerType.GetMethod("Invoke")
            ?? throw new MissingMethodException(handlerType.FullName, "Invoke");
        var parameters = invokeMethod.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        if (parameters.Length != 1)
        {
            throw new InvalidOperationException("Windows Toast activation event has an unexpected signature.");
        }

        var callback = typeof(WindowsToastNotificationPoster).GetMethod(
            nameof(OnActivated),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(WindowsToastNotificationPoster).FullName, nameof(OnActivated));
        var body = Expression.Call(Expression.Constant(this), callback, Expression.Convert(parameters[0], typeof(object)));
        return Expression.Lambda(handlerType, body, parameters).Compile();
    }

    private void OnActivated(object args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var argument = args.GetType().GetProperty("Argument", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(args)?.ToString() ?? string.Empty;
        var eventArgs = new NotificationActionActivatedEventArgs(argument);
        _activationSource.Publish(this, eventArgs);
    }

    private static object? Invoke(object target, string methodName, params object?[] arguments)
    {
        var targetType = target as Type ?? target.GetType();
        var bindingFlags = BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance);
        var method = targetType.GetMethods(bindingFlags).FirstOrDefault(candidate =>
        {
            if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
            {
                return false;
            }

            var parameters = candidate.GetParameters();
            return parameters.Length >= arguments.Length
                && parameters.Skip(arguments.Length).All(static parameter => parameter.HasDefaultValue)
                && parameters.Take(arguments.Length).Zip(arguments).All(pair =>
                    pair.Second is null || pair.First.ParameterType.IsInstanceOfType(pair.Second));
        }) ?? throw new MissingMethodException(targetType.FullName, methodName);

        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        Array.Copy(arguments, values, arguments.Length);
        for (var index = arguments.Length; index < parameters.Length; index++)
        {
            values[index] = parameters[index].DefaultValue;
        }

        return method.Invoke(target is Type ? null : target, values);
    }

    private static (AssemblyLoadContext Context, Assembly Assembly) LoadToolkit()
    {
        var context = new AssemblyLoadContext("MAAUnified.WindowsToast", isCollectible: false);
        LoadDependency(context, WinRtRuntimeAssemblyFileName);
        LoadDependency(context, WindowsSdkAssemblyFileName);
        return (context, LoadAssembly(context, ToolkitAssemblyFileName));
    }

    private static void LoadDependency(AssemblyLoadContext context, string fileName)
    {
        _ = LoadAssembly(context, fileName);
    }

    private static Assembly LoadAssembly(AssemblyLoadContext context, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Windows Toast dependency was not deployed: {fileName}", path);
        }

        return context.LoadFromAssemblyPath(path);
    }
}

internal static class WindowsTaskbarAttention
{
    private const uint FlashTray = 0x00000002;
    private const uint FlashTimerNoForeground = 0x0000000C;

    public static void TryFlashMainWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var windowHandle = process.MainWindowHandle;
            if (windowHandle == nint.Zero)
            {
                return;
            }

            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = windowHandle,
                Flags = FlashTray | FlashTimerNoForeground,
                Count = 5,
                Timeout = 0,
            };
            _ = FlashWindowEx(ref info);
        }
        catch
        {
            // Notification delivery should not fail when taskbar attention is unavailable.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }
}
