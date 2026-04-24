namespace MAAUnified.Tests;

public sealed class NavigationSharedStructureContractTests
{
    [Fact]
    public void MainWindow_ShouldKeepRootEntryBindings_AndUseSharedNavigationClasses()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml"));
        var controlStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));

        Assert.Contains("<DataTemplate DataType=\"viewModels:RootPageHostViewModel\">", mainWindow, StringComparison.Ordinal);
        Assert.Contains("<TabStrip Classes=\"root-nav\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding TaskQueueRootPage.PageContent}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding CopilotRootPage.PageContent}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding ToolboxRootPage.PageContent}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SettingsRootPage.PageContent}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskQueueRootHost\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CopilotRootHost\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ToolboxRootHost\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsRootHost\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<advancedViews:CopilotView DataContext=\"{Binding CopilotPage}\" />", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<advancedViews:ToolboxView DataContext=\"{Binding ToolboxPage}\" />", mainWindow, StringComparison.Ordinal);

        Assert.Contains("Style Selector=\"TabStrip.root-nav\"", controlStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TabStrip.root-nav TabStripItem\"", controlStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TabStrip.copilot-nav\"", controlStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TabStrip.copilot-nav TabStripItem\"", controlStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TabControl.toolbox-nav TabItem\"", controlStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TabControl.toolbox-nav TabItem:selected\"", controlStyles, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionIndicatorPresenter Classes=\"selection-indicator-nav\"", controlStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotAndToolboxViews_ShouldBindTabHeadersThroughSharedNavigationClasses()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var copilot = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));
        var toolbox = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "ToolboxView.axaml"));

        Assert.Contains("<TabStrip Classes=\"copilot-nav\"", copilot, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding MainTabTitle}\"", copilot, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding SecurityTabTitle}\"", copilot, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding ParadoxTabTitle}\"", copilot, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding OtherTabTitle}\"", copilot, StringComparison.Ordinal);

        Assert.Contains("<TabControl Classes=\"toolbox-nav\"", toolbox, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding RecruitTabTitle}\"", toolbox, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding OperBoxTabTitle}\"", toolbox, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding DepotTabTitle}\"", toolbox, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding GachaTabTitle}\"", toolbox, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding PeepTabTitle}\"", toolbox, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding MiniGameTabTitle}\"", toolbox, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndAnnouncement_ShouldUseSharedRailSelectionStructure()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var settings = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var announcement = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml"));
        var selectionStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppSelectionListStyles.axaml"));

        Assert.Contains("<controls:AppSelectionList", settings, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"Rail\"", settings, StringComparison.Ordinal);
        Assert.Contains("<controls:AppStickyTitlePresenter x:Name=\"StickyTitlePresenter\"", settings, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList", announcement, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"Rail\"", announcement, StringComparison.Ordinal);
        Assert.Contains("ReserveTrailingAccessorySpace=\"True\"", announcement, StringComparison.Ordinal);
        Assert.Contains("<controls:AppStickyTitlePresenter x:Name=\"StickyTitlePresenter\"", announcement, StringComparison.Ordinal);

        Assert.Contains("controls|AppSelectionList.selection-list-rail Border.app-selection-list-item-shell", selectionStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionList.selection-list-rail.selection-list-rail-trailing-accessory-space Border.app-selection-list-item-shell", selectionStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionList.selection-list-surface Border.app-selection-list-item-shell", selectionStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionList.selection-list-none Border.app-selection-list-item-shell", selectionStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionIndicatorPresenter.selection-indicator-rail.indicator-vertical /template/ Border#PART_Indicator", selectionStyles, StringComparison.Ordinal);
    }
}
