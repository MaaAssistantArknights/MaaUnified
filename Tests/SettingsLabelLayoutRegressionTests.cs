using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

public sealed class SettingsLabelLayoutRegressionTests
{
    [Fact]
    public void SettingsLabel_ShouldReserveStableHintColumnAndWrapText()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "SettingsLabel.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Controls", "SettingsLabel.axaml.cs"));

        Assert.Contains("ColumnDefinitions=\"Auto,Auto,Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PART_HintGap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"{DynamicResource MAA.App.Settings.HintMargin}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-label-tip\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasTip, ElementName=Root}\"", xaml, StringComparison.Ordinal);
        var styles = File.ReadAllText(Path.Combine(root, "App", "Styles", "SettingsShellStyles.axaml"));
        Assert.Contains("<Thickness x:Key=\"MAA.App.Settings.HintMargin\">8,0,0,0</Thickness>", styles, StringComparison.Ordinal);
        Assert.Contains("controls|SettingsLabel controls|TooltipHint.settings-label-tip", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"Margin\" Value=\"0\"", styles, StringComparison.Ordinal);
        Assert.Contains("nameof(Text)", code, StringComparison.Ordinal);
        Assert.Contains("nameof(Tip)", code, StringComparison.Ordinal);
        Assert.Contains("MeasureNaturalLabelWidth()", code, StringComparison.Ordinal);
        Assert.Contains("MeasureHintReserveWidth()", code, StringComparison.Ordinal);
        Assert.Contains("UpdateTextMaxWidth()", code, StringComparison.Ordinal);
        Assert.Contains("change.Property == BoundsProperty", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsLabelWidthCoordinator_ShouldClampMeasuredWidthToMaximum()
    {
        Assert.Equal(132d, SettingsLabelWidthCoordinator.CalculateSharedLabelWidth([40d, 120d], 220d));
        Assert.Equal(232d, SettingsLabelWidthCoordinator.CalculateSharedLabelWidth([40d, 260d], 220d));
        Assert.Equal(224d, SettingsLabelWidthCoordinator.CalculateSharedLabelWidth([260d], 220d, 4d));
        Assert.Equal(SettingsLabelWidthCoordinator.DefaultMaxLabelWidth + SettingsLabelWidthCoordinator.DefaultFieldGap, SettingsLabelWidthCoordinator.CalculateSharedLabelWidth([260d], double.NaN));
    }

    [Fact]
    public void SettingsLabelWidthCoordinator_ShouldRecalculateForTextAndTipButNotWindowResize()
    {
        var root = GetMaaUnifiedRoot();
        var labelCode = File.ReadAllText(Path.Combine(root, "App", "Controls", "SettingsLabel.axaml.cs"));
        var coordinatorCode = File.ReadAllText(Path.Combine(root, "App", "Controls", "SettingsLabelWidthCoordinator.cs"));

        Assert.Contains("change.Property == TextProperty", labelCode, StringComparison.Ordinal);
        Assert.Contains("change.Property == TipProperty", labelCode, StringComparison.Ordinal);
        Assert.Contains("SettingsLabelWidthCoordinator.InvalidateNearestGroup(this);", labelCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeChanged", coordinatorCode, StringComparison.Ordinal);
        Assert.DoesNotContain("BoundsProperty", coordinatorCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutUpdated", coordinatorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetSettingsViews_ShouldUseSharedLabelsAndGroupedWidths()
    {
        var root = GetMaaUnifiedRoot();
        var targetFiles = new[]
        {
            "ConnectSettingsView.axaml",
            "StartSettingsView.axaml",
            "GameSettingsView.axaml",
            "GuiSettingsView.axaml",
            "PerformanceSettingsView.axaml",
            "ConfigurationManagerView.axaml",
            "BackgroundSettingsView.axaml",
            "RemoteControlSettingsView.axaml",
            "VersionUpdateSettingsView.axaml",
            "HotKeySettingsView.axaml",
        };

        foreach (var file in targetFiles)
        {
            var text = ReadSettingsView(root, file);
            Assert.DoesNotContain("InlineUIContainer", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ColumnDefinitions=\"132,*\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ColumnDefinitions=\"144,*\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ColumnDefinitions=\"184,*\"", text, StringComparison.Ordinal);
            Assert.Contains("controls:SettingsLabel", text, StringComparison.Ordinal);
            Assert.Contains("controls:SettingsLabelWidthCoordinator.GroupKey", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConnectCoreRows_ShouldShareOneLocalLabelGroup()
    {
        var root = GetMaaUnifiedRoot();
        var connect = ReadSettingsView(root, "ConnectSettingsView.axaml");

        Assert.True(
            CountOccurrences(connect, "controls:SettingsLabelWidthCoordinator.GroupKey=\"Connect.AdbCore\"") >= 3,
            "Connection configuration, address, and ADB path should share one local label-width group.");
        Assert.Contains("Tip=\"{Binding RootTexts[Settings.Connect.AddressTip]}\"", connect, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalNotificationStackedCaptions_ShouldWrapWithoutJoiningGroupedLabelWidth()
    {
        var root = GetMaaUnifiedRoot();
        var external = ReadSettingsView(root, "ExternalNotificationSettingsView.axaml");

        Assert.Contains("external-notification-field-stack TextBlock.app-caption", external, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"TextWrapping\" Value=\"Wrap\" />", external, StringComparison.Ordinal);
        Assert.DoesNotContain("controls:SettingsLabelWidthCoordinator.GroupKey", external, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsStyles_ShouldApplyMaximumLabelWidthTokenToGroupedRows()
    {
        var root = GetMaaUnifiedRoot();
        var controlStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));
        var settingsStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "SettingsShellStyles.axaml"));

        Assert.Contains("<x:Double x:Key=\"MAA.Size.Settings.LabelMaxWidth\">220</x:Double>", controlStyles, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Settings.LabelFieldGap\">12</x:Double>", controlStyles, StringComparison.Ordinal);
        Assert.Contains("Property=\"controls:SettingsLabelWidthCoordinator.MaxLabelWidth\" Value=\"{DynamicResource MAA.Size.Settings.LabelMaxWidth}\"", settingsStyles, StringComparison.Ordinal);
        Assert.Contains("Property=\"controls:SettingsLabelWidthCoordinator.FieldGap\" Value=\"{DynamicResource MAA.Size.Settings.LabelFieldGap}\"", settingsStyles, StringComparison.Ordinal);
    }

    private static string ReadSettingsView(string root, string file)
    {
        return File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", file));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }
}
