using System.Security.Cryptography;
using System.Text;

namespace MAAUnified.Compat.Runtime;

public sealed class PackageInstanceGuard : IDisposable
{
    // WPF parity (Bootstrapper.HandleMultipleInstances): wait briefly so a restart
    // successor process, which is spawned before the previous instance exits, can
    // acquire the guard once the previous instance releases it.
    internal static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Lock HeldKeysGate = new();
    private static readonly HashSet<string> HeldKeys = new(StringComparer.Ordinal);
    private static PackageInstanceGuard? s_processWideGuard;
    private static string? s_processWideKeyMaterial;
    private readonly Mutex _mutex;
    private bool _disposed;

    private PackageInstanceGuard(string keyMaterial, Mutex mutex)
    {
        KeyMaterial = keyMaterial;
        _mutex = mutex;
    }

    public string KeyMaterial { get; }

    public static bool TryAcquire(string keyMaterial, out PackageInstanceGuard? guard)
        => TryAcquire(keyMaterial, DefaultAcquireTimeout, out guard);

    public static bool TryAcquire(string keyMaterial, TimeSpan timeout, out PackageInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyMaterial);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        guard = null;
        lock (HeldKeysGate)
        {
            if (HeldKeys.Contains(keyMaterial))
            {
                return false;
            }
        }

        var mutex = new Mutex(initiallyOwned: false, BuildMutexName(keyMaterial));
        try
        {
            if (!mutex.WaitOne(timeout, exitContext: false))
            {
                mutex.Dispose();
                return false;
            }

            lock (HeldKeysGate)
            {
                if (!HeldKeys.Add(keyMaterial))
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                    return false;
                }
            }

            guard = new PackageInstanceGuard(keyMaterial, mutex);
            return true;
        }
        catch (AbandonedMutexException)
        {
            guard = new PackageInstanceGuard(keyMaterial, mutex);
            return true;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Ignore already-released mutexes during shutdown.
        }

        _mutex.Dispose();
        lock (HeldKeysGate)
        {
            HeldKeys.Remove(KeyMaterial);
        }
    }

    /// <summary>
    /// Adopts a guard as the process-wide single-instance guard so the restart
    /// flow can release it before spawning the successor process, mirroring the
    /// frozen WPF exit ordering (`Bootstrapper.OnExit` calls `Release()` first
    /// and only then starts the new process at the end of `OnExit`).
    /// </summary>
    public static void AdoptProcessWideGuard(PackageInstanceGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        lock (HeldKeysGate)
        {
            s_processWideGuard ??= guard;
            s_processWideKeyMaterial = guard.KeyMaterial;
        }
    }

    /// <summary>
    /// Releases the process-wide guard before a restart spawn so the child
    /// process can acquire the single-instance mutex while this instance is
    /// still tearing down. Safe to call when no guard was adopted.
    /// </summary>
    public static void ReleaseProcessWideGuard()
    {
        PackageInstanceGuard? guard;
        lock (HeldKeysGate)
        {
            guard = s_processWideGuard;
            s_processWideGuard = null;
        }

        guard?.Dispose();
    }

    /// <summary>
    /// Best-effort re-acquire after a restart spawn failed, so this instance
    /// keeps single-instance protection while it continues running.
    /// </summary>
    public static bool TryReacquireProcessWideGuard()
    {
        string? keyMaterial;
        lock (HeldKeysGate)
        {
            if (s_processWideGuard is not null)
            {
                return true;
            }

            keyMaterial = s_processWideKeyMaterial;
        }

        if (keyMaterial is null)
        {
            return false;
        }

        if (TryAcquire(keyMaterial, out var guard) && guard is not null)
        {
            AdoptProcessWideGuard(guard);
            return true;
        }

        return false;
    }

    internal static string BuildMutexName(string keyMaterial)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
        return $"MAAUnified.Package.{Convert.ToHexString(hash)}";
    }
}
