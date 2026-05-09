namespace MAAUnified.Tests;

public sealed class AdvancedInputControlMigrationTests
{
    [Fact]
    public void AdvancedFirstBatchViews_ShouldUseSharedInputControls()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var advancedRoot = Path.Combine(root, "App", "Features", "Advanced");
        var files = new[]
        {
            "ExternalNotificationProvidersView.axaml",
            "StageManagerView.axaml",
            "WebApiView.axaml",
            "RemoteControlCenterView.axaml",
            "ToolboxMiniGameView.axaml",
        };

        foreach (var file in files)
        {
            var xaml = File.ReadAllText(Path.Combine(advancedRoot, file));

            Assert.Contains("xmlns:controls=\"clr-namespace:MAAUnified.App.Controls\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<TextBox", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<ComboBox", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<controls:VerticalSpinNumberBox", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Classes=\"app-input", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToolboxMiniGameSelector_ShouldOnlyKeepLayoutOverrides()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "App",
            "Features",
            "Advanced",
            "ToolboxMiniGameView.axaml"));

        Assert.Contains("Style Selector=\"ComboBox.toolbox-minigame-selector\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"BorderBrush\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedRemainingInputViews_ShouldUseSharedInputControls()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var advancedRoot = Path.Combine(root, "App", "Features", "Advanced");
        var files = new[]
        {
            "ToolboxRecruitView.axaml",
            "ToolboxPeepView.axaml",
            "TrayIntegrationView.axaml",
            "OverlayView.axaml",
        };

        foreach (var file in files)
        {
            var xaml = File.ReadAllText(Path.Combine(advancedRoot, file));

            Assert.Contains("xmlns:controls=\"clr-namespace:MAAUnified.App.Controls\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<TextBox", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<ComboBox", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<controls:VerticalSpinNumberBox", xaml, StringComparison.Ordinal);
        }
    }
}
