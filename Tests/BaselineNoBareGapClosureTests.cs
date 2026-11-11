namespace MAAUnified.Tests;

public sealed class BaselineNoBareGapClosureTests
{
    [Fact]
    public void Baseline_GapsShouldBeExplicitAndActionable()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var bareItemGaps = baseline.Items
            .Where(item => string.Equals(item.ParityStatus, "Gap", StringComparison.Ordinal))
            .Where(item => item.Fields.Count == 0 || item.Fields.All(field => string.IsNullOrWhiteSpace(field.Notes)))
            .Select(item => item.ItemId)
            .ToList();
        var bareMappingGaps = baseline.ConfigKeyMappings
            .Where(mapping => string.Equals(mapping.ParityStatus, "Gap", StringComparison.Ordinal))
            .Where(mapping => string.IsNullOrWhiteSpace(mapping.Notes))
            .Select(mapping => $"{mapping.OwnerItemId}:{mapping.Key}")
            .ToList();
        var bareFallbackGaps = baseline.FallbackCapabilities
            .Where(row => string.Equals(row.ParityStatus, "Gap", StringComparison.Ordinal))
            .Where(row => string.IsNullOrWhiteSpace(row.Message))
            .Select(row => $"{row.CapabilityId}:{row.Platform}")
            .ToList();

        Assert.Empty(bareItemGaps);
        Assert.Empty(bareMappingGaps);
        Assert.Empty(bareFallbackGaps);
    }
}
