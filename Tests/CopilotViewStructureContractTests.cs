namespace MAAUnified.Tests;

public sealed class CopilotViewStructureContractTests
{
    [Fact]
    public void CopilotView_ShouldKeepVisibleStartButtonAndExpandableFileDropdown()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = BaselineTestSupport.NormalizeLineEndings(
            File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml")));

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
        Assert.Contains("IsVisible=\"{Binding ShowStartButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LoadedCopilotInputHint}\"", xaml, StringComparison.Ordinal);
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
    public void CopilotView_BattleList_ShouldUseSelectionListContractWithoutLegacyLoadButton()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = BaselineTestSupport.NormalizeLineEndings(
            File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml")));

        Assert.Contains("<controls:AppSelectionList", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Items}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedItem", xaml, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"{Binding CanEdit}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemReorderRequested=\"OnCopilotListItemReorderRequested\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"copilot-battle-list selection-list-section-cards selection-list-no-indicator\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:ReorderableList", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("</controls:ReorderableList", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Reordered=\"OnCopilotListReordered\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("controls|ReorderableList", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnLoadListItemClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnLoadListItemPointerPressed", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"{Binding DataContext.LoadButtonText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotView_BattleListItems_ShouldExposeCardActivationContextMenuAndCompactDelete()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));

        Assert.Contains("Tapped=\"OnCopilotListItemBodyTapped\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"OnCopilotListItemBodyPointerPressed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Popup x:Name=\"CopilotListActionPopup\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Placement=\"Pointer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsControl x:Name=\"CopilotListActionPopupItems\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"OnCopilotListActionPopupItemPointerPressed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Header}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnDeleteListItemPointerPressed", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsChecked", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-row-action copilot-list-delete-hotspot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-row-action-glyph\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"×\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"app-button app-secondary copilot-list-delete-hotspot\"", xaml, StringComparison.Ordinal);

        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml.cs"));
        Assert.DoesNotContain("CopilotListPopupAction.ToggleChecked", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CopilotListDisableButtonText", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CopilotListEnableButtonText", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotView_BattleListFooter_ShouldKeepTwoRowsAndConfirmedClearAllEntry()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));

        Assert.Contains("Classes=\"task-queue-list-actions\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-list-action-stack\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RowDefinitions=\"Auto,*,Auto,Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Border Grid.Row=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-list-action-divider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"grouped-card-footer-action", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"copilot-list-task-name-display\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding ImportBatchButtonText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CopilotTaskName", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:AppTextInput Grid.Column=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding AddListButtonText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding ClearAllButtonText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnClearListClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RowDefinitions=\"Auto,8,Auto\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"-8,14,-8,-8\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPressed=\"OnClearListPointerPressed\"", xaml, StringComparison.Ordinal);
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
        Assert.Contains("<controls:AppSelect", xaml, StringComparison.Ordinal);

        var connectConfigBindingIndex = xaml.IndexOf("ItemsSource=\"{Binding ConnectConfigOptions}\"", StringComparison.Ordinal);
        Assert.True(connectConfigBindingIndex >= 0, "Connect settings should bind the connect config selector to ConnectConfigOptions.");
        Assert.True(
            xaml.LastIndexOf("<controls:AppSelect", connectConfigBindingIndex, StringComparison.Ordinal) >
            xaml.LastIndexOf("<controls:CheckComboBox", connectConfigBindingIndex, StringComparison.Ordinal),
            "Connect config selection should use AppSelect, the shared standard ComboBox wrapper, rather than the custom CheckComboBox.");
    }

    [Fact]
    public void AppDropdownControls_ShouldUseOutlinedArrowAndSolidDropdownPanel()
    {
        var root = GetMaaUnifiedRoot();
        var multiSelectXaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppMultiSelect.axaml"));
        var multiSelectDropdownXaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppMultiSelectDropdown.axaml"));
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
        Assert.Contains("<Setter Property=\"Template\">", multiSelectXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{TemplateBinding Content}\"", multiSelectXaml, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-dropdown-arrow\"", multiSelectDropdownXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Template\">", multiSelectDropdownXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{TemplateBinding Content}\"", multiSelectDropdownXaml, StringComparison.Ordinal);

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
