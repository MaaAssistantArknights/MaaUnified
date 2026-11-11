namespace MAAUnified.Tests;

public sealed class BaselineNoWaiverRemainingTests
{
    [Fact]
    public void KnownGaps_ShouldNotBeHiddenBehindWaivers()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var acceptance = BaselineTestSupport.LoadAcceptanceTemplate();

        Assert.All(baseline.Items.Where(item => item.ParityStatus == "Gap"), item => Assert.Null(item.Waiver));
        Assert.All(baseline.ConfigKeyMappings.Where(mapping => mapping.ParityStatus == "Gap"), mapping => Assert.Null(mapping.Waiver));
        Assert.All(baseline.FallbackCapabilities.Where(row => row.ParityStatus == "Gap"), row => Assert.Null(row.Waiver));
        Assert.All(acceptance.Cases.Where(testCase => testCase.Waiver is not null), testCase =>
            Assert.DoesNotContain(baseline.Items, item => item.ItemId == testCase.ItemId && item.ParityStatus == "Gap"));
    }
}
