using MAAUnified.Application.Services;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Tests;

[CollectionDefinition("ProcessWideInstanceGuard", DisableParallelization = true)]
public sealed class ProcessWideInstanceGuardCollection;

[Collection("ProcessWideInstanceGuard")]
public sealed class ProcessAppLifecycleServiceTests
{
    [Fact]
    public async Task RestartAsync_ReleasesProcessWideGuardBeforeSpawningSuccessor()
    {
        // WPF parity: Bootstrapper.OnExit releases the single-instance mutex
        // first and only then starts the new process, so the child never fails
        // the guard while the old instance is still shutting down.
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";
        Assert.True(PackageInstanceGuard.TryAcquire(identity, out var guard));
        PackageInstanceGuard.AdoptProcessWideGuard(guard!);
        try
        {
            var spawnCount = 0;
            var guardFreeAtSpawn = false;
            var service = new ProcessAppLifecycleService(_ =>
            {
                spawnCount++;
                guardFreeAtSpawn = PackageInstanceGuard.TryAcquire(identity, out var successor);
                successor?.Dispose();
                return null;
            });

            var result = await service.RestartAsync();

            Assert.True(result.Success);
            Assert.Equal(1, spawnCount);
            Assert.True(guardFreeAtSpawn);
        }
        finally
        {
            PackageInstanceGuard.ReleaseProcessWideGuard();
        }
    }

    [Fact]
    public async Task RestartAsync_WhenSpawnFails_ReacquiresProcessWideGuard()
    {
        var identity = $"maa-unified-instance-{Guid.NewGuid():N}";
        Assert.True(PackageInstanceGuard.TryAcquire(identity, out var guard));
        PackageInstanceGuard.AdoptProcessWideGuard(guard!);
        try
        {
            var service = new ProcessAppLifecycleService(_ => throw new InvalidOperationException("spawn failed"));

            var result = await service.RestartAsync();

            Assert.False(result.Success);
            Assert.False(PackageInstanceGuard.TryAcquire(identity, out var duplicate));
            Assert.Null(duplicate);
        }
        finally
        {
            PackageInstanceGuard.ReleaseProcessWideGuard();
        }
    }

    [Fact]
    public async Task RestartAsync_WhenRestartAlreadyInitiated_DoesNotSpawnSecondSuccessor()
    {
        // WPF parity: the frozen bootstrapper starts the successor exactly once at
        // the end of OnExit, so a repeat Restart command while the old process is
        // still shutting down must not spawn a second child (two children would
        // race the single-instance mutex and the loser would exit with a spurious
        // duplicate-instance warning).
        PackageInstanceGuard.ReleaseProcessWideGuard();
        var spawnCount = 0;
        var service = new ProcessAppLifecycleService(_ =>
        {
            spawnCount++;
            return null;
        });

        var first = await service.RestartAsync();
        var second = await service.RestartAsync();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, spawnCount);
    }

    [Fact]
    public async Task RestartAsync_WhenSpawnFails_ReopensLatchSoRetryCanSpawn()
    {
        PackageInstanceGuard.ReleaseProcessWideGuard();
        var spawnCount = 0;
        var service = new ProcessAppLifecycleService(_ =>
        {
            spawnCount++;
            if (spawnCount == 1)
            {
                throw new InvalidOperationException("spawn failed");
            }

            return null;
        });

        var first = await service.RestartAsync();
        var second = await service.RestartAsync();

        Assert.False(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, spawnCount);
    }

    [Fact]
    public async Task RestartAsync_WithoutAdoptedProcessWideGuard_StillSpawnsSuccessor()
    {
        PackageInstanceGuard.ReleaseProcessWideGuard();
        var spawnCount = 0;
        var service = new ProcessAppLifecycleService(_ =>
        {
            spawnCount++;
            return null;
        });

        var result = await service.RestartAsync();

        Assert.True(result.Success);
        Assert.Equal(1, spawnCount);
    }
}
