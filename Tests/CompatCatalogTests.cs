using MAAUnified.Compat.Mapping;

namespace MAAUnified.Tests;

public sealed class CompatCatalogTests
{
    [Fact]
    public void WpfBaselineAndLegacyCatalog_ShouldIncludeSingleStepTask()
    {
        Assert.Contains("SingleStep", WpfFeatureBaseline.TaskModules);
        Assert.Contains("SingleStepTask", LegacyTaskTypeCatalog.SupportedTaskTypes);
    }
}
