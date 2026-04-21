using System.Text.RegularExpressions;

namespace MAAUnified.Tests;

public sealed class StyleTokenContractTests
{
    private static readonly string[] CoreEntryViews =
    [
        "App/Views/MainWindow.axaml",
        "App/Features/Root/TaskQueueView.axaml",
        "App/Features/Root/SettingsView.axaml",
        "App/Features/Advanced/CopilotView.axaml",
        "App/Features/Advanced/ToolboxView.axaml",
    ];
    private static readonly string[] TokenizedCoreEntryViews =
    [
        "App/Features/Root/SettingsView.axaml",
    ];

    [Fact]
    public void GlobalStyleEntry_ShouldBeSingleSource()
    {
        var root = GetMaaUnifiedRoot();
        var appAxaml = Path.Combine(root, "App", "App.axaml");
        var appText = File.ReadAllText(appAxaml);

        Assert.Contains("StyleInclude Source=\"avares://MAAUnified/Styles/ColorTokens.axaml\"", appText, StringComparison.Ordinal);
        Assert.Contains("StyleInclude Source=\"avares://MAAUnified/Styles/ControlStyles.axaml\"", appText, StringComparison.Ordinal);

        var allAxamlFiles = Directory.EnumerateFiles(Path.Combine(root, "App"), "*.axaml", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(Path.Combine("App", "App.axaml"), StringComparison.Ordinal))
            .ToList();

        foreach (var file in allAxamlFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("StyleInclude Source=\"avares://MAAUnified/Styles/", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ColorTokens_ShouldContainRequiredSemanticKeys_ForLightAndDark()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Styles", "ColorTokens.axaml"));

        Assert.Contains("<ResourceDictionary x:Key=\"Light\">", text, StringComparison.Ordinal);
        Assert.Contains("<ResourceDictionary x:Key=\"Dark\">", text, StringComparison.Ordinal);

        var requiredKeys =
            new[]
            {
                "MAA.Color.Surface.Window",
                "MAA.Color.Surface.Section",
                "MAA.Color.Surface.SectionStrong",
                "MAA.Color.Border.Default",
                "MAA.Color.Text.Primary",
                "MAA.Color.State.Warning",
                "MAA.Color.State.Error",
                "MAA.Color.State.Success",
                "MAA.Color.State.Running",
                "MAA.Color.State.Skipped",
                "MAA.Color.State.Idle",
                "MAA.Color.Action.Background",
                "MAA.Color.Action.BackgroundHover",
                "MAA.Color.Action.BackgroundPressed",
                "MAA.Color.Action.Border",
                "MAA.Color.Action.Foreground",
            };

        foreach (var key in requiredKeys)
        {
            Assert.Contains($"x:Key=\"{key}\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ControlStyles_ShouldContainSectionTitleActionContracts()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));

        Assert.Contains("Style Selector=\"Border.wpf-panel\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.wpf-button\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.wpf-card.status-running\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.wpf-card.status-success\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.wpf-card.status-error\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.wpf-card.status-skipped\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.wpf-card.status-idle\"", text, StringComparison.Ordinal);

        Assert.Contains("Style Selector=\"Border.section\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TextBlock.section-title\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.action\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.item-card.status-running\"", text, StringComparison.Ordinal);

        Assert.Contains("{DynamicResource MAA.Brush.Wpf.Region25}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.FontSize.SectionTitle}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.FontSize.CopilotNavTab}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.Size.Action.Height}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.Brush.Wpf.TaskStatus.Running}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.Brush.Wpf.TaskStatus.Success}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.Brush.Wpf.TaskStatus.Error}", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TabControl.copilot-nav TabItem\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppFoundationStyles_ShouldDefineAppInputBlockContract()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));

        Assert.Contains("Style Selector=\"TextBox.app-input.app-input-block\"", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"NaN\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"{DynamicResource MAA.App.Size.InputBlockMinHeight}\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{DynamicResource MAA.App.Thickness.InputBlockPadding}\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"VerticalContentAlignment\" Value=\"Top\" />", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSources_ShouldNotRetainModernDialogShellOrLegacyModernDialogTokens()
    {
        var root = GetMaaUnifiedRoot();
        var appRoot = Path.Combine(root, "App");
        var legacyHits = Directory.EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(path => new
            {
                Path = path,
                Text = File.ReadAllText(path),
            })
            .Where(entry =>
                entry.Text.Contains("ModernDialogShell", StringComparison.Ordinal)
                || entry.Text.Contains("modern-dialog-", StringComparison.Ordinal))
            .ToList();

        Assert.False(File.Exists(Path.Combine(root, "App", "Features", "Dialogs", "ModernDialogShell.cs")));
        Assert.Empty(legacyHits);
    }

    [Fact]
    public void ControlStyles_ShouldKeepLowResolutionFriendlyMainWindowSizing()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));

        Assert.Contains("<x:Double x:Key=\"MAA.Size.MainWindow.Width\">1380</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.MainWindow.Height\">900</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.MainWindow.MinWidth\">1080</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.MainWindow.MinHeight\">620</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.MainWindow.LayoutWidth\">1360</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.MainWindow.ContentMaxWidth\">1360</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Action.Height\">30</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Action.RunPrimaryHeight\">50</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.TaskQueue.RowHeight\">30</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Tab.MinHeight\">30</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.TaskQueue.ListPanelWidth\">276</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.TaskQueue.LogPanelWidth\">380</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Settings.SectionListWidth\">224</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Copilot.SidePanelWidth\">420</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.queue-run\"", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"{DynamicResource MAA.Size.Action.RunPrimaryHeight}\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"{DynamicResource MAA.Size.Tab.MinHeight}\" />", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ShouldUseResponsiveWidthAndCompactHeightLayoutControls()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("CompactLayoutHeightThreshold = 720d", text, StringComparison.Ordinal);
        Assert.Contains("ResponsiveMarginStageEndWidth = 1160d", text, StringComparison.Ordinal);
        Assert.Contains("ResponsiveMaxLayoutWidth = 1360d", text, StringComparison.Ordinal);
        Assert.Contains("ResponsiveContentStageEndWidth = ResponsiveMarginStageEndWidth + (ResponsiveMaxLayoutWidth - ResponsiveMinLayoutWidth)", text, StringComparison.Ordinal);
        Assert.Contains("SizeChanged += OnWindowSizeChanged;", text, StringComparison.Ordinal);
        Assert.Contains("ApplyResponsiveLayoutMetrics(width);", text, StringComparison.Ordinal);
        Assert.Contains("\"MAA.Size.MainWindow.LayoutWidth\"", text, StringComparison.Ordinal);
        Assert.Contains("\"MAA.Size.TaskQueue.ListPanelWidth\"", text, StringComparison.Ordinal);
        Assert.Contains("\"MAA.Size.Settings.SectionListWidth\"", text, StringComparison.Ordinal);
        Assert.Contains("\"MAA.Size.Copilot.SidePanelWidth\"", text, StringComparison.Ordinal);
        Assert.Contains("height <= CompactLayoutHeightThreshold", text, StringComparison.Ordinal);
        Assert.Contains("\"MAA.Thickness.PageMargin\"", text, StringComparison.Ordinal);
        Assert.Contains("\"MAA.Size.Action.Height\"", text, StringComparison.Ordinal);
        Assert.Contains("\"MAA.Size.Tab.MinHeight\"", text, StringComparison.Ordinal);
        Assert.Contains("UpdateAdaptiveLayoutMode();", text, StringComparison.Ordinal);
        Assert.Contains("Resources[\"MAA.Thickness.PageMargin\"]", text, StringComparison.Ordinal);
        Assert.Contains("Resources[key] = value;", text, StringComparison.Ordinal);
        Assert.Contains("Resources.Remove(key);", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ShouldCenterShellContentWithinMaximumWidth()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml"));

        Assert.Contains("x:Name=\"ShellRoot\"", text, StringComparison.Ordinal);
        Assert.Contains("Width=\"{DynamicResource MAA.Size.MainWindow.LayoutWidth}\"", text, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{DynamicResource MAA.Size.MainWindow.ContentMaxWidth}\"", text, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RootViews_ShouldUseResponsiveMainColumnWidths()
    {
        var root = GetMaaUnifiedRoot();
        var taskQueue = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));
        var settings = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var copilot = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));

        Assert.Contains("Width=\"{DynamicResource MAA.Size.TaskQueue.ListPanelWidth}\"", taskQueue, StringComparison.Ordinal);
        Assert.Contains("Width=\"{DynamicResource MAA.Size.TaskQueue.LogPanelWidth}\"", taskQueue, StringComparison.Ordinal);
        Assert.Contains("Width=\"{DynamicResource MAA.Size.Settings.SectionListWidth}\"", settings, StringComparison.Ordinal);
        Assert.Contains("Width=\"{DynamicResource MAA.Size.Copilot.SidePanelWidth}\"", copilot, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueView_ShouldUseStatusClassBindingInsteadOfStatusBrush()
    {
        var root = GetMaaUnifiedRoot();
        var taskQueuePath = Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml");
        var text = File.ReadAllText(taskQueuePath);

        Assert.DoesNotContain("StatusBrush", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-running=\"{Binding IsStatusRunning}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-success=\"{Binding IsStatusSuccess}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-error=\"{Binding IsStatusError}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-skipped=\"{Binding IsStatusSkipped}\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes.status-idle=\"{Binding IsStatusIdle}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueView_ShouldBindCanEditAndPrimaryRunToggle()
    {
        var root = GetMaaUnifiedRoot();
        var taskQueuePath = Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml");
        var text = File.ReadAllText(taskQueuePath);

        Assert.Contains("ListBox Grid.Row=\"1\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"wpf-list-no-highlight\"", text, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanEdit}\"", text, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanToggleRun}\"", text, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding BatchActionText}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusDisplayName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TabControl Grid.Row=\"2\"", text, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer Grid.Row=\"1\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TooltipHintControl_ShouldUseFrameworkTooltipServiceWithHalfSecondDelay()
    {
        var root = GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "TooltipHint.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "App", "Controls", "TooltipHint.axaml.cs"));

        Assert.Contains("ToolTip.ShowDelay=\"500\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ToolTip.Tip>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Popup", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreEntryViews_ShouldNotContainHardcodedColorOrSizeLiterals()
    {
        var root = GetMaaUnifiedRoot();
        var colorPattern = new Regex("#[0-9A-Fa-f]{6,8}", RegexOptions.Compiled);
        var sizePattern = new Regex(
            "(Width|Height|MinWidth|MinHeight|MaxWidth|MaxHeight|Margin|Padding|Spacing|CornerRadius|FontSize|BorderThickness|ColumnDefinitions|RowDefinitions)=\"[^\"]*[0-9][^\"]*\"",
            RegexOptions.Compiled);

        foreach (var relative in TokenizedCoreEntryViews)
        {
            var fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(fullPath);
            Assert.DoesNotMatch(colorPattern, text);
            Assert.DoesNotMatch(sizePattern, text);
        }
    }

    [Fact]
    public void CoreEntryViews_AllButtons_ShouldUseFoundationButtonClass()
    {
        var root = GetMaaUnifiedRoot();
        var buttonPattern = new Regex("<Button\\b(?!\\.ContextMenu)([^>]*)>", RegexOptions.Compiled | RegexOptions.Singleline);
        var totalButtons = 0;

        foreach (var relative in CoreEntryViews)
        {
            var fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(fullPath);
            var matches = buttonPattern.Matches(text);

            foreach (Match match in matches)
            {
                totalButtons++;
                var attrs = match.Groups[1].Value;
                Assert.Matches(
                    "Classes=\"[^\"]*\\b(?:wpf-button|app-button|achievement-toast-close|copilot-link)\\b[^\"]*\"",
                    attrs);
            }
        }

        Assert.True(totalButtons > 0);
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }
}
