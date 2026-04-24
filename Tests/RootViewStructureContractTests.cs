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
        Assert.Contains("IsVisible=\"{Binding IsSettingsRootTabSelected}\"", text, StringComparison.Ordinal);
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
        Assert.Contains("TaskSettingsHost.Content = VM?.SelectedTaskSettingsViewModel;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanToggleRun}\"", text, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CoreInitializationMessage}\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasCoreInitializationMessage}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-list\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ListBox.task-queue-list ListBoxItem:pointerover\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ListBox.task-queue-list ListBoxItem:selected\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ListBox.task-queue-list ListBoxItem:selected:pointerover Border.task-queue-item-card\"", text, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LogsTitleText}\"", text, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LogCards}\"", text, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Items}\"", text, StringComparison.Ordinal);
        Assert.Contains("ToolTip.ShowDelay=\"200\"", text, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"NoWrap\"", text, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"960\"", text, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"540\"", text, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowTimeOnlyLayout}\"", text, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnOpenPostActionClick\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAddTaskModuleClick\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnBatchActionClick\"", text, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnToggleBatchModeClick\"", text, StringComparison.Ordinal);
        Assert.Contains("<DataTemplate DataType=\"taskVm:StartUpTaskModuleViewModel\">", text, StringComparison.Ordinal);
        Assert.Contains("<DataTemplate DataType=\"taskVm:FightTaskModuleViewModel\">", text, StringComparison.Ordinal);
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
            Assert.DoesNotContain("<WrapPanel", text, StringComparison.Ordinal);

            if (file.EndsWith("RecruitSettingsView.axaml", StringComparison.Ordinal))
            {
                // Recruit has one intentional horizontal row (checkbox + inline hint text).
                var horizontalCount = System.Text.RegularExpressions.Regex.Matches(text, "Orientation=\"Horizontal\"").Count;
                Assert.Equal(1, horizontalCount);
                Assert.Contains("Content=\"{Binding Texts[Recruit.AutoSelectLevel6]}\"", text, StringComparison.Ordinal);
                Assert.Contains("Text=\"{Binding Texts[Recruit.AutoSelectLevel6FixedTime]}\"", text, StringComparison.Ordinal);
            }
            else
            {
                // For task settings: keep a predictable vertical form layout (avoid horizontal stack layouts drifting in).
                Assert.DoesNotContain("Orientation=\"Horizontal\"", text, StringComparison.Ordinal);
            }
        }

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
    public void SettingsView_ShouldUseLazySectionHosts_AndKeepSharedScrollSurface()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml.cs"));

        Assert.Contains("ItemsSource=\"{Binding Sections}\"", text, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedSection}\"", text, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"OnSectionSelectionChanged\"", text, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList", text, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"Rail\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SectionScrollViewer\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SectionContentPanel\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StickyTitlePanel\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StickyCurrentHost\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StickyTransitionHost\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StickyTitleText\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StickyTransitionText\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-sticky-title\"", text, StringComparison.Ordinal);
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
        Assert.Contains("StickyTitlePresentationState", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UpdateStickyTitlePresentation()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplyStickyTitlePresentation(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_stickyCurrentTitleTransform", codeBehind, StringComparison.Ordinal);
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
        Assert.Contains("Watermark=\"{Binding RootTexts[Settings.ConfigurationManager.NewProfileWatermark]}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding RootTexts[Settings.ConfigurationManager.SaveAsNew]}\"", xaml, StringComparison.Ordinal);
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

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }
}
