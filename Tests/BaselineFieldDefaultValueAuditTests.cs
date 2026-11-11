namespace MAAUnified.Tests;

/// <summary>
/// M01 verifier/fixer round 10: closes the systematic blind spot behind M01-R9-01/02/03/04/05/08.
/// Every non-N/A item-level fields[].default_value claim is pinned here against audited frozen WPF
/// evidence (frozen commit 23ddd271b40a3d0c18de8a6ae8305357cc032e19) or explicitly recorded
/// Avalonia runtime evidence. Any change to a default_value/source claim must update this pin list
/// together with its frozen file:line justification.
/// M01 verifier/fixer round 12 (R11-01): the plain-WPF-source allow-list now requires a frozen
/// file:line citation per entry and is closed-world in both directions, so a misattributed
/// "WPF baseline" source can no longer be blessed without explicit frozen evidence.
/// </summary>
public sealed class BaselineFieldDefaultValueAuditTests
{
    // Frozen WPF evidence anchors used for the audit (frozen commit
    // 23ddd271b40a3d0c18de8a6ae8305357cc032e19; verified read-only from the superproject object
    // store — the frozen tree is not resolvable from this worktree, so citations are asserted for
    // shape, not dereferenced at test time):
    // - BackgroundSettingsUserControlModel.cs: opacity default "50", blur default "5".
    // - RemoteControlUserControlModel.cs: poll interval default "1000".
    // - GuiSettingsUserControlModel.cs: UseTray default true, MinimizeToTray default false;
    //   LocalizationHelper.cs: first-run language derived from OS culture with en-us fallback.
    // - ConnectSettingsUserControlModel.cs: connect address default string.Empty.
    // - NotifyIcon.xaml + Res/Localizations/zh-cn.xaml: startMenu header {DynamicResource Farming} =
    //   一键长草, stopMenu header {DynamicResource Stop} = 停止.
    // - GameSettingsUserControlModel.cs: ClientType default "Official".
    // - ConfigConverter.cs: legacy configuration is converted automatically at startup; the frozen
    //   tree contains no manual import UI.
    // - NotifyIcon.xaml.cs:76-90: switchLangMenu is built as one explicit menu item per supported
    //   language (click sets that language directly); the frozen WPF tray does NOT cycle languages.
    // - Bootstrapper.cs:750-778: OnUnhandledException -> ShowErrorDialog -> ErrorDialogView is the
    //   frozen WPF global error channel; the frozen tree has no page concept and no
    //   MainShellViewModel/PageViewModelBase/LastError symbols.
    private static readonly IReadOnlyDictionary<string, string> AuditedDefaultValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TaskQueueRoot"] = "SelectedTaskModule=StartUp",
            ["SettingsRoot"] = "SelectedSection=ConfigurationManager",
            ["Settings.Background"] = "BackgroundOpacity=50; BackgroundBlur=5",
            ["Settings.Gui"] = "Language=zh-cn (Avalonia fixed default; frozen WPF derives from OS culture with en-us fallback); UseTray=true; MinimizeToTray=false",
            ["Settings.RemoteControl"] = "RemotePollInterval=1000",
            ["Advanced.StageManager"] = "ClientType=Official",
            ["Advanced.WebApi"] = "Enabled=false; Host=127.0.0.1; Port=51888",
            ["System.Connect"] = "ConnectionAddress= (frozen WPF default is empty; Avalonia pre-fills 127.0.0.1:5555)",
            ["System.ImportLegacyConfig"] = "SelectedImportSource=auto",
            ["System.TrayMenu.Start"] = "MenuItem=开始 (Avalonia); frozen WPF label=一键长草 (Farming)",
            ["System.TrayMenu.Stop"] = "MenuItem=停止",
            ["System.TrayMenu.ForceShow"] = "Show+Activate",
            ["System.TrayMenu.HideTray"] = "Show main window; disable tray integration",
            ["System.TrayMenu.ToggleOverlay"] = "Toggle overlay visibility",
            ["System.TrayMenu.Restart"] = "Confirm exit; restart without user arguments",
            ["System.TrayMenu.Exit"] = "Close window",
            ["System.TrayMenu.SwitchLanguage"] = "Frozen WPF=explicit per-language selection submenu (src/MaaWpfGui/Views/UI/NotifyIcon.xaml.cs:76-90); Avalonia reserved command=cycle display language to the next supported locale (App/ViewModels/MainShellViewModel.cs:1700-1708)",
            ["System.GlobalErrorChannel"] = "MainShellViewModel.LastError (Avalonia); frozen WPF analog=Bootstrapper.ShowErrorDialog -> ErrorDialogView (src/MaaWpfGui/Main/Bootstrapper.cs:766-778)",
            ["System.PageErrorChannel"] = "PageViewModelBase.LastErrorMessage (Avalonia); frozen WPF has no page-level error state",
        };

    // Items whose field source may remain a plain "WPF baseline freeze"/"WPF system baseline"
    // claim. Every entry MUST carry a frozen file:line citation (frozen commit
    // 23ddd271b40a3d0c18de8a6ae8305357cc032e19) that independently supports the pinned value;
    // entries whose value is Avalonia-sourced or divergent must be removed here and use an
    // explicit mixed/Avalonia source string instead (R9-03/04/05, R11-01).
    private static readonly IReadOnlyDictionary<string, string> AllowedPlainWpfSourceFrozenEvidence =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TaskQueueRoot"] = "src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs:977-986 (first-run task list seeds StartUpTask first)",
            ["SettingsRoot"] = "src/MaaWpfGui/Views/UI/SettingsView.xaml:64-67 (first settings section is ConfigurationMgrUserControl)",
            ["Settings.Background"] = "src/MaaWpfGui/ViewModels/UserControl/Settings/BackgroundSettingsUserControlModel.cs:120,130 (opacity default \"50\", blur default \"5\")",
            ["Settings.RemoteControl"] = "src/MaaWpfGui/ViewModels/UserControl/Settings/RemoteControlUserControlModel.cs:87 (poll interval default \"1000\")",
            ["Advanced.StageManager"] = "src/MaaWpfGui/ViewModels/UserControl/Settings/GameSettingsUserControlModel.cs:69 (ClientType default \"Official\")",
            ["System.TrayMenu.Stop"] = "src/MaaWpfGui/Views/UI/NotifyIcon.xaml.cs:134-138 (StopTask -> TaskQueueViewModel.ManualStop); src/MaaWpfGui/Res/Localizations/zh-cn.xaml:724 (Stop=停止)",
            ["System.TrayMenu.ForceShow"] = "src/MaaWpfGui/Views/UI/NotifyIcon.xaml.cs:140-143 (ForceShow -> MainWindowManager.ForceShow)",
            ["System.TrayMenu.HideTray"] = "src/MaaWpfGui/Views/UI/NotifyIcon.xaml.cs:146-152 (MainWindowManager.Show + toggle UseTray)",
            ["System.TrayMenu.ToggleOverlay"] = "src/MaaWpfGui/Views/UI/NotifyIcon.xaml.cs:154-157 (ToggleOverlay -> TaskQueueViewModel.ToggleOverlay)",
            ["System.TrayMenu.Restart"] = "src/MaaWpfGui/Views/UI/NotifyIcon.xaml.cs:159-165 (ConfirmExit -> Bootstrapper.ShutdownAndRestartWithoutArgs)",
            ["System.TrayMenu.Exit"] = "src/MaaWpfGui/Views/UI/NotifyIcon.xaml.cs:167-173 (ConfirmExit -> Bootstrapper.Shutdown)",
        };

    [Fact]
    public void NonNaFieldDefaults_ShouldMatchTheAuditedPinListExactly()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var actual = baseline.Items
            .SelectMany(item => item.Fields.Select(field => (item.ItemId, field)))
            .Where(entry => !string.Equals(entry.field.DefaultValue, "N/A", StringComparison.Ordinal))
            .ToDictionary(entry => entry.ItemId, entry => entry.field.DefaultValue, StringComparer.Ordinal);

        Assert.Equal(
            AuditedDefaultValues.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void FieldSources_ShouldNotAttributeAvaloniaValuesToTheWpfBaselineWithoutFrozenEvidence()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        // Sources that silently attribute a value to the frozen WPF baseline were wrong or
        // misattributed in 7 of the audited cases (R9-01/02/04/05, R11-01). A plain
        // "WPF baseline freeze"/"WPF system baseline" attribution is only allowed for the
        // explicitly pinned entries in AllowedPlainWpfSourceFrozenEvidence; anything divergent
        // must use an explicit mixed/Avalonia source string instead.
        var plainSourcedItemIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in baseline.Items)
        {
            foreach (var field in item.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.Source), $"{item.ItemId} field source is empty");
                var plainWpfSource = field.Source is "WPF baseline freeze" or "WPF system baseline";
                if (plainWpfSource
                    && !string.Equals(field.DefaultValue, "N/A", StringComparison.Ordinal))
                {
                    Assert.True(
                        AllowedPlainWpfSourceFrozenEvidence.ContainsKey(item.ItemId),
                        $"{item.ItemId} attributes default \"{field.DefaultValue}\" to the frozen WPF baseline without an audited pin carrying frozen file:line evidence");
                    plainSourcedItemIds.Add(item.ItemId);
                }
            }
        }

        // Closed world in both directions: a stale allow-list entry would keep blessing a row
        // whose source was corrected, hiding future regressions behind an outdated exception.
        Assert.Equal(
            AllowedPlainWpfSourceFrozenEvidence.Keys.OrderBy(id => id, StringComparer.Ordinal),
            plainSourcedItemIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void PlainWpfSourceAllowListEntries_ShouldCarryFrozenFileLineEvidence()
    {
        // R11-01 hardening: a plain-WPF-source claim must not be blessable without a frozen
        // file:line reference. The frozen tree is not resolvable from this worktree (it lives in
        // the superproject object store), so the gate asserts the citation shape; each citation
        // above was verified read-only against frozen commit
        // 23ddd271b40a3d0c18de8a6ae8305357cc032e19 when the entry was added.
        foreach (var (itemId, citation) in AllowedPlainWpfSourceFrozenEvidence)
        {
            Assert.False(string.IsNullOrWhiteSpace(citation), $"{itemId} allow-list entry has no frozen evidence citation");
            Assert.Contains("src/MaaWpfGui/", citation, StringComparison.Ordinal);
            Assert.Matches(@"src/MaaWpfGui/\S+:\d+", citation);
        }
    }

    [Fact]
    public void ImportLegacyConfig_ShouldReferenceTheFrozenAutomaticConversion_NotAFictionalManualImport()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var item = Assert.Single(baseline.Items, i => i.ItemId == "System.ImportLegacyConfig");

        Assert.DoesNotContain("Manual Import", item.WpfReference, StringComparison.Ordinal);
        Assert.Contains("ConfigConverter", item.WpfReference, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemAndFallbackCaseIds_ShouldBeUniqueAndWellFormed()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        // M01-R9-06: BL-*/FAL-* ids are baseline-internal identifiers; they never resolve into the
        // acceptance template (which only contains ACC-* cases). Gate format and uniqueness so the
        // ids stay meaningful as log/scope locators.
        var itemIds = baseline.Items.Select(item => item.Evidence.CaseId).ToList();
        Assert.Equal(itemIds.Count, itemIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(itemIds, id => Assert.Matches("^BL-[0-9]{3}$", id));

        var fallbackIds = baseline.FallbackCapabilities.Select(row => row.Evidence.CaseId).ToList();
        Assert.Equal(fallbackIds.Count, fallbackIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(fallbackIds, id => Assert.Matches("^FAL-[A-Z]{3}-[A-Z]{3}$", id));
    }
}
