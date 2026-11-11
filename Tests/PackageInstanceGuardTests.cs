using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Tests;

public sealed class PackageInstanceGuardTests
{
    [Fact]
    public void TryAcquire_WhenSameIdentityAlreadyHeld_ReturnsFalse()
    {
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";

        Assert.True(PackageInstanceGuard.TryAcquire(identity, out var firstGuard));
        try
        {
            Assert.NotNull(firstGuard);
            Assert.False(PackageInstanceGuard.TryAcquire(identity, out var secondGuard));
            Assert.Null(secondGuard);
        }
        finally
        {
            firstGuard?.Dispose();
        }
    }

    [Fact]
    public void TryAcquire_AfterFirstGuardDisposed_ReturnsTrue()
    {
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";

        Assert.True(PackageInstanceGuard.TryAcquire(identity, out var firstGuard));
        firstGuard!.Dispose();

        Assert.True(PackageInstanceGuard.TryAcquire(identity, out var secondGuard));
        secondGuard!.Dispose();
    }

    [Fact]
    public void TryAcquire_WhenMutexHeldExternally_WaitsForReleaseAndSucceeds()
    {
        // Restart flow spawns the successor process before the previous instance exits;
        // the guard must wait for the previous holder to release instead of failing
        // immediately (WPF Bootstrapper.HandleMultipleInstances waits 500ms).
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";
        using var externalMutex = new Mutex(initiallyOwned: false, BuildExpectedMutexName(identity));
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            externalMutex.WaitOne();
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            externalMutex.ReleaseMutex();
        });
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));

        // The holder releases 200ms into the wait; acquisition must succeed instead of
        // failing immediately on the still-held mutex.
        var releaser = new Thread(() =>
        {
            Thread.Sleep(200);
            release.Set();
        });
        releaser.Start();

        Assert.True(PackageInstanceGuard.TryAcquire(identity, TimeSpan.FromSeconds(10), out var guard));
        Assert.NotNull(guard);
        guard!.Dispose();
        holder.Join();
    }

    [Fact]
    public void TryAcquire_WhenMutexHeldExternallyBeyondTimeout_ReturnsFalseAfterWaiting()
    {
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";
        using var externalMutex = new Mutex(initiallyOwned: false, BuildExpectedMutexName(identity));
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            externalMutex.WaitOne();
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            externalMutex.ReleaseMutex();
        });
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            var timeout = TimeSpan.FromMilliseconds(400);
            var elapsed = Stopwatch.StartNew();
            Assert.False(PackageInstanceGuard.TryAcquire(identity, timeout, out var guard));
            elapsed.Stop();
            Assert.Null(guard);
            Assert.True(
                elapsed.Elapsed >= TimeSpan.FromMilliseconds(300),
                $"Expected the acquire attempt to wait for the timeout, but it returned after {elapsed.ElapsedMilliseconds}ms.");
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void DefaultAcquireTimeout_MatchesWpfWaitWindow()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), PackageInstanceGuard.DefaultAcquireTimeout);
    }

    private static string BuildExpectedMutexName(string keyMaterial)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
        return $"MAAUnified.Package.{Convert.ToHexString(hash)}";
    }


    [Fact]
    public void ReleaseProcessWideGuard_AfterAdopt_FreesMutexForSuccessorInstance()
    {
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";
        Assert.True(PackageInstanceGuard.TryAcquire(identity, out var guard));
        PackageInstanceGuard.AdoptProcessWideGuard(guard!);
        try
        {
            PackageInstanceGuard.ReleaseProcessWideGuard();

            Assert.True(PackageInstanceGuard.TryAcquire(identity, out var successor));
            successor!.Dispose();
        }
        finally
        {
            PackageInstanceGuard.ReleaseProcessWideGuard();
        }
    }

    [Fact]
    public void TryReacquireProcessWideGuard_AfterRelease_RestoresSingleInstanceProtection()
    {
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";
        Assert.True(PackageInstanceGuard.TryAcquire(identity, out var guard));
        PackageInstanceGuard.AdoptProcessWideGuard(guard!);
        try
        {
            PackageInstanceGuard.ReleaseProcessWideGuard();

            Assert.True(PackageInstanceGuard.TryReacquireProcessWideGuard());
            Assert.False(PackageInstanceGuard.TryAcquire(identity, out var duplicate));
            Assert.Null(duplicate);
        }
        finally
        {
            PackageInstanceGuard.ReleaseProcessWideGuard();
        }
    }
}
