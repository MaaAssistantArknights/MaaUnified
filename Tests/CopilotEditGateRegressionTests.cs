namespace MAAUnified.Tests;

public sealed class CopilotEditGateRegressionTests
{
    [Fact]
    public void CopilotView_ShouldGateEditingSurfacesAndBattleListRowsByCanEdit()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = BaselineTestSupport.NormalizeLineEndings(
            File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml")));

        var canEditRowBinding =
            "IsEnabled=\"{Binding DataContext.CanEdit, RelativeSource={RelativeSource AncestorType=UserControl}}\"";
        var hitTestRowBinding =
            "IsHitTestVisible=\"{Binding DataContext.CanEdit, RelativeSource={RelativeSource AncestorType=UserControl}}\"";

        Assert.Contains("IsEnabled=\"{Binding CanEdit}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"{Binding CanEdit}\"", xaml, StringComparison.Ordinal);

        var checkbox = ExtractElement(xaml, "<CheckBox Grid.Column=\"1\"", "</CheckBox>");
        Assert.Contains(canEditRowBinding, checkbox, StringComparison.Ordinal);
        Assert.Contains(hitTestRowBinding, checkbox, StringComparison.Ordinal);

        var body = ExtractElement(xaml, "Classes=\"copilot-list-item-body\"", "</Border>");
        Assert.Contains(canEditRowBinding, body, StringComparison.Ordinal);
        Assert.Contains(hitTestRowBinding, body, StringComparison.Ordinal);

        var deleteHotspot = ExtractElement(xaml, "copilot-list-delete-hotspot", "</Border>");
        Assert.Contains(canEditRowBinding, deleteHotspot, StringComparison.Ordinal);
        Assert.Contains(hitTestRowBinding, deleteHotspot, StringComparison.Ordinal);
    }

    private static string ExtractElement(string xaml, string startMarker, string endMarker)
    {
        var start = xaml.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker `{startMarker}`.");
        var end = xaml.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker `{endMarker}` after `{startMarker}`.");
        return xaml[start..end];
    }
}
