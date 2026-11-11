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
    public async Task TryAcquire_WithWaitBudget_AcquiresAfterAnotherThreadReleasesMutex()
    {
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";
        using var externallyHeldMutex = new Mutex(initiallyOwned: false, BuildMutexName(identity));
        Assert.True(externallyHeldMutex.WaitOne());

        try
        {
            var acquireTask = Task.Run(() =>
            {
                var acquired = PackageInstanceGuard.TryAcquire(identity, TimeSpan.FromSeconds(2), out var guard);
                return (acquired, guard);
            });

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
            Assert.False(acquireTask.IsCompleted);

            externallyHeldMutex.ReleaseMutex();
            var result = await acquireTask;
            Assert.True(result.acquired);
            result.guard!.Dispose();
        }
        finally
        {
            try
            {
                externallyHeldMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was already released on the successful path.
            }
        }
    }

    private static string BuildMutexName(string keyMaterial)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
        return $"MAAUnified.Package.{Convert.ToHexString(hash)}";
    }
}
