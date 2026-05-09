using MAAUnified.App.ViewModels;

namespace MAAUnified.Tests;

public sealed class FeatureManifestTests
{
    [Fact]
    public void FeatureManifest_HasExpectedCoverage()
    {
        var all = FeatureManifest.All;
        Assert.Equal(38, all.Count);

        var duplicateKeys = all.GroupBy(m => m.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicateKeys);

        Assert.Contains(all, m => m.Key == "Settings.ConfigurationManager");
        Assert.Contains(all, m => m.Key == "Task.PostAction");
        Assert.Contains(all, m => m.Key == "Advanced.Copilot");
        Assert.Contains(all, m => m.Key == "Advanced.StageManager");
        Assert.Contains(all, m => m.Key == "Advanced.WebApi");
        Assert.Contains(all, m => m.Key == "Dialog.Error");
        Assert.DoesNotContain(all, m => m.Key == "Settings.HotKeyEditor");
        Assert.DoesNotContain(all, m => m.Key == "RootDashboard");
        Assert.DoesNotContain(all, m => m.Key == "Advanced.RemoteControlCenter");
        Assert.DoesNotContain(all, m => m.Key == "Advanced.Overlay");
        Assert.DoesNotContain(all, m => m.Key == "Advanced.TrayIntegration");
        Assert.DoesNotContain(all, m => m.Key == "Advanced.ExternalNotificationProviders");
    }
}
