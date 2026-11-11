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

    private static string BuildMutexName(string keyMaterial)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
        return $"MAAUnified.Package.{Convert.ToHexString(hash)}";
    }
}
