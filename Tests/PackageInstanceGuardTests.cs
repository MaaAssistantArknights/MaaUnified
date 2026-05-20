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
}
