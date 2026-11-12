using System.Runtime.InteropServices;

namespace MAAUnified.Platform;

/// <summary>
/// Keep-awake leases for the post-action countdown.
/// WPF parity: <c>Utilities/SleepManagement.cs</c> at 0b048d5e94 (<c>CreateKeepAwakeRequest</c>)
/// hands the post-action countdown an independently owned power request so an unattended
/// machine does not idle-sleep and freeze the countdown.
/// <para>
/// Implementation scope: only the Windows power-request API (<c>PowerCreateRequest</c> /
/// <c>PowerSetRequest</c> / <c>PowerClearRequest</c>) is implemented, because it is the API the
/// WPF implementation uses and there is no cross-platform equivalent without extra native
/// dependencies. macOS and Linux are explicit degradations: the lease factory returns
/// <see langword="null"/>, <see cref="Capability"/> reports the server platform as unsupported and
/// the countdown keeps running without sleep protection. Callers must treat a null lease as
/// "countdown continues without protection", never as an error.
/// </para>
/// </summary>
public sealed class SleepManagementService : IKeepAwakeService
{
    // PowerRequestType.PowerRequestSystemRequired
    private const int PowerRequestSystemRequired = 1;

    private readonly Func<string, nint> _createRequest;
    private readonly Action<nint> _clearRequest;

    public SleepManagementService()
        : this(CreatePlatformRequest, ClearPlatformRequest)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SleepManagementService"/> class with a
    /// replaceable backend so the lease lifecycle is testable without invoking Windows APIs.
    /// </summary>
    /// <param name="platformCreate">Backend that acquires the keep-awake request; 0 means "unsupported".</param>
    /// <param name="platformClear">Backend that releases a previously acquired request.</param>
    internal SleepManagementService(Func<string, nint> platformCreate, Action<nint> platformClear)
    {
        ArgumentNullException.ThrowIfNull(platformCreate);
        ArgumentNullException.ThrowIfNull(platformClear);
        _createRequest = platformCreate;
        _clearRequest = platformClear;
        Capability = BuildCapability();
    }

    public PlatformCapabilityStatus Capability { get; }

    public IDisposable? CreateKeepAwakeRequest(string reason)
    {
        var handle = _createRequest(reason ?? string.Empty);
        return handle == 0 ? null : new KeepAwakeLease(handle, _clearRequest);
    }

    private static PlatformCapabilityStatus BuildCapability()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformCapabilityStatus(
                true,
                "Keep-awake is available via the Windows power request API.",
                Provider: "windows-power-request");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PlatformCapabilityStatus(
                false,
                "Keep-awake is not implemented on macOS (placeholder); the post-action countdown runs without sleep protection.",
                Provider: "macos-placeholder",
                HasFallback: true,
                FallbackMode: "countdown-without-sleep-protection");
        }

        if (OperatingSystem.IsLinux())
        {
            return new PlatformCapabilityStatus(
                false,
                "Keep-awake is not implemented on Linux (placeholder); the post-action countdown runs without sleep protection.",
                Provider: "linux-placeholder",
                HasFallback: true,
                FallbackMode: "countdown-without-sleep-protection");
        }

        return new PlatformCapabilityStatus(
            false,
            "Keep-awake is unsupported on the current platform.",
            Provider: "unsupported",
            HasFallback: true,
            FallbackMode: "countdown-without-sleep-protection");
    }

    private static nint CreatePlatformRequest(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        nint request = 0;
        try
        {
            var context = new PowerRequestContextSimple
            {
                Version = 0,
                Flags = 1,
                SimpleReasonString = string.IsNullOrWhiteSpace(reason) ? "MAA keep awake" : reason,
            };

            request = PowerCreateRequest(ref context);
            if (request == 0 || request == -1)
            {
                return 0;
            }

            if (!PowerSetRequest(request, PowerRequestSystemRequired))
            {
                CloseHandle(request);
                return 0;
            }

            return request;
        }
        catch
        {
            if (request != 0 && request != -1)
            {
                try
                {
                    CloseHandle(request);
                }
                catch
                {
                    // Best effort; the request handle is already unusable.
                }
            }

            return 0;
        }
    }

    private static void ClearPlatformRequest(nint request)
    {
        if (request == 0)
        {
            return;
        }

        try
        {
            PowerClearRequest(request, PowerRequestSystemRequired);
        }
        catch
        {
            // Best effort: the OS reclaims the request when the process exits.
        }
        finally
        {
            try
            {
                CloseHandle(request);
            }
            catch
            {
                // Best effort handle cleanup.
            }
        }
    }

    private sealed class KeepAwakeLease : IDisposable
    {
        private readonly Action<nint> _clearRequest;
        private nint _request;

        internal KeepAwakeLease(nint request, Action<nint> clearRequest)
        {
            _request = request;
            _clearRequest = clearRequest;
        }

        public void Dispose()
        {
            var request = Interlocked.Exchange(ref _request, 0);
            if (request == 0)
            {
                return;
            }

            _clearRequest(request);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerRequestContextSimple
    {
        public uint Version;

        public uint Flags;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref PowerRequestContextSimple context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
