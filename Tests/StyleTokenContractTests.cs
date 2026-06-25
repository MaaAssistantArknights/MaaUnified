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
        Assert.Contains("Style Selector=\"TabStrip.copilot-nav TabStripItem\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalStyles_ShouldUseDynamicUiFontFamilyResource()
    {
        var root = GetMaaUnifiedRoot();
        var text = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));

        Assert.DoesNotContain("Segoe UI, Microsoft YaHei UI, sans-serif", text, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource MAA.FontFamily.UI}\"", text, StringComparison.Ordinal);

        var requiredRootSelectors =
            new[]
            {
                "Style Selector=\"Window\"",
                "Style Selector=\"TextBlock\"",
                "Style Selector=\"TemplatedControl\"",
                "Style Selector=\"ContentPresenter\"",
                "Style Selector=\"TextPresenter\"",
                "Style Selector=\"ToolTip TemplatedControl\"",
                "Style Selector=\"ToolTip ContentPresenter\"",
                "Style Selector=\"ToolTip TextPresenter\"",
                "Style Selector=\"ToolTip TextBlock\"",
                "Style Selector=\"Popup TemplatedControl\"",
                "Style Selector=\"Popup ContentPresenter\"",
                "Style Selector=\"Popup TextPresenter\"",
                "Style Selector=\"Popup TextBlock\"",
            };

        foreach (var selector in requiredRootSelectors)
        {
            var selectorIndex = text.IndexOf(selector, StringComparison.Ordinal);
            Assert.True(selectorIndex >= 0, $"Missing root font selector: {selector}");

            var nextStyleIndex = text.IndexOf("  <Style Selector=\"", selectorIndex + selector.Length, StringComparison.Ordinal);
            var styleBlock = nextStyleIndex < 0
                ? text[selectorIndex..]
                : text[selectorIndex..nextStyleIndex];
            Assert.True(
                styleBlock.Contains("<Setter Property=\"FontFamily\" Value=\"{DynamicResource MAA.FontFamily.UI}\" />", StringComparison.Ordinal)
                || styleBlock.Contains("<Setter Property=\"documents:TextElement.FontFamily\" Value=\"{DynamicResource MAA.FontFamily.UI}\" />", StringComparison.Ordinal),
                $"Missing UI font resource setter for selector: {selector}");
        }
    }

    [Fact]
    public void SettingsAnnouncementAndAchievement_ShouldNotDefineLocalFontFallback()
    {
        var root = GetMaaUnifiedRoot();
        var targetFiles = Directory.EnumerateFiles(Path.Combine(root, "App", "Features", "Settings"), "*.axaml", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "App", "Styles", "SettingsShellStyles.axaml"))
            .Append(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml"))
            .Append(Path.Combine(root, "App", "Features", "Dialogs", "AchievementListDialogView.axaml"))
            .ToArray();

        foreach (var file in targetFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("FontFamily", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft YaHei", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Noto Sans CJK", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Source Han Sans", text, StringComparison.Ordinal);
        }
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
        Assert.Contains("Style Selector=\"ComboBox.app-input /template/ ToggleButton#PART_DropDownButton\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ComboBox.app-input /template/ Border#DropDownOverlay\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-button /template/ ContentPresenter#PART_ContentPresenter\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-button:pointerover /template/ ContentPresenter#PART_ContentPresenter\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-button.app-subtle /template/ ContentPresenter#PART_ContentPresenter\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-button.app-subtle:pointerover /template/ ContentPresenter#PART_ContentPresenter\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-button.app-icon-only /template/ ContentPresenter#PART_ContentPresenter\"", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Right\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"IsHitTestVisible\" Value=\"True\" />", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.grouped-card-frame\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.grouped-card-item\"", text, StringComparison.Ordinal);
        Assert.Contains("<BoxShadows x:Key=\"MAA.App.BoxShadow.Card\">0 3 8 0 #0C000000, 0 2 4 -3 #12000000, 0 6 16 4 #08000000</BoxShadows>", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{DynamicResource MAA.Brush.App.Surface.Card}\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"0\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BoxShadow\" Value=\"{DynamicResource MAA.App.BoxShadow.Card}\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ClipToBounds\" Value=\"False\" />", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppStyles_ShouldNotClearBoxShadowWithNull()
    {
        var root = GetMaaUnifiedRoot();
        var styleFiles = Directory.EnumerateFiles(Path.Combine(root, "App"), "*.axaml", SearchOption.AllDirectories);

        foreach (var file in styleFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Property=\"BoxShadow\" Value=\"{x:Null}\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppInputStyles_ShouldBeIncludedAndDefineSettingsInputContracts()
    {
        var root = GetMaaUnifiedRoot();
        var appText = File.ReadAllText(Path.Combine(root, "App", "App.axaml"));
        var text = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppInputStyles.axaml"));

        Assert.Contains("StyleInclude Source=\"avares://MAAUnified/Styles/AppInputStyles.axaml\"", appText, StringComparison.Ordinal);

        Assert.Contains("Style Selector=\"TextBox.settings-input", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ComboBox.settings-input, ComboBox.settings-select", text, StringComparison.Ordinal);
        Assert.Contains("ComboBox.app-input /template/ ToggleButton#PART_DropDownButton", text, StringComparison.Ordinal);
        Assert.Contains("ComboBox.app-input /template/ Border#DropDownOverlay", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.settings-input-group\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.settings-input-group TextBox.settings-input-group-editor\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.settings-input-group-action\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ComboBoxItem Border.settings-dropdown-item-shell", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ComboBoxItem TextBlock.settings-dropdown-item-text", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.settings-dropdown-delete-button\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.app-suggest-input-shell\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TextBox.app-suggest-input-editor\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-suggest-input-toggle\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.app-suggest-input-popup-panel\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ItemsControl.app-suggest-input-list ContentPresenter\"", text, StringComparison.Ordinal);

        Assert.Contains("x:Key=\"MAA.App.Thickness.SettingsInputGroupPadding\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"MAA.App.Thickness.SettingsInputGroupEditorPadding\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"MAA.App.Size.SettingsInputActionButtonMinWidth\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"MAA.App.CornerRadius.SettingsInputGroupEditor\"", text, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"MAA.Brush.App.SettingsInput.GroupActionBackgroundHover\"", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.App.Size.InputHeight}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.Brush.App.SettingsInput.BorderFocus}", text, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.Brush.App.SettingsInput.ItemBackgroundSelected}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppInputControls_ShouldExposeUnifiedSettingsInputFoundation()
    {
        var root = GetMaaUnifiedRoot();
        var controlsRoot = Path.Combine(root, "App", "Controls");

        var textInput = File.ReadAllText(Path.Combine(controlsRoot, "AppTextInput.axaml.cs"));
        var select = File.ReadAllText(Path.Combine(controlsRoot, "AppSelect.axaml.cs"));
        var history = File.ReadAllText(Path.Combine(controlsRoot, "AppHistoryInput.axaml"));
        var suggest = File.ReadAllText(Path.Combine(controlsRoot, "AppSuggestInput.axaml"));
        var suggestCode = File.ReadAllText(Path.Combine(controlsRoot, "AppSuggestInput.axaml.cs"));
        var inputStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppInputStyles.axaml"));
        var action = File.ReadAllText(Path.Combine(controlsRoot, "AppActionInput.axaml"));
        var actionCode = File.ReadAllText(Path.Combine(controlsRoot, "AppActionInput.axaml.cs"));
        var number = File.ReadAllText(Path.Combine(controlsRoot, "VerticalSpinNumberBox.axaml"));
        var numberCode = File.ReadAllText(Path.Combine(controlsRoot, "VerticalSpinNumberBox.axaml.cs"));
        var appNumberCode = File.ReadAllText(Path.Combine(controlsRoot, "AppNumberInput.axaml.cs"));

        Assert.Contains("public class AppTextInput : TextBox", textInput, StringComparison.Ordinal);
        Assert.Contains("StyleKeyOverride => typeof(TextBox)", textInput, StringComparison.Ordinal);
        Assert.Contains("Classes.Set(\"settings-input\", true)", textInput, StringComparison.Ordinal);
        Assert.Contains("DismissFocusOnEnterProperty", textInput, StringComparison.Ordinal);
        Assert.Contains("Key.Enter", textInput, StringComparison.Ordinal);
        Assert.Contains("ClearFocus()", textInput, StringComparison.Ordinal);
        Assert.Contains("public class AppSelect : ComboBox", select, StringComparison.Ordinal);
        Assert.Contains("StyleKeyOverride => typeof(ComboBox)", select, StringComparison.Ordinal);
        Assert.Contains("Classes.Set(\"settings-select\", true)", select, StringComparison.Ordinal);

        Assert.Contains("Classes=\"app-history-input-toggle\"", history, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"{DynamicResource MAA.App.Size.InputHeight}\" />", history, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"{DynamicResource MAA.App.Size.InputHeight}\" />", history, StringComparison.Ordinal);
        Assert.Contains("Width=\"{Binding Bounds.Width, ElementName=ShellBorder}\"", history, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"{Binding Bounds.Width, ElementName=ShellBorder}\"", history, StringComparison.Ordinal);

        Assert.Contains("x:Class=\"MAAUnified.App.Controls.AppSuggestInput\"", suggest, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-suggest-input-shell\"", suggest, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-suggest-input-item-shell\"", suggest, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ItemsControl.app-suggest-input-list ContentPresenter:pointerover Border.app-suggest-input-item-shell\"", suggest, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ItemsControl.app-suggest-input-list ContentPresenter.keyboard-current Border.app-suggest-input-item-shell\"", suggest, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"ItemsControl.app-suggest-input-list ContentPresenter.keyboard-current:pointerover Border.app-suggest-input-item-shell\"", suggest, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Button.app-suggest-input-toggle\"", inputStyles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"{DynamicResource MAA.App.Size.InputHeight}\" />", inputStyles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"{DynamicResource MAA.App.Size.InputHeight}\" />", inputStyles, StringComparison.Ordinal);
        Assert.Contains("nameof(MinimumPrefixLength)", suggestCode, StringComparison.Ordinal);
        Assert.Contains("candidate.Contains(prefix, StringComparison.OrdinalIgnoreCase)", suggestCode, StringComparison.Ordinal);

        Assert.Contains("x:Class=\"MAAUnified.App.Controls.AppActionInput\"", action, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-input-group\"", action, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-input-group-editor\"", action, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-input-group-action\"", action, StringComparison.Ordinal);
        Assert.Contains("ActionClick?.Invoke(this, e)", actionCode, StringComparison.Ordinal);

        Assert.Contains("x:Class=\"MAAUnified.App.Controls.VerticalSpinNumberBox\"", number, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.Brush.App.SettingsInput.Border}", number, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource MAA.App.CornerRadius.SettingsInput}", number, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnEditorKeyDown\"", number, StringComparison.Ordinal);
        Assert.Contains("DismissFocusOnEnterProperty", numberCode, StringComparison.Ordinal);
        Assert.Contains("Key.Enter", numberCode, StringComparison.Ordinal);
        Assert.Contains("ClearFocus()", numberCode, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-spin", number, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-spin", numberCode, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-spin", appNumberCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AppPopupMenu_ShouldProvideUnifiedContextMenuFoundation()
    {
        var root = GetMaaUnifiedRoot();
        var controlsRoot = Path.Combine(root, "App", "Controls");
        var interactionStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppInteractionStyles.axaml"));
        var appCode = File.ReadAllText(Path.Combine(root, "App", "App.axaml.cs"));
        var menuEntry = File.ReadAllText(Path.Combine(controlsRoot, "AppMenuEntry.cs"));
        var popupMenu = File.ReadAllText(Path.Combine(controlsRoot, "AppPopupMenu.cs"));
        var popupMenuService = File.ReadAllText(Path.Combine(controlsRoot, "AppPopupMenuService.cs"));
        var presenter = File.ReadAllText(Path.Combine(controlsRoot, "AppPopupMenuPresenter.axaml"));
        var textEditingMenu = File.ReadAllText(Path.Combine(controlsRoot, "AppTextEditingMenu.cs"));
        var hotkeys = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "HotKeySettingsView.axaml"));

        Assert.Contains("abstract record AppMenuEntry", menuEntry, StringComparison.Ordinal);
        Assert.Contains("record AppMenuActionItem", menuEntry, StringComparison.Ordinal);
        Assert.Contains("record AppMenuSeparatorEntry", menuEntry, StringComparison.Ordinal);
        Assert.Contains("AppMenuItemInvokedEventArgs", menuEntry, StringComparison.Ordinal);
        Assert.Contains("public sealed class AppPopupMenu : Popup", popupMenu, StringComparison.Ordinal);
        Assert.Contains("IsLightDismissEnabled = true", popupMenu, StringComparison.Ordinal);
        Assert.Contains("ShouldUseOverlayLayer = true", popupMenu, StringComparison.Ordinal);
        Assert.Contains("WindowManagerAddShadowHint = false", popupMenu, StringComparison.Ordinal);
        Assert.Contains("PopupUiScale.SetUseTopLevelUiScale(this, true)", popupMenu, StringComparison.Ordinal);
        var popupInvokeIndex = popupMenu.IndexOf("ItemInvoked?.Invoke(this, e)", StringComparison.Ordinal);
        var popupCloseIndex = popupMenu.IndexOf("IsOpen = false", popupInvokeIndex, StringComparison.Ordinal);
        Assert.True(popupInvokeIndex >= 0, "AppPopupMenu should raise item invocation.");
        Assert.True(popupCloseIndex > popupInvokeIndex, "AppPopupMenu should close after raising item invocation.");
        Assert.Contains("ActivePopups.Add(popup)", popupMenuService, StringComparison.Ordinal);
        Assert.Contains("ActivePopups.Remove(popup)", popupMenuService, StringComparison.Ordinal);
        Assert.Contains("PlacementTarget = owner", popupMenuService, StringComparison.Ordinal);
        Assert.Contains("ResolvePopupHost(owner)", popupMenuService, StringComparison.Ordinal);
        Assert.Contains("host.Children.Add(popup)", popupMenuService, StringComparison.Ordinal);
        Assert.Contains("host.Children.Remove(popup)", popupMenuService, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"MAAUnified.App.Controls.AppPopupMenuPresenter\"", presenter, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-popup-menu-item-shell\"", presenter, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-popup-menu-separator\"", presenter, StringComparison.Ordinal);

        Assert.Contains("Style Selector=\"controls|AppPopupMenu\"", interactionStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.app-popup-menu-panel\"", interactionStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.app-popup-menu-item-shell:pointerover\"", interactionStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"Border.app-popup-menu-separator\"", interactionStyles, StringComparison.Ordinal);
        Assert.Contains("MAA.App.Interaction.FloatingMenuPadding", interactionStyles, StringComparison.Ordinal);
        Assert.Contains("MAA.App.Interaction.FloatingMenuItemMinHeight", interactionStyles, StringComparison.Ordinal);

        Assert.Contains("AppTextEditingMenu.Register()", appCode, StringComparison.Ordinal);
        Assert.Contains("InputElement.PointerPressedEvent.AddClassHandler<TextBox>", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("InputElement.PointerReleasedEvent.AddClassHandler<TextBox>", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("Control.ContextRequestedEvent.AddClassHandler<TextBox>", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Tunnel | RoutingStrategies.Bubble", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("PlacementMode.Pointer", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("textBox.Cut()", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("textBox.Copy()", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("textBox.Paste()", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("textBox.SelectAll()", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("Common.Edit.Cut", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("Common.Edit.Copy", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("Common.Edit.Paste", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("Common.Edit.SelectAll", textEditingMenu, StringComparison.Ordinal);
        Assert.Contains("controls:AppTextEditingMenu.IsEnabled=\"False\"", hotkeys, StringComparison.Ordinal);
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
        Assert.Contains("<x:Double x:Key=\"MAA.Size.TaskQueue.LogPanelWidth\">440</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Settings.SectionListWidth\">224</x:Double>", text, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.Size.Copilot.SidePanelWidth\">546</x:Double>", text, StringComparison.Ordinal);
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
        Assert.Contains("Resized += OnWindowResized;", text, StringComparison.Ordinal);
        Assert.Contains("var metrics = CalculateResponsiveLayoutMetrics(width", text, StringComparison.Ordinal);
        Assert.Contains("ApplyResponsiveLayoutMetrics(metrics)", text, StringComparison.Ordinal);
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
        var settingsShellStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "SettingsShellStyles.axaml"));
        var copilot = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));

        Assert.Contains("Width=\"{DynamicResource MAA.Size.TaskQueue.ListPanelWidth}\"", taskQueue, StringComparison.Ordinal);
        Assert.Contains("Width=\"{DynamicResource MAA.Size.TaskQueue.LogPanelWidth}\"", taskQueue, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-rail\"", settings, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"{DynamicResource MAA.Size.Settings.SectionListWidth}\" />", settingsShellStyles, StringComparison.Ordinal);
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
    public void TaskQueueView_StatusColors_ShouldSurviveSelectedAndPointerOverStates()
    {
        var root = GetMaaUnifiedRoot();
        var taskQueuePath = Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml");
        var text = File.ReadAllText(taskQueuePath);
        var selectionListStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppSelectionListStyles.axaml"));

        Assert.Contains("Style Selector=\"controls|AppSelectionList.selection-list-section-cards ListBoxItem:pointerover Border.app-selection-list-item-shell\"", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"controls|AppSelectionList.selection-list-section-cards ListBoxItem:selected Border.app-selection-list-item-shell\"", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"controls|AppSelectionList.selection-list-section-cards ListBoxItem:selected:pointerover Border.app-selection-list-item-shell\"", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{DynamicResource MAA.Brush.App.Surface.Section}\" />", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderBrush\" Value=\"Transparent\" />", selectionListStyles, StringComparison.Ordinal);

        foreach (var status in new[] { "running", "success", "error", "skipped", "idle" })
        {
            Assert.Contains($"Classes.status-{status}=\"{{Binding IsStatus{char.ToUpperInvariant(status[0])}{status[1..]}}}\"", text, StringComparison.Ordinal);
            Assert.Contains($"Style Selector=\"Border.task-queue-item-card.status-{status}\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TaskQueueView_ShouldBindCanEditAndPrimaryRunToggle()
    {
        var root = GetMaaUnifiedRoot();
        var taskQueuePath = Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml");
        var text = File.ReadAllText(taskQueuePath);

        Assert.Contains("<controls:AppSelectionList x:Name=\"TaskListBox\"", text, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"0\"", text, StringComparison.Ordinal);
        Assert.Contains("Classes=\"task-queue-list selection-list-section-cards selection-list-no-indicator\"", text, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"controls|AppSelectionList.task-queue-list Border.task-queue-item-card\"", text, StringComparison.Ordinal);
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
        var hintedCheckBox = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppHintedCheckBox.axaml"));
        var hintedCheckBoxCodeBehind = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppHintedCheckBox.axaml.cs"));

        Assert.Contains("ToolTip.ShowDelay=\"500\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ToolTip.Tip>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Popup", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasTip, ElementName=Root}\"", hintedCheckBox, StringComparison.Ordinal);
        Assert.Contains("CheckBoxIsEnabled", hintedCheckBox, StringComparison.Ordinal);
        Assert.Contains("public bool HasTip", hintedCheckBoxCodeBehind, StringComparison.Ordinal);
        Assert.Contains("HasTip = !string.IsNullOrWhiteSpace(Tip);", hintedCheckBoxCodeBehind, StringComparison.Ordinal);
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
            var scrubbedText = Regex.Replace(
                text,
                "ColumnDefinitions=\"Auto,24,\\*\"",
                "ColumnDefinitions=\"{DynamicResource MAA.Layout.Settings.Columns}\"",
                RegexOptions.Compiled);
            scrubbedText = Regex.Replace(
                scrubbedText,
                "Margin=\"0,4,0,0\"|Margin=\"0,4,12,0\"|Spacing=\"28\"",
                "Margin=\"{DynamicResource MAA.Thickness.SettingsShell}\"",
                RegexOptions.Compiled);
            Assert.DoesNotMatch(sizePattern, scrubbedText);
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
                    "Classes=\"[^\"]*\\b(?:wpf-button|app-button|grouped-card-footer-action|achievement-toast-close|copilot-link)\\b[^\"]*\"",
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
