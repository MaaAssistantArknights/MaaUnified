namespace MAAUnified.Tests;

public sealed class RootViewStructureContractTests
{
    [Fact]
    public void MainWindow_ShouldUseMainShellTabTitleBindings_ForTabsAndMenus()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml"));

        Assert.Contains("{Binding TaskQueueTabTitle}", text, StringComparison.Ordinal);
        Assert.Contains("{Binding CopilotTabTitle}", text, StringComparison.Ordinal);
        Assert.Contains("{Binding ToolboxTabTitle}", text, StringComparison.Ordinal);
        Assert.Contains("{Binding SettingsTabTitle}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[Main.Tab.Advanced]}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[Main.Tab.TaskQueue]}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[Main.Tab.Copilot]}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[Main.Tab.Toolbox]}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[Main.Tab.Settings]}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[Main.Menu.Start]}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[Main.Menu.SwitchLanguage]}", text, StringComparison.Ordinal);
        Assert.Contains("Title=\"{Binding WindowTitle}\"", text, StringComparison.Ordinal);
        Assert.Contains("<TabStrip Classes=\"root-nav\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnWindowOverlayToggleClick\"", text, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"OnWindowOverlayButtonPointerPressed\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowWindowOverlayButton}\"", text, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding TaskQueuePage.OverlayButtonToolTip}\"", text, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TaskQueuePage.OverlayButtonText}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"main-shell-floating-overlays\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"main-shell-update-overlay-card main-shell-floating-card interactive app-surface app-card\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnDismissWindowUpdateClick\"", text, StringComparison.Ordinal);
        Assert.Contains("HasVisibleWindowUpdateInfo", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.main-shell-update-action\"", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"NaN\" />", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TextBlock.main-shell-update-description\"", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"Normal\" />", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"achievement-toast-host achievement-toast-overlay-host\"", text, StringComparison.Ordinal);
        Assert.Contains("PointerEntered=\"OnAchievementToastPointerEntered\"", text, StringComparison.Ordinal);
        Assert.Contains("PointerExited=\"OnAchievementToastPointerExited\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAchievementToastClosePointerEntered", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAchievementToastClosePointerExited", text, StringComparison.Ordinal);
        Assert.Contains("<DataTemplate DataType=\"viewModels:RootPageHostViewModel\">", text, StringComparison.Ordinal);
        Assert.Contains("<DataTemplate DataType=\"taskVm:TaskQueuePageViewModel\">", text, StringComparison.Ordinal);
        Assert.Contains("<DataTemplate DataType=\"copilotVm:CopilotPageViewModel\">", text, StringComparison.Ordinal);
        Assert.Contains("<DataTemplate DataType=\"toolboxVm:ToolboxPageViewModel\">", text, StringComparison.Ordinal);
        Assert.Contains("<DataTemplate DataType=\"settingsVm:SettingsPageViewModel\">", text, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding TaskQueueRootPage.PageContent}\"", text, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding CopilotRootPage.PageContent}\"", text, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding ToolboxRootPage.PageContent}\"", text, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SettingsRootPage.PageContent}\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsTaskQueueRootTabSelected}\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsCopilotRootTabSelected}\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsToolboxRootTabSelected}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-root-host\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.settings-root-host-selected=\"{Binding IsSettingsRootTabSelected}\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ContentControl.settings-root-host\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<rootViews:TaskQueueView DataContext=\"{Binding TaskQueuePage}\" />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<advancedViews:CopilotView DataContext=\"{Binding CopilotPage}\" />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<advancedViews:ToolboxView DataContext=\"{Binding ToolboxPage}\" />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<rootViews:SettingsView DataContext=\"{Binding SettingsPage}\" />", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueView_ShouldBindRootTextsAndKeepCoreRunContracts()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml.cs"));

        Assert.Contains("Text=\"{Binding TaskListTitleText}\"", text, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TaskConfigTitleText}\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskSettingsHost\"", text, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanEdit}\"", text, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SelectedTaskSettingsViewModel}\"", text, StringComparison.Ordinal);
        Assert.Contains("DataType=\"taskVm:StartUpTaskModuleViewModel\"", text, StringComparison.Ordinal);
        Assert.Contains("DataType=\"taskVm:FightTaskModuleViewModel\"", text, StringComparison.Ordinal);
        Assert.Contains("DataType=\"taskVm:InfrastModuleViewModel\"", text, StringComparison.Ordinal);
        Assert.Contains("DataType=\"taskVm:SingleStepModuleViewModel\"", text, StringComparison.Ordinal);
        Assert.Contains("DataType=\"taskVm:PostActionModuleViewModel\"", text, StringComparison.Ordinal);
        Assert.Contains("<taskViews:PostActionSettingsView />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding TaskPanels}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"{Binding ModuleViewModel}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=\"{Binding IsSelected}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=\"{Binding ShowPostActionSettingsPanel}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskSettingsHost.Content =", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrCreateTaskSettingsView", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanToggleRun}\"", text, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CoreInitializationMessage}\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasCoreInitializationMessage}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"grouped-card-frame compact task-queue-list-frame\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"grouped-card-footer-action task-queue-card-action\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"grouped-card-footer-action task-queue-card-action grouped-card-footer-post-action task-queue-post-action-row\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("task-queue-card-action app-button", text, StringComparison.Ordinal);
        Assert.DoesNotContain("task-queue-post-action-row app-button", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"app-button task-queue-card-action\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-list-action-separator\"", text, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList x:Name=\"TaskListBox\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-list selection-list-section-cards selection-list-no-indicator\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"controls|AppSelectionList.task-queue-list Border.task-queue-item-card\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-running=\"{Binding IsStatusRunning}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-success=\"{Binding IsStatusSuccess}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-error=\"{Binding IsStatusError}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-skipped=\"{Binding IsStatusSkipped}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-idle=\"{Binding IsStatusIdle}\"", text, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LogsTitleText}\"", text, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LogCards}\"", text, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Items}\"", text, StringComparison.Ordinal);
        Assert.Contains("PointerEntered=\"OnLogThumbnailPointerEntered\"", text, StringComparison.Ordinal);
        Assert.Contains("PointerExited=\"OnLogThumbnailPointerExited\"", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"TextWrapping\" Value=\"NoWrap\" />", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-log-image-popup\"", text, StringComparison.Ordinal);
        Assert.Contains("<DoubleTransition Property=\"Opacity\"", text, StringComparison.Ordinal);
        Assert.Contains("Duration=\"0:0:0.1\"", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"854\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"480\" />", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowTimeOnlyLayout}\"", text, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnOpenPostActionClick\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnOpenButtonContextMenuClick\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnBatchActionClick\"", text, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"OnBatchActionPointerPressed\"", text, StringComparison.Ordinal);
        var foundationStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));
        Assert.Contains("Style Selector=\"Border.task-queue-row-action\"", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.task-queue-row-action:pointerover\"", foundationStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Style Selector=\"Border.task-queue-row-action\"", text, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"OnTaskGearPointerPressed\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskSettingsViewWarmupOrder", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskQueue.Root.AutoReload", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding AutoReload}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OnInverseClick\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskQueue.Root.AdvancedMode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowAdvanced", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding OverlayStatusText}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding OverlayTargetSummaryText}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding RootTexts[TaskQueue.Root.RuntimeTitle]}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueTaskViews_ShouldUseVerticalSettingsLayout_AndHidePostActionCommandInputs()
    {
        var root = GetMaaUnifiedRoot();
        var infrastText = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "InfrastSettingsView.axaml"));
        var taskViewFiles = new[]
        {
            "App/Features/TaskQueue/StartUpTaskView.axaml",
            "App/Features/TaskQueue/FightSettingsView.axaml",
            "App/Features/TaskQueue/RecruitSettingsView.axaml",
            "App/Features/TaskQueue/InfrastSettingsView.axaml",
            "App/Features/TaskQueue/MallSettingsView.axaml",
            "App/Features/TaskQueue/AwardSettingsView.axaml",
            "App/Features/TaskQueue/RoguelikeSettingsView.axaml",
            "App/Features/TaskQueue/ReclamationSettingsView.axaml",
            "App/Features/TaskQueue/CustomSettingsView.axaml",
            "App/Features/TaskQueue/PostActionSettingsView.axaml",
        };

        foreach (var file in taskViewFiles)
        {
            var fullPath = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(fullPath);
            if (file.EndsWith("FightSettingsView.axaml", StringComparison.Ordinal))
            {
                Assert.Contains("<WrapPanel", text, StringComparison.Ordinal);
                Assert.DoesNotContain("<UniformGrid", text, StringComparison.Ordinal);
                Assert.Contains("Content=\"{Binding WeeklyScheduleSundayText}\"", text, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("<WrapPanel", text, StringComparison.Ordinal);
            }

            if (file.EndsWith("RecruitSettingsView.axaml", StringComparison.Ordinal))
            {
                // Recruit has one intentional horizontal row (checkbox + inline hint text).
                var horizontalCount = System.Text.RegularExpressions.Regex.Matches(text, "Orientation=\"Horizontal\"").Count;
                Assert.Equal(1, horizontalCount);
                Assert.Contains("Content=\"{Binding Texts[Recruit.AutoSelectLevel6]}\"", text, StringComparison.Ordinal);
                Assert.Contains("Text=\"{Binding Texts[Recruit.AutoSelectLevel6FixedTime]}\"", text, StringComparison.Ordinal);
                Assert.Contains("Classes=\"recruit-time-picker\"", text, StringComparison.Ordinal);
            }
            else
            {
                // For task settings: keep a predictable vertical form layout (avoid horizontal stack layouts drifting in).
                Assert.DoesNotContain("Orientation=\"Horizontal\"", text, StringComparison.Ordinal);
            }
        }

        var startUpText = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "StartUpTaskView.axaml"));
        Assert.Contains("IsVisible=\"{Binding ShowAccountSwitch}\"", startUpText, StringComparison.Ordinal);
        Assert.Contains("<controls:SettingsInlineRow Classes=\"startup-card-content\"", startUpText, StringComparison.Ordinal);
        Assert.Contains("Texts[StartUp.AccountSwitchManualRun]", startUpText, StringComparison.Ordinal);

        Assert.Contains("Classes=\"grouped-card-frame compact infrast-facility-frame\"", infrastText, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList x:Name=\"FacilitySelectionList\"", infrastText, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"True\"", infrastText, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-selection-list-item-shell\"", infrastText, StringComparison.Ordinal);
        Assert.DoesNotContain("<ListBox Classes=\"infrast-facility-list\"", infrastText, StringComparison.Ordinal);

        var postActionText = File.ReadAllText(
            Path.Combine(
                root,
                "App",
                "Features",
                "TaskQueue",
                "PostActionSettingsView.axaml"));
        Assert.DoesNotContain("ExitArknightsCommand", postActionText, StringComparison.Ordinal);
        Assert.DoesNotContain("BackToAndroidHomeCommand", postActionText, StringComparison.Ordinal);
        Assert.DoesNotContain("ExitEmulatorCommand", postActionText, StringComparison.Ordinal);
        Assert.DoesNotContain("ExitSelfCommand", postActionText, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowExitEmulator}\"", postActionText, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowHibernate}\"", postActionText, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanUseIfNoOtherMaa}\"", postActionText, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueRemainingViews_ShouldUseSharedInputControls()
    {
        var root = GetMaaUnifiedRoot();
        var migratedTaskViewFiles = new[]
        {
            "CustomSettingsView.axaml",
            "MallSettingsView.axaml",
            "UserDataUpdateSettingsView.axaml",
            "InfrastSettingsView.axaml",
            "ReclamationSettingsView.axaml",
            "RecruitSettingsView.axaml",
        };

        foreach (var file in migratedTaskViewFiles)
        {
            var text = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", file));
            Assert.DoesNotContain("<TextBox", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<ComboBox", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ComboBox.ItemTemplate", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<controls:VerticalSpinNumberBox", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Classes=\"app-input", text, StringComparison.Ordinal);
            Assert.Contains("xmlns:controls=\"clr-namespace:MAAUnified.App.Controls\"", text, StringComparison.Ordinal);
        }

        var recruitText = File.ReadAllText(
            Path.Combine(root, "App", "Features", "TaskQueue", "RecruitSettingsView.axaml"));
        Assert.Contains("<controls:AppMultiSelect", recruitText, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:AppMultiSelectDropdown", recruitText, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:CheckComboBox", recruitText, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastSettingsView_ShouldUseSharedSelectionListForFacilityOrder()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "InfrastSettingsView.axaml"));

        Assert.Contains("Classes=\"grouped-card-frame compact infrast-facility-frame\"", text, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList x:Name=\"FacilitySelectionList\"", text, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"True\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-selection-list-item-shell\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<ListBox Classes=\"infrast-facility-list\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FightSettingsView_ShouldUseSharedSelectionListForStagePlan()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "FightSettingsView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "FightSettingsView.axaml.cs"));

        Assert.Contains("IsVisible=\"{Binding ShowStagePlanSelector}\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowStagePlanList}\"", text, StringComparison.Ordinal);
        Assert.Contains("SelectedValueBinding=\"{Binding Value}\"", text, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedStageOption, Mode=OneWay}\"", text, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding SelectedStageValue, Mode=TwoWay}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem=\"{Binding SelectedStageOption, Mode=TwoWay}\"", text, StringComparison.Ordinal);
        Assert.Contains("FieldWidth=\"{DynamicResource MAA.Size.Settings.FieldCenteredWidth}\"", text, StringComparison.Ordinal);
        Assert.Contains("FieldWidth=\"222\"", text, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList x:Name=\"StagePlanSelectionList\"", text, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding StagePlan}\"", text, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"{Binding UseAlternateStage}\"", text, StringComparison.Ordinal);
        Assert.Contains("CanReorderFromComboBox=\"True\"", text, StringComparison.Ordinal);
        Assert.Contains("ItemReorderRequested=\"OnStagePlanItemReorderRequested\"", text, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList.ReorderDragPreviewContentTemplate>", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"grouped-card-frame compact stage-plan-frame\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-compact-selection-list stage-plan-selection-list selection-list-section-cards selection-list-no-indicator\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"fight-settings-stage-field stage-plan-embedded-select\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-selection-list-item-shell\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-dropdown-delete-button stage-plan-delete-button\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAddStagePlanEntryClick\"", text, StringComparison.Ordinal);
        Assert.Contains("MoveStagePlanEntry(e.SourceIndex, e.TargetIndex)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("OnMoveStagePlanEntryUpClick", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OnMoveStagePlanEntryDownClick", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OnMoveStagePlanEntryUpClick", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("OnMoveStagePlanEntryDownClick", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_ShouldUseLazySectionHosts_AndKeepSharedScrollSurface()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml.cs"));
        var styles = File.ReadAllText(Path.Combine(root, "App", "Styles", "SettingsShellStyles.axaml"));

        Assert.Contains("ItemsSource=\"{Binding Sections}\"", text, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedSection}\"", text, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"OnSectionSelectionChanged\"", text, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList", text, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"Rail\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SectionScrollViewer\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SectionContentPanel\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Spacing=\"28\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StickyTitlePanel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StickyCurrentHost", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StickyTransitionHost", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StickyTitleText", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StickyTransitionText", text, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-sticky-title", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AppStickyTitlePresenter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-page-sticky-title", text, StringComparison.Ordinal);
        Assert.Contains("ScrollChanged=\"OnSectionScrollChanged\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SectionConfigurationManager\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SectionAbout\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding CurrentSectionActions}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSectionActionClick", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding SelectedSectionTitle}\"", text, StringComparison.Ordinal);

        var sectionHostCount = System.Text.RegularExpressions.Regex.Matches(
            text,
            "<Border\\s+x:Name=\"Section[A-Za-z]+\"").Count;
        Assert.Equal(15, sectionHostCount);

        Assert.DoesNotContain("settingsViews:ConfigurationManagerView", text, StringComparison.Ordinal);
        Assert.DoesNotContain("settingsViews:AboutSettingsView", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns:settingsViews=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"SectionHost\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"StackPanel.settings-content-panel\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Spacing\" Value=\"{DynamicResource MAA.App.Settings.SectionSpacing}\" />", styles, StringComparison.Ordinal);

        Assert.Contains("private readonly HashSet<string> _materializedSections", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private readonly Dictionary<string, double> _sectionTopCache", codeBehind, StringComparison.Ordinal);
        Assert.Contains("EnsureSectionMaterialized(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateSectionContent(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("anchor.Child = content;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryMaterializeNextSectionForScroll();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("EnsureSectionsThrough(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BindViewModelNotifications();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RefreshLocalizedSections()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MaterializeSectionWhenReadyAsync(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_pendingProgressiveSections", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("OnProgressiveMaterializationTick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GetSectionActivationLineY()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ResolveActiveSectionIndex(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StickyTitlePresentationState", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateStickyTitlePresentation()", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyStickyTitlePresentation(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshStickyTitlePresenter", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_stickyCurrentTitleTransform", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollViewer.Viewport.Height * 0.25d", codeBehind, StringComparison.Ordinal);
        Assert.Contains("nameof(SettingsPageViewModel.RootTexts)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new settingsViews.ConfigurationManagerView()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new settingsViews.AboutSettingsView()", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueReportView_ShouldExposeOpenRuntimeLogWindowAction()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "IssueReportView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "IssueReportView.axaml.cs"));

        Assert.Contains("Click=\"OnOpenRuntimeLogWindowClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanOpenRuntimeLogWindow}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Settings.IssueReport.DeveloperMode", xaml, StringComparison.Ordinal);
        Assert.Contains("Settings.IssueReport.DeveloperModeNote", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanUseDeveloperMode}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnOpenRuntimeLogWindowClick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OpenRuntimeLogWindow()", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationManagerView_ShouldAutoSwitchProfiles_AndRemoveObsoleteButtons()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml.cs"));

        Assert.Contains("SelectionChanged=\"OnConfigurationProfileSelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnImportProfilesClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"configuration-manager-action-grid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RowDefinitions=\"Auto,10,Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.RowSpan=\"3\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Watermark=\"{Binding RootTexts[Settings.ConfigurationManager.NewProfileWatermark]}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding RootTexts[Settings.ConfigurationManager.SaveAsNew]}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding RootTexts[Settings.ConfigurationManager.ExportCurrent]}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding RootTexts[Settings.ConfigurationManager.ExportAll]}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RootTexts[Settings.ConfigurationManager.AutoSavedCurrent]", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"app-button app-primary\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("留空使用当前时间", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("切换到别的配置", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("保存当前配置的修改", xaml, StringComparison.Ordinal);

        // Confirm dialog strings should come from RootTexts (not hard-coded literals) and include profile name formatting.
        Assert.Contains("Settings.ConfigurationManager.Dialog.DeleteTitle", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Settings.ConfigurationManager.Dialog.DeleteMessage", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Settings.Action.Delete", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Settings.Action.Cancel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("string.Format", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueView_ContextMenus_ShouldUseCustomPopupMenuWithoutDefaultMenuChrome()
    {
        var root = GetMaaUnifiedRoot();
        var taskQueue = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml.cs"));
        var interactionStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppInteractionStyles.axaml"));
        var popupMenu = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppPopupMenu.cs"));
        var popupPresenter = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppPopupMenuPresenter.axaml"));

        Assert.DoesNotContain("<ContextMenu", taskQueue, StringComparison.Ordinal);
        Assert.DoesNotContain("MenuFlyout", taskQueue, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMenu", taskQueue, StringComparison.Ordinal);
        Assert.DoesNotContain("app-context-menu", interactionStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextMenu.app-context-menu", interactionStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("PART_ContextMenuChrome", interactionStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("PART_MenuItemChrome", interactionStyles, StringComparison.Ordinal);
        Assert.Contains("<controls:AppPopupMenu x:Name=\"TaskQueueActionPopup\"", taskQueue, StringComparison.Ordinal);
        Assert.Contains("ItemInvoked=\"OnTaskQueueActionPopupItemInvoked\"", taskQueue, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskQueueActionPopupItems", taskQueue, StringComparison.Ordinal);
        Assert.DoesNotContain("task-queue-action-popup-item-shell", taskQueue, StringComparison.Ordinal);
        Assert.DoesNotContain("OnTaskQueueActionPopupItemPointerPressed", taskQueue, StringComparison.Ordinal);
        Assert.Contains("OpenTaskQueueActionPopup", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildTaskMenuItems", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CloseTaskQueueActionPopup()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TaskQueuePopupMenuItem", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AppMenuActionItem", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsLightDismissEnabled = true", popupMenu, StringComparison.Ordinal);
        Assert.Contains("WindowManagerAddShadowHint = false", popupMenu, StringComparison.Ordinal);
        Assert.Contains("PopupUiScale.SetUseTopLevelUiScale(this, true)", popupMenu, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-popup-menu-item-shell\"", popupPresenter, StringComparison.Ordinal);
        Assert.Contains("MAA.App.Interaction.FloatingMenuPadding", interactionStyles, StringComparison.Ordinal);
        Assert.Contains("MAA.App.Interaction.FloatingMenuItemMinHeight", interactionStyles, StringComparison.Ordinal);
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }

}
