using Xunit;

namespace MAAUnified.Tests;

public sealed class AchievementCatalogParityTests
{
    [Fact]
    public async Task Snapshot_ShouldContainWpfDevV2ThirdPhaseAchievements()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-achievements", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tracker = new MAAUnified.Application.Services.Features.AchievementTrackerService(configService: null, root);
            await AssertCatalogAsync(tracker);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertCatalogAsync(MAAUnified.Application.Services.Features.AchievementTrackerService tracker)
    {
        string[] thirdPhase =
        [
            "AchievementObserver", "AllChannelBroadcast", "BackstageExplorer", "ChangelogReader",
            "ConnectionTester", "DisappearTrick", "DoubleSync", "LatestVersionInspector",
            "LoadLastSave", "MasterDirector", "OneMoreLook", "OperatorRoster", "PallasCheers",
            "PallasStarter", "PrivateDormManager", "QueueExpansion", "QueueSimplifier", "ResumeRecord",
            "SlackingOff", "SortingMaster", "TimeMachine", "TimeManagementMaster", "TitleTweaker",
        ];
        foreach (var id in thirdPhase)
        {
            Assert.True(tracker.Unlock(id).Success, id);
        }

        var snapshot = await tracker.GetSnapshotAsync("en-us");
        Assert.True(snapshot.Success);
        var ids = snapshot.Value!.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in thirdPhase)
        {
            Assert.Contains(id, ids);
        }

        Assert.Equal(111, snapshot.Value.TotalCount);
    }
}
