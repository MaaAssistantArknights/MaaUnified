namespace MAAUnified.Tests;

public sealed class CopilotViewStructureContractTests
{
    [Fact]
    public void CopilotView_ShouldKeepVisibleStartButtonAndExpandableFileDropdown()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));

        Assert.Contains("Content=\"{Binding StartButtonText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabStrip Classes=\"copilot-nav\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabStripItem Content=\"{Binding MainTabTitle}\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabStripItem Content=\"{Binding SecurityTabTitle}\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabStripItem Content=\"{Binding ParadoxTabTitle}\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabStripItem Content=\"{Binding OtherTabTitle}\" />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabControl Classes=\"copilot-nav\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"{Binding MainTabTitle}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Texts[Copilot.", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"开始\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanEdit}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<controls:AppCopilotPathDropdown", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:AppTreeSelectDropdown", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:CheckComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayFilename}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Watermark=\"{Binding PathOrCodeWatermark}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDropDownOpen=\"{Binding IsFilePopupOpen}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding FileItems}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EditorCommitted=\"OnFileSelectorEditorCommitted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionCommitted=\"OnFileSelectorSelectionCommitted\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnToggleFileNodeClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnFileNodeClick", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotView_ShouldNotEmbedOverlayButton_WhenWindowHostsSharedOverlayEntry()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));

        Assert.DoesNotContain("Click=\"OnToggleOverlayClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPressed=\"OnOverlayButtonPointerPressed\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContext.TaskQueuePage.OverlayButtonToolTip", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskQueue.Root.OverlayButton", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectSettingsView_ShouldUseStandardComboBox_ForConnectConfigSelection_AndAppHistoryInputForEditableAddress()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConnectSettingsView.axaml"));

        Assert.Contains("<controls:AppHistoryInput", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ConnectConfigOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedConnectConfigOption, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ConnectAddressHistory}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ConnectAddress, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemDeleted=\"OnConnectAddressItemDeleted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionCommitted=\"OnConnectAddressSelectionCommitted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EditorCommitted=\"OnConnectAddressEditorCommitted\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderText=\"{Binding SelectedConnectConfigOption.DisplayName}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:CheckComboBox", xaml, StringComparison.Ordinal);

        var connectConfigBindingIndex = xaml.IndexOf("ItemsSource=\"{Binding ConnectConfigOptions}\"", StringComparison.Ordinal);
        Assert.True(connectConfigBindingIndex >= 0, "Connect settings should bind the connect config selector to ConnectConfigOptions.");
        Assert.True(
            xaml.LastIndexOf("<ComboBox", connectConfigBindingIndex, StringComparison.Ordinal) >
            xaml.LastIndexOf("<controls:CheckComboBox", connectConfigBindingIndex, StringComparison.Ordinal),
            "Connect config selection should use the standard ComboBox rather than the custom CheckComboBox.");
    }

    [Fact]
    public void AppDropdownControls_ShouldUseOutlinedArrowAndSolidDropdownPanel()
    {
        var root = GetMaaUnifiedRoot();
        var multiSelectXaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppMultiSelect.axaml"));
        var copilotPathXaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppCopilotPathDropdown.axaml"));

        Assert.False(File.Exists(Path.Combine(root, "App", "Controls", "AppTreeSelectDropdown.axaml")));
        Assert.False(File.Exists(Path.Combine(root, "App", "Controls", "DelimitedTextListEditor.axaml")));

        foreach (var xaml in new[] { multiSelectXaml, copilotPathXaml })
        {
            Assert.Contains("Data=\"M 1 1 L 5 5 L 9 1\"", xaml, StringComparison.Ordinal);
            Assert.Contains("<Setter Property=\"Stroke\" Value=\"{DynamicResource MAA.Brush.App.SettingsInput.Chevron}\" />", xaml, StringComparison.Ordinal);
            Assert.Contains("Placement=\"BottomEdgeAlignedLeft\"", xaml, StringComparison.Ordinal);
            Assert.Contains("HorizontalAlignment=\"Left\"", xaml, StringComparison.Ordinal);
            Assert.Contains("<Setter Property=\"Background\" Value=\"{DynamicResource MAA.Brush.App.SettingsInput.PopupBackground}\" />", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("<Setter Property=\"Width\" Value=\"{DynamicResource MAA.App.Size.InputHeight}\" />", multiSelectXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"{DynamicResource MAA.App.Size.InputHeight}\" />", multiSelectXaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-multi-select-panel\"", multiSelectXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderBrush\" Value=\"Transparent\" />", multiSelectXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BoxShadow\" Value=\"{DynamicResource MAA.App.BoxShadow.Card}\" />", multiSelectXaml, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ToggleButton.app-multi-select-option:checked\"", multiSelectXaml, StringComparison.Ordinal);

        Assert.Contains("Classes=\"app-copilot-path-popup-panel\"", copilotPathXaml, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-copilot-path-toggle:pointerover\"", copilotPathXaml, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-copilot-path-row:pointerover Border.app-copilot-path-row-shell\"", copilotPathXaml, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-copilot-path-row.current Border.app-copilot-path-row-shell\"", copilotPathXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsControl x:Name=\"VisibleRowsControl\"", copilotPathXaml, StringComparison.Ordinal);
        Assert.Contains("Classes.current=\"{Binding IsCurrent}\"", copilotPathXaml, StringComparison.Ordinal);
        Assert.Contains("ShowCollapsedChevron", copilotPathXaml, StringComparison.Ordinal);
        Assert.Contains("ShowExpandedChevron", copilotPathXaml, StringComparison.Ordinal);
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }
}
